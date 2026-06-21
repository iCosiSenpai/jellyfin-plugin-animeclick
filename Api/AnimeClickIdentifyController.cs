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
    private readonly AnimeClickAniListResolver _aniListResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeClickIdentifyController> _logger;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickClient client,
        AnimeClickAniListResolver aniListResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<AnimeClickIdentifyController> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _client = client;
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
        item.SetProviderId(ProviderKey, request.AnimeClickId);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "AnimeClick IdentifyAndRefresh: item {ItemId} ({Name}) set AnimeClick='{NewId}' (was '{OldId}'), replaceAllImages={ReplaceAll}",
            item.Id, item.Name, request.AnimeClickId, previousId ?? "<none>", request.ReplaceAllImages);

        // ── Optional: wipe existing remote images so ImageFetchers can re-download ──
        int deletedImages = 0;
        if (request.ReplaceAllImages)
        {
            deletedImages = await WipeRemoteImagesAsync(item, cancellationToken).ConfigureAwait(false);
        }

        // ── Ensure AniList/TMDB/IMDB provider IDs are populated so ImageFetchers
        // can do their lookup. AnimeClick does not provide TMDB/IMDB IDs but
        // we can fetch them by title via the AniList GraphQL API.
        var anilistIdFound = await EnsureAniListIdAsync(item, cancellationToken).ConfigureAwait(false);
        if (anilistIdFound is not null)
        {
            _logger.LogInformation(
                "AnimeClick IdentifyAndRefresh: ensured AniList ID={AniListId} for {ItemId} ({Name})",
                anilistIdFound, item.Id, item.Name);
        }

        // ── Always fetch the best remote images from each enabled ImageFetcher ──
        // Jellyfin 10.11.x with ReplaceAllMetadata=true does NOT reliably call
        // every metadata provider and does NOT always re-download remote images,
        // so we do it explicitly here. The wipe above ensures we can save
        // fresh images over the existing slots.
        var downloadedImages = await DownloadBestRemoteImagesAsync(item, cancellationToken).ConfigureAwait(false);

        // ── Trigger full metadata refresh (text + cast + tags + …) ──
        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(BaseItem.FileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = request.ReplaceAllMetadata,
            ReplaceAllImages = request.ReplaceAllImages,
            EnableRemoteContentProbe = true,
            ForceSave = true
        };

        bool refreshOk = false;
        string? refreshError = null;
        try
        {
            await item.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
            refreshOk = true;
            _logger.LogInformation(
                "AnimeClick IdentifyAndRefresh: full metadata refresh completed for {ItemId} ({Name})",
                item.Id, item.Name);
        }
        catch (Exception ex)
        {
            refreshError = ex.Message;
            _logger.LogError(ex,
                "AnimeClick IdentifyAndRefresh: refresh failed for {ItemId} ({Name})",
                item.Id, item.Name);
        }

        return Ok(new IdentifyAndRefreshResponse
        {
            Success = refreshOk,
            ItemId = item.Id.ToString(),
            Name = item.Name,
            AnimeClickId = request.AnimeClickId,
            PreviousAnimeClickId = previousId,
            RefreshTriggered = true,
            ReplaceAllImages = request.ReplaceAllImages,
            DeletedImages = deletedImages,
            DownloadedImages = downloadedImages,
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
            IncludeDisabledProviders = true
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
                    IncludeDisabledProviders = true
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
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AnimeClick IdentifyAndRefresh: failed to save {Type} from {Provider} ({Url}) for {ItemId}",
                            type, cand.Img.ProviderName, cand.Img.Url, item.Id);
                    }
                }
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
    /// If the item doesn't have an AniList provider ID yet, queries the
    /// AniList GraphQL API by title and stores the resulting AniList ID on
    /// the item so the AniList ImageFetcher can do its lookup. This is
    /// best-effort: returns the new AniList ID on success, null on failure.
    /// </summary>
    private async Task<string?> EnsureAniListIdAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var existing = item.GetProviderId("AniList");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var anilistId = await _aniListResolver.ResolveAniListIdAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(anilistId))
        {
            return null;
        }

        item.SetProviderId("AniList", anilistId);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
    public bool ReplaceAllMetadata { get; set; } = true;

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
