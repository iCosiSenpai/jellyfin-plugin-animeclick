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
/// Administrative identification. The selected card is verified before saving and Jellyfin
/// owns the background refresh, provider ordering and optional image replacement.
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
    private readonly MediaBrowser.Model.IO.IFileSystem _fileSystem;
    private readonly ILogger<AnimeClickIdentifyController> _logger;
    private readonly AnimeClickCommunityService? _community;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickClient client,
        AnimeClickHtmlParser parser,
        MediaBrowser.Model.IO.IFileSystem fileSystem,
        ILogger<AnimeClickIdentifyController> logger,
        AnimeClickCommunityService? community = null)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _client = client;
        _parser = parser;
        _fileSystem = fileSystem;
        _logger = logger;
        _community = community;
    }

    /// <summary>Validates the selected work, saves its ID, then lets Jellyfin refresh in the background.</summary>
    [HttpPost("IdentifyAndRefresh")]
    public async Task<ActionResult<IdentifyAndRefreshResponse>> IdentifyAndRefresh(
        [FromBody] IdentifyAndRefreshRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
            return BadRequest(new { error = "Seleziona un elemento Jellyfin valido." });
        if (!AnimeClickClient.TryNormalizeAnimeInput(request.AnimeClickId, out var animeClickId))
            return BadRequest(new { error = "Inserisci un ID AnimeClick o il link alla scheda dell’anime." });

        var item = _libraryManager.GetItemById(itemId);
        if (item is null) return NotFound(new { error = "L’elemento non è più presente nella libreria." });
        if (item is not (Movie or Series or Season))
            return BadRequest(new { error = "Seleziona un film, una serie o una stagione. Gli episodi usano l’identità della serie." });
        if (item.IsLocked)
            return Conflict(new { error = "La scheda è bloccata in Jellyfin. Sbloccala prima di identificarla." });
        var previousId = item.GetProviderId(ProviderKey);

        // No mutation until AnimeClick confirms a real detail page. HTTP 200 alone can be an ad.
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        try
        {
            var url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
            var html = await _client.GetStringAsync(url, configuration, cancellationToken).ConfigureAwait(false);
            _parser.ParseAnimePage(url, html);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick identification could not validate the selected page");
            return StatusCode(502, new { error = "Impossibile verificare la scheda AnimeClick. Nessuna modifica salvata: controlla il link e riprova." });
        }

        // The item may have been locked while the network request was in flight.
        item = _libraryManager.GetItemById(itemId);
        if (item is null) return NotFound();
        if (item.IsLocked) return Conflict(new { error = "La scheda è stata bloccata durante la verifica." });
        if (!string.Equals(previousId, item.GetProviderId(ProviderKey), StringComparison.Ordinal))
            return Conflict(new { error = "L’abbinamento è cambiato durante la verifica. Ricarica la scheda e riprova." });
        item.SetProviderId(ProviderKey, animeClickId);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        string? communityMessage = null;
        if (_community is not null)
        {
            try { communityMessage = await _community.EnqueueCorrectionAsync(item, cancellationToken).ConfigureAwait(false); }
            catch { communityMessage = "La correzione locale è salvata; l’invio alla comunità non è stato accodato."; }
        }
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = request.ReplaceAllImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
            ReplaceAllMetadata = request.ReplaceAllMetadata,
            ReplaceAllImages = request.ReplaceAllImages,
            ForceSave = true
        };
        try
        {
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnimeClick identification saved but refresh could not be queued for {ItemId}", item.Id);
            return Ok(new IdentifyAndRefreshResponse
            {
                ItemId = item.Id.ToString(), AnimeClickId = animeClickId, PreviousAnimeClickId = previousId,
                CommunityMessage = communityMessage,
                Error = "L’abbinamento è salvato, ma l’aggiornamento non è partito. Usa «Aggiorna metadati» nella scheda Jellyfin."
            });
        }
        return Ok(new IdentifyAndRefreshResponse
        {
            Success = true, ItemId = item.Id.ToString(), Name = item.Name, AnimeClickId = animeClickId,
            PreviousAnimeClickId = previousId, RefreshTriggered = true, ReplaceAllImages = request.ReplaceAllImages,
            CommunityMessage = communityMessage
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
        if (!Guid.TryParse(itemId, out var parsedItemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        var item = _libraryManager.GetItemById(parsedItemId);
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
        if (!Guid.TryParse(itemId, out var parsedItemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        var item = _libraryManager.GetItemById(parsedItemId);
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
    /// Requests image replacement through Jellyfin's refresh flow. The plugin itself
    /// never deletes artwork or overrides the administrator's image provider order.
    /// </summary>
    public bool ReplaceAllImages { get; set; } = false;
}

public sealed class IdentifyAndRefreshResponse
{
    public string? CommunityMessage { get; set; }
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
