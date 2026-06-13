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
/// The endpoints here are equivalent to "Save AND Refresh" in a single
/// call: they persist the AnimeClick ID on the item and immediately
/// trigger a full metadata refresh.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public class AnimeClickIdentifyController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly AnimeClickClient _client;
    private readonly ILogger<AnimeClickIdentifyController> _logger;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        AnimeClickClient client,
        ILogger<AnimeClickIdentifyController> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Persists the AnimeClick provider ID on the given item and triggers a
    /// full metadata refresh so the title, overview, cast, etc. are
    /// populated immediately.
    /// </summary>
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
            "AnimeClick IdentifyAndRefresh: item {ItemId} ({Name}) set AnimeClick='{NewId}' (was '{OldId}')",
            item.Id, item.Name, request.AnimeClickId, previousId ?? "<none>");

        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(BaseItem.FileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = request.ReplaceAllMetadata,
            EnableRemoteContentProbe = true,
            ForceSave = true
        };

        try
        {
            await item.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "AnimeClick IdentifyAndRefresh: full metadata refresh completed for {ItemId} ({Name})",
                item.Id, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AnimeClick IdentifyAndRefresh: refresh failed for {ItemId} ({Name})",
                item.Id, item.Name);
            return StatusCode(500, new IdentifyAndRefreshResponse
            {
                Success = false,
                ItemId = item.Id.ToString(),
                Name = item.Name,
                AnimeClickId = request.AnimeClickId,
                PreviousAnimeClickId = previousId,
                RefreshTriggered = true,
                Error = ex.Message
            });
        }

        return Ok(new IdentifyAndRefreshResponse
        {
            Success = true,
            ItemId = item.Id.ToString(),
            Name = item.Name,
            AnimeClickId = request.AnimeClickId,
            PreviousAnimeClickId = previousId,
            RefreshTriggered = true
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
}

public sealed class IdentifyAndRefreshRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string AnimeClickId { get; set; } = string.Empty;
    public bool ReplaceAllMetadata { get; set; } = true;
}

public sealed class IdentifyAndRefreshResponse
{
    public bool Success { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? AnimeClickId { get; set; }
    public string? PreviousAnimeClickId { get; set; }
    public bool RefreshTriggered { get; set; }
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
