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
    private readonly ILogger<AnimeClickIdentifyController> _logger;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickClient client,
        ILogger<AnimeClickIdentifyController> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _client = client;
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

        var query = new RemoteImageQuery(providerName: string.Empty)
        {
            ImageType = type is null ? null : ParseImageType(type)
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
