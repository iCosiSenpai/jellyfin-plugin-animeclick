using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Api;

/// <summary>
/// Custom endpoints to work around Jellyfin 10.11.x's lack of automatic
/// metadata refresh after <c>SetProviderId</c> (Identify → Save).
///
/// The built-in <c>POST /Items/RemoteSearch/Apply</c> only persists the new
/// provider ID; it does NOT call <c>RefreshSingleItem</c> on the item.
/// This means a user that identifies an item via AnimeClick sees a brief
/// spinner, the spinner ends, and the metadata (title, overview, cast, …)
/// stays empty/old until the user manually clicks "Refresh &amp; replace".
///
/// The endpoints here are equivalent to "Save AND Refresh" (and optionally
/// "Replace all images") in a single call: they persist the AnimeClick ID
/// on the item, optionally wipe existing remote images so new ones can be
/// downloaded by the configured ImageFetchers (Fanart, AniList, TMDB, …),
/// and immediately trigger a full metadata refresh.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public class AnimeClickIdentifyController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly AnimeClickClient _client;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickAniListResolver _aniListResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeClickIdentifyController> _logger;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickClient client,
        AnimeClickHtmlParser parser,
        AnimeClickAniListResolver aniListResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<AnimeClickIdentifyController> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _client = client;
        _parser = parser;
        _aniListResolver = aniListResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Persists the AnimeClick provider ID on the given item and triggers a
    /// full metadata refresh so the title, overview, cast, etc. are
    /// populated immediately.
    /// </summary>
    /// <remarks>
    /// With <c>ReplaceAllImages = true</c> the controller also wipes the
    /// existing remote-fetched images (primary, backdrop, logo, art, …) so
    /// that the configured <c>ImageFetchers</c> (Fanart, AniList, TMDB,
    /// OMDb, Embedded Image Extractor, Screen Grabber) can re-download
    /// higher quality covers. The AnimeClick plugin itself does NOT download
    /// images by design — it only provides Japanese/Italian text metadata.
    /// </remarks>
    [HttpPost("IdentifyAndRefresh")]
    public async Task<ActionResult<IdentifyAndRefreshResponse>> IdentifyAndRefresh(
        [FromBody] IdentifyAndRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ItemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        if (string.IsNullOrWhiteSpace(request.AnimeClickId))
        {
            return BadRequest(new { error = "animeClickId is required" });
        }

        if (!AnimeClickClient.TryNormalizeAnimeClickId(request.AnimeClickId, out var animeClickId))
        {
            return BadRequest(new { error = "animeClickId must be numeric or use the 'number/slug' format" });
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound(new { error = $"Item '{request.ItemId}' not found" });
        }

        if (item is not (Movie or Series or Episode or Season))
        {
            return BadRequest(new { error = $"Item '{request.ItemId}' is type '{item.GetType().Name}', not Movie/Series/Episode/Season" });
        }

        var previousId = item.GetProviderId(ProviderKey);
        item.SetProviderId(ProviderKey, animeClickId);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "AnimeClick IdentifyAndRefresh: item {ItemId} ({Name}) set AnimeClick='{NewId}' (was '{OldId}'), replaceAllImages={ReplaceAll}",
            item.Id, item.Name, animeClickId, previousId ?? "<none>", request.ReplaceAllImages);

        // Wrap the entire downstream flow (image wipe, AniList lookup,
        // image download, metadata refresh) in a 30-second hard cap so the
        // user doesn't face an infinite spinner on the first try when one
        // of the upstream APIs is slow. The state already persisted on the
        // item above (SetProviderId + UpdateItemAsync) will be picked up by
        // a later scheduled refresh even if the timeout fires.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var linkedToken = cts.Token;

        int deletedImages = 0;
        List<string>? downloadedImages = null;
        string? refreshError = null;
        bool timedOut = false;
        bool refreshTriggered = false;

        try
        {
            // ── Optional: replace remote images only when explicitly requested. ──
            if (request.ReplaceAllImages)
            {
                deletedImages = await WipeRemoteImagesAsync(item, linkedToken).ConfigureAwait(false);
            }

            // AniList IDs belong to the anime work, not to an individual season/episode.
            // Persist one only on Series/Movie items so manual identify cannot attach a
            // random anime ID to an episode title.
            if (item is Series or Movie)
            {
                var anilistIdFound = await EnsureAniListIdAsync(item, linkedToken).ConfigureAwait(false);
                if (anilistIdFound is not null)
                {
                    _logger.LogInformation(
                        "AnimeClick IdentifyAndRefresh: ensured AniList ID={AniListId} for {ItemId} ({Name})",
                        anilistIdFound, item.Id, item.Name);
                }
            }

            // Saving images at index zero can replace user-curated artwork. Do it only
            // behind the explicit ReplaceAllImages option advertised by the UI/README.
            if (request.ReplaceAllImages)
            {
                downloadedImages = await DownloadBestRemoteImagesAsync(item, linkedToken).ConfigureAwait(false);
            }

            // ── Trigger full metadata refresh (text + cast + tags + …) ──
            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(BaseItem.FileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = request.ReplaceAllMetadata,
                ReplaceAllImages = request.ReplaceAllImages,
                EnableRemoteContentProbe = true,
                ForceSave = true
            };

            try
            {
                refreshTriggered = true;
                await item.RefreshMetadata(refreshOptions, linkedToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "AnimeClick IdentifyAndRefresh: full metadata refresh completed for {ItemId} ({Name})",
                    item.Id, item.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (Exception ex)
            {
                refreshError = ex.Message;
                _logger.LogError(ex,
                    "AnimeClick IdentifyAndRefresh: refresh failed for {ItemId} ({Name})",
                    item.Id, item.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (Exception ex)
        {
            refreshError = ex.Message;
            _logger.LogError(ex,
                "AnimeClick IdentifyAndRefresh: error during identify flow for {ItemId} ({Name})",
                item.Id, item.Name);
        }

        if (timedOut)
        {
            refreshError = "Timeout dopo 30 secondi. La serie è stata identificata; riprova per completare refresh immagini e metadati. "
                + "I metadati basic sono stati già salvati sul db.";
            _logger.LogWarning(
                "AnimeClick IdentifyAndRefresh: timeout (30s) for {ItemId} ({Name}) — partial completion, retry to finish",
                item.Id, item.Name);
        }

        return Ok(new IdentifyAndRefreshResponse
        {
            Success = !timedOut && refreshError is null,
            ItemId = item.Id.ToString(),
            Name = item.Name,
            AnimeClickId = animeClickId,
            PreviousAnimeClickId = previousId,
            RefreshTriggered = refreshTriggered,
            ReplaceAllImages = request.ReplaceAllImages,
            DeletedImages = deletedImages,
            DownloadedImages = downloadedImages ?? new List<string>(),
            Error = refreshError
        });
    }

    /// <summary>
    /// Diagnostic helper: returns whether the item currently has an
    /// AnimeClick provider ID. Useful for the "Identify &amp; Refresh"
    /// button in the plugin config page.
    /// </summary>
    [HttpGet("IdentifyStatus")]
    public ActionResult<IdentifyStatusResponse> IdentifyStatus(
        [FromQuery] string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = $"Item '{itemId}' not found" });
        }

        var id = item.GetProviderId(ProviderKey);
        return Ok(new IdentifyStatusResponse
        {
            ItemId = item.Id.ToString(),
            Name = item.Name,
            TypeName = item.GetType().Name,
            AnimeClickId = id,
            HasAnimeClickId = !string.IsNullOrWhiteSpace(id)
        });
    }

    /// <summary>
    /// Lists remote images available from each enabled ImageFetcher for
    /// the given item. Used by the configPage to let the user preview what
    /// the next refresh will pick up.
    /// </summary>
    [HttpGet("AvailableRemoteImages")]
    public async Task<ActionResult<RemoteImagesResponse>> AvailableRemoteImages(
        [FromQuery] string itemId,
        [FromQuery] string? type,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = $"Item '{itemId}' not found" });
        }

        var query = new RemoteImageQuery(providerName: (string)null!)
        {
            ImageType = type is null ? null : ParseImageType(type),
            IncludeDisabledProviders = false
        };

        var images = await _providerManager.GetAvailableRemoteImages(item, query, cancellationToken).ConfigureAwait(false);

        return Ok(new RemoteImagesResponse
        {
            ItemId = item.Id.ToString(),
            Count = images.Count(),
            Images = images.Select(i => new RemoteImageInfo
            {
                ProviderName = i.ProviderName,
                Type = i.Type.ToString(),
                Url = i.Url,
                Width = i.Width ?? 0,
                Height = i.Height ?? 0,
                Language = i.Language ?? string.Empty,
                CommunityRating = (float)(i.CommunityRating ?? 0)
            }).ToList()
        });
    }

    /// <summary>
    /// Removes all remote (non-local) images from the given item so that
    /// the next refresh can re-download from the configured ImageFetchers.
    /// Local <c>folder.jpg</c>, <c>poster.jpg</c> and <c>backdrop.jpg</c> in
    /// the item's media folder are preserved (they're the user's choice).
    /// </summary>
    private async Task<int> WipeRemoteImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        // Iterate every supported image type. We snapshot the list before
        // deletion because the ImageInfos collection may be mutated as we
        // call DeleteImageAsync.
        var supportedTypes = new[]
        {
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Logo,
            ImageType.Art,
            ImageType.Banner,
            ImageType.Thumb,
            ImageType.Disc,
            ImageType.Box,
            ImageType.BoxRear
        };

        int deleted = 0;
        foreach (var type in supportedTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var images = item.GetImages(type)?.ToList() ?? new List<ItemImageInfo>();
            if (images.Count == 0)
            {
                continue;
            }

            // Enumerate by index from the END so deleting doesn't shift
            // the indices of the remaining items.
            for (int i = images.Count - 1; i >= 0; i--)
            {
                var info = images[i];
                if (info is null)
                {
                    continue;
                }

                if (info.IsLocalFile)
                {
                    // Preserve the user's local folder.jpg / poster.jpg / backdrop.jpg.
                    continue;
                }

                try
                {
                    var idx = item.GetImageIndex(info);
                    await item.DeleteImageAsync(type, idx).ConfigureAwait(false);
                    deleted++;
                    _logger.LogDebug(
                        "AnimeClick IdentifyAndRefresh: deleted remote image type={Type} index={Index} path={Path} for {ItemId}",
                        type, idx, info.Path, item.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "AnimeClick IdentifyAndRefresh: failed to delete image type={Type} index={Index} for {ItemId}",
                        type, i, item.Id);
                }
            }
        }

        if (deleted > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("AnimeClick IdentifyAndRefresh: wiped {Count} remote image(s) from {ItemId}", deleted, item.Id);
        }
        else
        {
            _logger.LogInformation("AnimeClick IdentifyAndRefresh: no remote images to wipe for {ItemId}", item.Id);
        }

        return deleted;
    }

    /// <summary>
    /// For each supported image type, queries the enabled ImageFetchers
    /// (Fanart, AniList, TheMovieDb, OMDb, …) and downloads the best
    /// available remote image for that type, picking in the order:
    /// Fanart > AniList > TheMovieDb > The Open Movie Database.
    /// Returns a list of "type:providerName:url" entries for diagnostics.
    /// </summary>
    private async Task<List<string>> DownloadBestRemoteImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var downloaded = new List<string>();
        // Priority order for the best provider. We don't trust the
        // Enabled state in Jellyfin (Fanart may be configured but the
        // API key may be missing), so we try each provider in order
        // and accept the first that returns a URL.
        var priorityOrder = new[] { "Fanart", "AniList", "TheMovieDb", "The Open Movie Database", "Embedded Image Extractor" };

        // We need to download: 1 Primary, up to 3 Backdrops, 1 Logo, 1 Art, 1 Thumb.
        var typesToFetch = new (ImageType Type, int MaxCount)[]
        {
            (ImageType.Primary, 1),
            (ImageType.Backdrop, 3),
            (ImageType.Logo, 1),
            (ImageType.Art, 1),
            (ImageType.Thumb, 1)
        };

        foreach (var (type, maxCount) in typesToFetch)
        {
            try
            {
                var query = new RemoteImageQuery(providerName: (string)null!)
                {
                    ImageType = type,
                    IncludeDisabledProviders = false
                };
                var candidates = (await _providerManager.GetAvailableRemoteImages(item, query, cancellationToken).ConfigureAwait(false)).ToList();
                if (candidates.Count == 0)
                {
                    _logger.LogInformation("AnimeClick IdentifyAndRefresh: no remote images available for {Type} on {ItemId}", type, item.Id);
                    continue;
                }

                // Sort by provider priority: lower index wins. Ties broken
                // by CommunityRating desc, then by Width*Height desc (bigger is better).
                var ordered = candidates
                    .Select((img, idx) => new
                    {
                        Img = img,
                        ProviderPriority = IndexOfProvider(priorityOrder, img.ProviderName),
                        OriginalIndex = idx
                    })
                    .OrderBy(x => x.ProviderPriority < 0 ? int.MaxValue : x.ProviderPriority)
                    .ThenByDescending(x => x.Img.CommunityRating)
                    .ThenByDescending(x => (long)(x.Img.Width ?? 0) * (x.Img.Height ?? 0))
                    .ToList();

                int saved = 0;
                foreach (var cand in ordered)
                {
                    if (saved >= maxCount)
                    {
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(cand.Img.Url))
                    {
                        continue;
                    }

                    try
                    {
                        // Compute the index: Primary/Logo/Art/Thumb/Disc are single-slot
                        // (index 0), Backdrop is multi-slot (0,1,2,…)
                        var imageIndex = type == ImageType.Backdrop ? saved : 0;
                        await _providerManager.SaveImage(item, cand.Img.Url, type, imageIndex, cancellationToken).ConfigureAwait(false);
                        saved++;
                        downloaded.Add($"{type}:{cand.Img.ProviderName}:{cand.Img.Url}");
                        _logger.LogInformation(
                            "AnimeClick IdentifyAndRefresh: saved remote {Type} from {Provider} ({Width}x{Height}) for {ItemId}",
                            type, cand.Img.ProviderName, cand.Img.Width, cand.Img.Height, item.Id);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AnimeClick IdentifyAndRefresh: failed to save {Type} from {Provider} ({Url}) for {ItemId}",
                            type, cand.Img.ProviderName, cand.Img.Url, item.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AnimeClick IdentifyAndRefresh: error fetching {Type} for {ItemId}",
                    type, item.Id);
            }
        }

        return downloaded;
    }

    private static int IndexOfProvider(string[] priority, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }
        for (var i = 0; i < priority.Length; i++)
        {
            if (string.Equals(priority[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Preserves an existing AniList provider ID. Otherwise asks the validated
    /// resolver to match the already-persisted AnimeClick work by title, year
    /// and media format before storing a new ID for artwork providers.
    /// </summary>
    private async Task<string?> EnsureAniListIdAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var existing = item.GetProviderId("AniList");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var animeClickId = item.GetProviderId(ProviderKey);
        if (string.IsNullOrWhiteSpace(animeClickId) || item is not (Series or Movie))
        {
            return null;
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var animeUrl))
        {
            return null;
        }

        string? anilistId;
        try
        {
            var html = await _client
                .GetStringAsync(animeUrl, configuration, cancellationToken)
                .ConfigureAwait(false);
            var selectedAnime = _parser.ParseAnimePage(animeUrl, html);
            anilistId = await _aniListResolver.ResolveAniListIdAsync(
                    selectedAnime.Id,
                    selectedAnime.OriginalTitle ?? selectedAnime.Title,
                    selectedAnime.Title,
                    selectedAnime.ProductionYear,
                    seriesRequest: item is Series,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AnimeClick IdentifyAndRefresh: AniList validation failed for selected AnimeClick ID {AnimeClickId}",
                animeClickId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(anilistId))
        {
            return null;
        }

        item.SetProviderId("AniList", anilistId);
        await _libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken)
            .ConfigureAwait(false);
        return anilistId;
    }

    private static ImageType? ParseImageType(string s)
    {
        if (Enum.TryParse<ImageType>(s, ignoreCase: true, out var t))
        {
            return t;
        }
        return null;
    }
}

public sealed class IdentifyAndRefreshRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string AnimeClickId { get; set; } = string.Empty;
    public bool ReplaceAllMetadata { get; set; } = false;

    /// <summary>
    /// When true, all existing remote (non-local) images for the item are
    /// deleted before the refresh so the configured ImageFetchers can
    /// re-download higher quality covers. Defaults to false to preserve
    /// any user-curated artwork unless explicitly requested.
    /// </summary>
    public bool ReplaceAllImages { get; set; } = false;
}

public sealed class IdentifyAndRefreshResponse
{
    public bool Success { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? AnimeClickId { get; set; }
    public string? PreviousAnimeClickId { get; set; }
    public bool RefreshTriggered { get; set; }
    public bool ReplaceAllImages { get; set; }
    public int DeletedImages { get; set; }
    public List<string> DownloadedImages { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class IdentifyStatusResponse
{
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string? AnimeClickId { get; set; }
    public bool HasAnimeClickId { get; set; }
}

public sealed class RemoteImagesResponse
{
    public string ItemId { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<RemoteImageInfo> Images { get; set; } = [];
}

public sealed class RemoteImageInfo
{
    public string ProviderName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Language { get; set; } = string.Empty;
    public float CommunityRating { get; set; }
}
