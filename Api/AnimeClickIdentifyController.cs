using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
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
    private readonly AnimeClickCacheService? _cache;
    private readonly AnimeClickRepairLedger? _repairLedger;

    public const string ProviderKey = "AnimeClick";

    public AnimeClickIdentifyController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickClient client,
        AnimeClickHtmlParser parser,
        MediaBrowser.Model.IO.IFileSystem fileSystem,
        ILogger<AnimeClickIdentifyController> logger,
        AnimeClickCommunityService? community = null,
        AnimeClickCacheService? cache = null,
        AnimeClickRepairLedger? repairLedger = null)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _client = client;
        _parser = parser;
        _fileSystem = fileSystem;
        _logger = logger;
        _community = community;
        _cache = cache;
        _repairLedger = repairLedger;
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
        if (item.IsLocked && !request.Unlock)
            return Conflict(new { error = "La scheda è bloccata in Jellyfin. Sbloccala prima di identificarla.", isLocked = true });
        var previousId = item.GetProviderId(ProviderKey);

        // No mutation until AnimeClick confirms a real detail page. HTTP 200 alone can be an ad.
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
        if (await ReadCardAsync(url, configuration, cancellationToken).ConfigureAwait(false) is null)
        {
            return StatusCode(502, new { error = "Impossibile verificare la scheda AnimeClick. Nessuna modifica salvata: controlla il link e riprova." });
        }

        // The item may have been locked while the network request was in flight.
        item = _libraryManager.GetItemById(itemId);
        if (item is null) return NotFound();
        if (item.IsLocked && !request.Unlock) return Conflict(new { error = "La scheda è stata bloccata durante la verifica.", isLocked = true });
        if (!string.Equals(previousId, item.GetProviderId(ProviderKey), StringComparison.Ordinal))
            return Conflict(new { error = "L’abbinamento è cambiato durante la verifica. Ricarica la scheda e riprova." });
        var unlockedCount = 0;
        if (request.Unlock)
        {
            unlockedCount = await UnlockItemAndDescendantsAsync(item, includeChildren: true, specificField: null, cancellationToken).ConfigureAwait(false);
        }
        item.SetProviderId(ProviderKey, animeClickId);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        await ResetStaleDescendantsAndLedgerAsync(item, previousId, animeClickId, cancellationToken).ConfigureAwait(false);
        AnimeClickCommunityOffer? communityOffer = null;
        if (_community is not null)
        {
            try { communityOffer = await _community.OfferAsync(item, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Every failure is absorbed, a cancelled request included: the ID is already saved,
                // and the refresh below must still be queued or the title would keep its old data.
                _logger.LogDebug(ex, "AnimeClick community offer failed for {ItemId}", item.Id);
                communityOffer = new AnimeClickCommunityOffer { Message = "La correzione locale è salvata; l’invio alla comunità non è stato accodato." };
            }
        }

        var communityMessage = communityOffer?.Message;
        var communityProposal = communityOffer?.Proposal;
        if (!TryQueueRefresh(item, request.ReplaceAllMetadata, request.ReplaceAllImages))
        {
            return Ok(new IdentifyAndRefreshResponse
            {
                ItemId = item.Id.ToString(), AnimeClickId = animeClickId, PreviousAnimeClickId = previousId,
                UnlockedCount = unlockedCount,
                CommunityMessage = communityMessage, CommunityProposal = communityProposal,
                Error = "L’abbinamento è salvato, ma l’aggiornamento non è partito. Usa «Aggiorna metadati» qui sotto."
            });
        }

        return Ok(new IdentifyAndRefreshResponse
        {
            Success = true, ItemId = item.Id.ToString(), Name = item.Name, AnimeClickId = animeClickId,
            PreviousAnimeClickId = previousId, RefreshTriggered = true, ReplaceAllImages = request.ReplaceAllImages,
            UnlockedCount = unlockedCount,
            CommunityMessage = communityMessage,
            CommunityProposal = communityProposal
        });
    }

    /// <summary>
    /// Reads the card the administrator is about to bind and says which library titles already use
    /// it, so a wrong pick or a duplicate is visible before anything is saved. Nothing is written to
    /// the library; a card read here is cached like any other, so identifying it right after costs
    /// AnimeClick no second visit beyond the verification itself.
    /// </summary>
    [HttpGet("PreviewCard")]
    public async Task<ActionResult<AnimeClickCardPreview>> PreviewCard(
        [FromQuery] string animeClickId,
        [FromQuery] string? itemId,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryNormalizeAnimeInput(animeClickId, out var normalizedId))
        {
            return BadRequest(new { error = "Inserisci un ID AnimeClick o il link alla scheda dell’anime." });
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, normalizedId, out var url))
        {
            return BadRequest(new { error = "L’indirizzo di AnimeClick nelle impostazioni non è valido." });
        }

        AnimeClickAnime? anime = null;
        if (_cache is not null)
        {
            anime = await _cache
                .GetAsync<AnimeClickAnime>(CardCacheKey(url), configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
        }

        if (anime is null || string.IsNullOrWhiteSpace(anime.Title))
        {
            anime = await ReadCardAsync(url, configuration, cancellationToken).ConfigureAwait(false);
            if (anime is null)
            {
                return StatusCode(502, new { error = "Non riesco a leggere questa scheda su AnimeClick: controlla il link o riprova fra poco." });
            }
        }

        var selected = Guid.TryParse(itemId, out var selectedItemId) ? _libraryManager.GetItemById(selectedItemId) : null;
        return Ok(new AnimeClickCardPreview
        {
            AnimeClickId = normalizedId,
            Url = url,
            Title = AnimeClickMetadataText.Title(anime.Title) ?? normalizedId,
            OriginalTitle = AnimeClickMetadataText.Title(anime.OriginalTitle),
            Year = anime.ProductionYear,
            Category = AnimeClickMetadataText.Title(anime.Category),
            Status = AnimeClickMetadataText.Title(anime.Status),
            EpisodeCount = anime.EpisodeCount,
            ImageUrl = AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, anime.ImageUrl, out var imageUri)
                && imageUri.Scheme == Uri.UriSchemeHttps
                    ? imageUri.AbsoluteUri
                    : null,
            Overview = AnimeClickMetadataText.Excerpt(AnimeClickMetadataText.Clean(anime.Overview), CardPreviewOverviewLength),
            Genres = [.. AnimeClickMetadataText.Labels(anime.Genres ?? []).Take(4)],
            UsedBy = FindItemsUsingCard(AnimeClickClient.GetStableIdentity(normalizedId), selectedItemId, selected)
        });
    }

    private static string CardCacheKey(string url) => "anime::" + url;

    /// <summary>
    /// Asks AnimeClick for a card and keeps it in the cache the providers read. Null when the page
    /// cannot be fetched or is not a card: HTTP 200 alone can be an advertisement or a maintenance
    /// page, and neither may be cached or bound to a title.
    /// </summary>
    private async Task<AnimeClickAnime?> ReadCardAsync(string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        AnimeClickAnime anime;
        try
        {
            var html = await _client.GetStringAsync(url, configuration, cancellationToken).ConfigureAwait(false);
            anime = _parser.ParseAnimePage(url, html);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick did not return a readable card for the selected page");
            return null;
        }

        if (_cache is not null && !string.IsNullOrWhiteSpace(anime.Title))
        {
            await _cache.SetAsync(CardCacheKey(url), anime, cancellationToken).ConfigureAwait(false);
        }

        return anime;
    }

    /// <summary>
    /// Other library titles already bound to this card. A second series on the same card is
    /// sometimes right (two cuts of one work) and often a mistake: the page says so and leaves the
    /// decision to the administrator. The selected title is not "another" one, and neither are its
    /// own series and seasons: the provider writes the series card on a season that has no card of
    /// its own, and that is the same work, not a duplicate.
    /// </summary>
    private List<AnimeClickCardUse> FindItemsUsingCard(string stableId, Guid selectedItemId, BaseItem? selected)
    {
        var selectedSeriesId = selected is Season selectedSeason ? selectedSeason.SeriesId : selectedItemId;
        try
        {
            return _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Season],
                    Recursive = true,
                    IsVirtualItem = false
                })
                .Where(candidate => candidate.Id != selectedItemId
                    && (selectedSeriesId == Guid.Empty
                        || (candidate.Id != selectedSeriesId && (candidate as Season)?.SeriesId != selectedSeriesId))
                    && AnimeClickClient.TryNormalizeAnimeClickId(candidate.GetProviderId(ProviderKey), out var bound)
                    && string.Equals(AnimeClickClient.GetStableIdentity(bound), stableId, StringComparison.OrdinalIgnoreCase))
                .Take(MaximumCardUses)
                .Select(candidate => new AnimeClickCardUse
                {
                    ItemId = candidate.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                    Name = candidate is Season season && !string.IsNullOrWhiteSpace(season.SeriesName)
                        ? season.SeriesName + " · " + season.Name
                        : candidate.Name ?? string.Empty,
                    Type = candidate switch { Movie => "Movie", Season => "Season", _ => "Series" }
                })
                .ToList();
        }
        catch (Exception ex)
        {
            // The list is a courtesy: failing to build it must not hide the card itself.
            _logger.LogDebug(ex, "AnimeClick card preview could not list the items using card {Card}", stableId);
            return [];
        }
    }

    private const int CardPreviewOverviewLength = 360;
    private const int MaximumCardUses = 5;

    /// <summary>
    /// Diagnostic helper: returns whether the item currently has an
    /// AnimeClick provider ID and whether its metadata is locked in Jellyfin.
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
            HasAnimeClickId = !string.IsNullOrWhiteSpace(id),
            IsLocked = item.IsLocked,
            LockedFields = item.LockedFields?.Select(field => field.ToString()).ToList() ?? []
        });
    }

    /// <summary>
    /// Unlocks a Jellyfin item (and optionally its child seasons/episodes) directly from the plugin
    /// so the administrator does not have to leave the current page.
    /// </summary>
    [HttpPost("UnlockItem")]
    public async Task<ActionResult<UnlockItemResponse>> UnlockItem(
        [FromBody] UnlockItemRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest(new { error = "Seleziona un elemento Jellyfin valido." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "L’elemento non è più presente nella libreria." });
        }

        // An unreadable field name must be refused: treating it as "no field" would remove every
        // lock of the item, which is more than the caller asked for.
        MetadataField? specificField = null;
        if (!string.IsNullOrWhiteSpace(request.Field))
        {
            if (!Enum.TryParse<MetadataField>(request.Field, ignoreCase: true, out var parsedField)
                || !Enum.IsDefined(parsedField))
            {
                return BadRequest(new { error = "Campo da sbloccare non riconosciuto." });
            }

            specificField = parsedField;
        }

        var unlocked = await UnlockItemAndDescendantsAsync(
            item,
            request.IncludeChildren,
            specificField,
            cancellationToken).ConfigureAwait(false);

        return Ok(new UnlockItemResponse
        {
            Success = true,
            ItemId = item.Id.ToString(),
            UnlockedCount = unlocked
        });
    }

    /// <summary>
    /// Triggers a full Jellyfin metadata refresh for one item directly from the plugin UI.
    /// </summary>
    [HttpPost("RefreshItem")]
    public async Task<ActionResult<RefreshItemResponse>> RefreshItem(
        [FromBody] RefreshItemRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest(new { error = "Seleziona un elemento Jellyfin valido." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "L’elemento non è più presente nella libreria." });
        }

        var unlocked = 0;
        if (request.Unlock)
        {
            unlocked = await UnlockItemAndDescendantsAsync(
                item,
                includeChildren: true,
                specificField: null,
                cancellationToken).ConfigureAwait(false);
        }

        _repairLedger?.Forget(item.Id);
        try
        {
            _repairLedger?.Forget(GetDescendants(item).Select(child => child.Id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: unable to clear descendant repair ledger for {ItemId}", item.Id);
        }

        if (!TryQueueRefresh(item, request.ReplaceAllMetadata, request.ReplaceAllImages))
        {
            return StatusCode(500, new { error = "Impossibile accodare l’aggiornamento dei metadati in Jellyfin." });
        }

        return Ok(new RefreshItemResponse
        {
            Success = true,
            ItemId = item.Id.ToString(),
            RefreshTriggered = true,
            UnlockedCount = unlocked
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
            Images = images.Select(i => new AnimeClickRemoteImageInfo
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


    private async Task ResetStaleDescendantsAndLedgerAsync(
        BaseItem item,
        string? previousId,
        string newAnimeClickId,
        CancellationToken cancellationToken)
    {
        _repairLedger?.Forget(item.Id);
        if (item is not (Series or Season))
        {
            return;
        }

        var previousStable = AnimeClickClient.TryNormalizeAnimeClickId(previousId, out var normalizedPrevious)
            ? AnimeClickClient.GetStableIdentity(normalizedPrevious)
            : string.Empty;
        var nextStable = AnimeClickClient.GetStableIdentity(newAnimeClickId);
        var identityChanged = !string.Equals(previousStable, nextStable, StringComparison.OrdinalIgnoreCase);

        try
        {
            var children = GetDescendants(item);
            if (children.Count == 0)
            {
                return;
            }

            _repairLedger?.Forget(children.Select(child => child.Id));
            if (!identityChanged)
            {
                return;
            }

            foreach (var child in children)
            {
                if (child.IsLocked || string.IsNullOrWhiteSpace(child.GetProviderId(ProviderKey)))
                {
                    continue;
                }

                child.ProviderIds.Remove(ProviderKey);
                await _libraryManager
                    .UpdateItemAsync(child, child.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: unable to reset descendant provider IDs for {ItemId}", item.Id);
        }
    }

    private async Task<int> UnlockItemAndDescendantsAsync(
        BaseItem item,
        bool includeChildren,
        MetadataField? specificField,
        CancellationToken cancellationToken)
    {
        var unlocked = 0;
        if (await TryUnlockSingleItemAsync(item, specificField, cancellationToken).ConfigureAwait(false))
        {
            unlocked++;
        }

        if (!includeChildren || item is not (Series or Season))
        {
            return unlocked;
        }

        try
        {
            foreach (var child in GetDescendants(item))
            {
                if (await TryUnlockSingleItemAsync(child, specificField, cancellationToken).ConfigureAwait(false))
                {
                    unlocked++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: unable to unlock descendant items for {ItemId}", item.Id);
        }

        return unlocked;
    }

    private async Task<bool> TryUnlockSingleItemAsync(
        BaseItem target,
        MetadataField? specificField,
        CancellationToken cancellationToken)
    {
        if (!await AnimeClickLocks.TryUnlockAsync(_libraryManager, target, specificField, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _repairLedger?.Forget(target.Id);
        return true;
    }

    /// <summary>The seasons and episodes of a series, or the episodes of a season; empty for anything else.</summary>
    private IReadOnlyList<BaseItem> GetDescendants(BaseItem item)
    {
        if (item is not (Series or Season))
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = item is Series
                ? [BaseItemKind.Season, BaseItemKind.Episode]
                : [BaseItemKind.Episode],
            Recursive = true,
            ParentId = item.Id,
            IsVirtualItem = false
        }) ?? [];
    }

    /// <summary>
    /// Hands the item to Jellyfin's own refresh queue. The plugin never deletes artwork itself:
    /// replacing images is a request to Jellyfin, which keeps the administrator's provider order.
    /// </summary>
    private bool TryQueueRefresh(BaseItem item, bool replaceAllMetadata, bool replaceAllImages)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = replaceAllImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
            ReplaceAllMetadata = replaceAllMetadata,
            ReplaceAllImages = replaceAllImages,
            ForceSave = true
        };
        try
        {
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnimeClick refresh could not be queued for {ItemId}", item.Id);
            return false;
        }
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

    /// <summary>
    /// Unlocks the target item (and locked child seasons/episodes) before saving the AnimeClick ID
    /// and queueing the refresh.
    /// </summary>
    public bool Unlock { get; set; } = false;
}

public sealed class IdentifyAndRefreshResponse
{
    public string? CommunityMessage { get; set; }

    /// <summary>With «Chiedi»: the exact public IDs «Condividi» would send for this correction.</summary>
    public AnimeClickCommunityMapping? CommunityProposal { get; set; }

    public bool Success { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? AnimeClickId { get; set; }
    public string? PreviousAnimeClickId { get; set; }
    public bool RefreshTriggered { get; set; }
    public bool ReplaceAllImages { get; set; }
    public int UnlockedCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>What the page shows of an AnimeClick card before it is bound to a library title.</summary>
public sealed class AnimeClickCardPreview
{
    public string AnimeClickId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public int? EpisodeCount { get; set; }
    public string? ImageUrl { get; set; }
    public string? Overview { get; set; }
    public List<string> Genres { get; set; } = [];

    /// <summary>Other library titles already bound to the same card.</summary>
    public List<AnimeClickCardUse> UsedBy { get; set; } = [];
}

public sealed class AnimeClickCardUse
{
    public string ItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Series";
}

public sealed class IdentifyStatusResponse
{
    public string ItemId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string? AnimeClickId { get; set; }
    public bool HasAnimeClickId { get; set; }
    public bool IsLocked { get; set; }
    public List<string> LockedFields { get; set; } = [];
}

public sealed class UnlockItemRequest
{
    public string ItemId { get; set; } = string.Empty;
    public bool IncludeChildren { get; set; } = true;
    public string? Field { get; set; }
}

public sealed class UnlockItemResponse
{
    public bool Success { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int UnlockedCount { get; set; }
}

public sealed class RefreshItemRequest
{
    public string ItemId { get; set; } = string.Empty;
    public bool ReplaceAllMetadata { get; set; } = false;
    public bool ReplaceAllImages { get; set; } = false;
    public bool Unlock { get; set; } = false;
}

public sealed class RefreshItemResponse
{
    public bool Success { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public bool RefreshTriggered { get; set; }
    public int UnlockedCount { get; set; }
}

public sealed class RemoteImagesResponse
{
    public string ItemId { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<AnimeClickRemoteImageInfo> Images { get; set; } = [];
}

public sealed class AnimeClickRemoteImageInfo
{
    public string ProviderName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Language { get; set; } = string.Empty;
    public float CommunityRating { get; set; }
}
