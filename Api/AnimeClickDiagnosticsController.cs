using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using AnimeClick.Plugin.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public class AnimeClickDiagnosticsController : ControllerBase
{
    private readonly AnimeClickSeriesSearchProvider _searchProvider;
    private readonly AnimeClickEpisodeListLoader _episodeListLoader;
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly AnimeClickEpisodeLayoutResolver _layoutResolver;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickTmdbClient _tmdbClient;
    private readonly AnimeClickAiTranslator _translator;
    private readonly AnimeClickTvdbClient _tvdbClient;
    private readonly AnimeClickMetadataFallbackService _fallbackService;
    private readonly AnimeClickTranslationQueue _translationQueue;
    private readonly AnimeClickLibraryQualityService _qualityService;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<AnimeClickDiagnosticsController> _logger;

    public AnimeClickDiagnosticsController(
        AnimeClickSeriesSearchProvider searchProvider,
        AnimeClickEpisodeListLoader episodeListLoader,
        AnimeClickSeasonResolver seasonResolver,
        AnimeClickEpisodeLayoutResolver layoutResolver,
        AnimeClickCacheService cache,
        AnimeClickTmdbClient tmdbClient,
        AnimeClickAiTranslator translator,
        AnimeClickTvdbClient tvdbClient,
        AnimeClickMetadataFallbackService fallbackService,
        AnimeClickTranslationQueue translationQueue,
        AnimeClickLibraryQualityService qualityService,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<AnimeClickDiagnosticsController> logger)
    {
        _searchProvider = searchProvider;
        _episodeListLoader = episodeListLoader;
        _seasonResolver = seasonResolver;
        _layoutResolver = layoutResolver;
        _cache = cache;
        _tmdbClient = tmdbClient;
        _translator = translator;
        _tvdbClient = tvdbClient;
        _fallbackService = fallbackService;
        _translationQueue = translationQueue;
        _qualityService = qualityService;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    [HttpGet("TestLookup")]
    public async Task<ActionResult<IEnumerable<LookupDiagnosticResponse>>> TestLookup(
        [FromQuery] string name,
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var results = await _searchProvider.SearchAsync(name, config, cancellationToken, year, seriesRequest: true);

        return Ok(results.Select(r => new LookupDiagnosticResponse
        {
            Name = r.Name,
            Year = r.ProductionYear,
            ImageUrl = r.ImageUrl,
            AnimeClickId = r.ProviderIds.TryGetValue("AnimeClick", out var id) ? id : null
        }).ToList());
    }

    [HttpGet("TestEpisodes")]
    public async Task<ActionResult<EpisodesDiagnosticResponse>> TestEpisodes(
        [FromQuery] string animeClickId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return BadRequest(new { error = "animeClickId is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, animeClickId, out var animeUrl))
        {
            return BadRequest(new { error = "animeClickId or configured BaseUrl is invalid" });
        }

        var episodesUrl = animeUrl + "/episodi";

        // Reuse the production loader so diagnostics sees the same complete, deduplicated
        // list and applies synthetic seasons only after pagination.
        int? seasonsCount = null;
        int? declaredEpisodeCount = null;
        var seriesCacheKey = $"anime::{animeUrl}";
        var series = await _cache
            .GetAsync<AnimeClickAnime>(seriesCacheKey, config.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (series is not null)
        {
            seasonsCount = series.SeasonsCount > 0 ? series.SeasonsCount : null;
            declaredEpisodeCount = series.EpisodeCount;
        }

        var loaded = await _episodeListLoader.LoadAsync(
            episodesUrl,
            config.BaseUrl,
            seasonsCount,
            declaredEpisodeCount,
            config,
            cancellationToken);
        var episodes = loaded.Episodes;

        AnimeClickEpisodeMatch? match = null;
        if (episode.HasValue)
        {
            match = AnimeClickEpisodeMatcher.Match(
                episodes,
                new AnimeClickEpisodeMatchContext(season, episode.Value)
                {
                    LayoutOverride = AnimeClickEpisodeLayoutOverrideParser.ParseFor(
                        config.EpisodeLayoutOverrides,
                        animeClickId),
                    DeclaredSeasonsCount = loaded.Catalog.DeclaredSeasonsCount > 0
                        ? loaded.Catalog.DeclaredSeasonsCount
                        : null
                });
        }

        return Ok(new EpisodesDiagnosticResponse
        {
            AnimeClickId = animeClickId,
            EpisodeCount = episodes.Count,
            DeclaredEpisodeCount = loaded.Catalog.DeclaredEpisodeCount,
            DeclaredSeasonsCount = loaded.Catalog.DeclaredSeasonsCount,
            LayoutFingerprint = loaded.Catalog.LayoutFingerprint,
            PaginationComplete = loaded.PaginationComplete,
            Episodes = episodes.Select(EpisodeDiagnosticItem.From).ToList(),
            MatchStrategy = match?.Strategy,
            MatchConfidence = match?.Confidence,
            MatchReason = match?.Reason,
            MatchedEpisode = match?.Episode is null ? null : EpisodeDiagnosticItem.From(match.Episode)
        });
    }

    /// <summary>
    /// Explains, series by series, why episodes still have no Italian title — reading only what is
    /// already cached, so auditing a whole library costs no AnimeClick requests.
    /// </summary>
    [HttpGet("LibraryAudit")]
    public async Task<ActionResult<LibraryAuditResponse>> LibraryAudit(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var response = new LibraryAuditResponse
        {
            EpisodeTitlesEnabled = config.EnableEpisodeTitles
        };

        // Two queries for the whole library instead of one per series: on a few thousand episodes
        // the difference is seconds.
        var allSeries = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Series>()
            .Where(AnimeClickAppliesTo)
            .ToList();

        var episodesBySeries = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .GroupBy(episode => episode.SeriesId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var seasonCardIds = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Season>()
            .Where(season => !string.IsNullOrWhiteSpace(season.GetProviderId("AnimeClick")))
            .ToDictionary(
                season => season.Id,
                season => season.GetProviderId("AnimeClick")!);

        var catalogs = new Dictionary<string, AnimeClickEpisodeCatalog?>(StringComparer.OrdinalIgnoreCase);
        foreach (var series in allSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seriesAnimeClickId = series.GetProviderId("AnimeClick");
            var episodes = episodesBySeries.TryGetValue(series.Id, out var found) ? found : [];
            var row = new LibraryAuditSeriesItem
            {
                Id = series.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = series.Name ?? string.Empty,
                Year = series.ProductionYear,
                AnimeClickId = seriesAnimeClickId,
                EpisodeCount = episodes.Count
            };

            var reasons = new List<AnimeClickAuditReason>();
            foreach (var seasonGroup in episodes
                         .GroupBy(episode => episode.ParentIndexNumber ?? episode.SeasonId.GetHashCode())
                         .OrderBy(group => group.Key))
            {
                var seasonEpisodes = seasonGroup.ToList();
                var seasonId = seasonEpisodes[0].SeasonId;
                var storedSeasonCard = seasonCardIds.TryGetValue(seasonId, out var seasonCard)
                    ? seasonCard
                    : null;
                string? traversedCard = null;
                if (!string.IsNullOrWhiteSpace(seriesAnimeClickId)
                    && seasonEpisodes[0].ParentIndexNumber is > 1)
                {
                    var layout = _layoutResolver.Resolve(seasonEpisodes[0].Path);
                    traversedCard = await _seasonResolver
                        .ResolveCachedAsync(
                            seriesAnimeClickId,
                            seasonEpisodes[0].ParentIndexNumber,
                            config,
                            cancellationToken,
                            layout?.GetSeasonAirYears())
                        .ConfigureAwait(false);
                }

                // Replay the provider's priority without leaving the local audit: a proven cached
                // traversal wins, then an explicit season ID, then the series card.
                var cardId = traversedCard ?? storedSeasonCard ?? seriesAnimeClickId;

                AnimeClickEpisodeCatalog? catalog = null;
                if (!string.IsNullOrWhiteSpace(cardId))
                {
                    catalog = await GetCachedCatalogAsync(cardId, config, catalogs, cancellationToken)
                        .ConfigureAwait(false);

                }

                var seasonReasons = seasonEpisodes
                    .Select(episode =>
                    {
                        var needsTitle = AnimeClickRefreshMissingTitlesTask.NeedsTitle(episode);
                        var result = string.IsNullOrWhiteSpace(seriesAnimeClickId)
                            ? needsTitle ? AnimeClickAuditReason.NotIdentified : AnimeClickAuditReason.Ok
                            : AnimeClickLibraryAudit.ClassifyEpisode(
                                episode.GetProviderId("AnimeClick"),
                                episode.Name,
                                needsTitle,
                                catalog);
                        return AnimeClickLibraryAudit.ApplyNameLock(
                            result,
                            AnimeClickRefreshMissingTitlesTask.IsNameLocked(episode));
                    })
                    .Where(reason => reason != AnimeClickAuditReason.Ok)
                    .ToList();
                if (seasonReasons.Count == 0)
                {
                    continue;
                }

                reasons.AddRange(seasonReasons);
                var seasonReason = AnimeClickLibraryAudit.Summarize(seasonReasons);
                row.Seasons.Add(new LibraryAuditSeasonItem
                {
                    SeasonNumber = seasonEpisodes[0].ParentIndexNumber,
                    MissingTitleCount = seasonReasons.Count,
                    AnimeClickId = cardId,
                    Reason = seasonReason.ToString(),
                    ReasonLabel = AnimeClickLibraryAudit.Describe(seasonReason)
                });
            }

            row.MissingTitleCount = reasons.Count;

            // Three separate numbers, because they mean three different things to the user: what a
            // recheck can still fix, what is only waiting for AnimeClick to publish, and what no
            // request will ever change.
            row.RecoverableTitleCount = reasons.Count(AnimeClickLibraryAudit.IsRecoverableByRecheck);
            row.WaitingTitleCount = reasons.Count(AnimeClickLibraryAudit.IsWaitingForSource);
            row.UnavailableTitleCount = row.MissingTitleCount
                - row.RecoverableTitleCount
                - row.WaitingTitleCount;
            var reason = reasons.Count == 0
                ? AnimeClickAuditReason.Ok
                : AnimeClickLibraryAudit.Summarize(reasons);
            row.Reason = reason.ToString();
            row.ReasonLabel = AnimeClickLibraryAudit.Describe(reason);
            response.Series.Add(row);
        }

        response.SeriesCount = response.Series.Count;
        response.EpisodeCount = response.Series.Sum(item => item.EpisodeCount);
        response.MissingTitleCount = response.Series.Sum(item => item.MissingTitleCount);
        response.RecoverableTitleCount = response.Series.Sum(item => item.RecoverableTitleCount);
        response.WaitingTitleCount = response.Series.Sum(item => item.WaitingTitleCount);
        response.UnavailableTitleCount = response.Series.Sum(item => item.UnavailableTitleCount);
        response.Totals = response.Series
            .GroupBy(item => item.Reason)
            .Select(group => new LibraryAuditReasonCount
            {
                Reason = group.Key,
                SeriesCount = group.Count(),
                EpisodeCount = group.Sum(item => item.MissingTitleCount)
            })
            .OrderByDescending(item => item.EpisodeCount)
            .ToList();

        // Problems first, and the largest gap at the top: the report opens on what to act upon.
        response.Series = response.Series
            .OrderByDescending(item => item.MissingTitleCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(response);
    }

    /// <summary>
    /// Re-reads one series' card from AnimeClick and re-classifies it. This is the only audit path
    /// allowed to make a request, and it is bounded to the one series the user asked about.
    /// </summary>
    [HttpPost("LibraryAuditSeries")]
    public async Task<ActionResult<LibraryAuditSeriesItem>> LibraryAuditSeries(
        [FromBody] LibraryAuditSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest(new { error = "itemId is required" });
        }

        if (_libraryManager.GetItemById(itemId) is not Series series)
        {
            return NotFound(new { error = "series not found" });
        }

        var animeClickId = series.GetProviderId("AnimeClick");
        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return Ok(new LibraryAuditSeriesItem
            {
                Id = series.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = series.Name ?? string.Empty,
                Year = series.ProductionYear,
                Reason = nameof(AnimeClickAuditReason.NotIdentified),
                ReasonLabel = AnimeClickLibraryAudit.Describe(AnimeClickAuditReason.NotIdentified)
            });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var episodes = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                ParentId = series.Id,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .ToList();

        var result = new LibraryAuditSeriesItem
        {
            Id = series.Id.ToString("N", CultureInfo.InvariantCulture),
            Name = series.Name ?? string.Empty,
            Year = series.ProductionYear,
            AnimeClickId = animeClickId,
            EpisodeCount = episodes.Count
        };

        var reasons = new List<AnimeClickAuditReason>();
        foreach (var seasonGroup in episodes
                     .GroupBy(episode => episode.ParentIndexNumber)
                     .OrderBy(group => group.Key ?? int.MaxValue))
        {
            var seasonEpisodes = seasonGroup.ToList();

            // Replay the provider's own decision instead of assuming the series card: on AnimeClick
            // a season is usually a card of its own, and reporting "no match" against the wrong card
            // would blame the plugin for a season it never looked at.
            var seasonNumber = seasonGroup.Key;
            var storedSeasonId = _libraryManager.GetItemById(seasonEpisodes[0].SeasonId) is Season season
                ? season.GetProviderId("AnimeClick")
                : null;
            var layout = _layoutResolver.Resolve(seasonEpisodes[0].Path);
            var traversed = await _seasonResolver
                .ResolveAsync(
                    animeClickId,
                    seasonNumber,
                    config,
                    cancellationToken,
                    layout?.GetSeasonAirYears())
                .ConfigureAwait(false);
            var cardId = traversed ?? storedSeasonId ?? animeClickId;
            var catalog = await LoadCatalogAsync(cardId, config, cancellationToken).ConfigureAwait(false);

            var seasonReasons = seasonEpisodes
                .Select(episode =>
                {
                    var result = AnimeClickLibraryAudit.ClassifyEpisode(
                        episode.GetProviderId("AnimeClick"),
                        episode.Name,
                        AnimeClickRefreshMissingTitlesTask.NeedsTitle(episode),
                        catalog);
                    return result == AnimeClickAuditReason.PendingRefresh
                           && AnimeClickRefreshMissingTitlesTask.IsNameLocked(episode)
                        ? AnimeClickAuditReason.Locked
                        : result;
                })
                .Where(reason => reason != AnimeClickAuditReason.Ok)
                .ToList();
            if (seasonReasons.Count == 0)
            {
                continue;
            }

            // A season past the first, on a card that is not its own and that nothing resolved, is
            // the case the season-level ID field exists for. Saying so is more useful than
            // reporting a mismatch the user cannot act on.
            var seasonReason = AnimeClickLibraryAudit.Summarize(seasonReasons);
            if (seasonReason == AnimeClickAuditReason.NotMatched
                && seasonNumber is > 1
                && traversed is null
                && string.IsNullOrWhiteSpace(storedSeasonId))
            {
                seasonReason = AnimeClickAuditReason.CardNotResolved;
                seasonReasons = [.. seasonReasons.Select(_ => AnimeClickAuditReason.CardNotResolved)];
            }

            reasons.AddRange(seasonReasons);
            result.Seasons.Add(new LibraryAuditSeasonItem
            {
                SeasonNumber = seasonNumber,
                MissingTitleCount = seasonReasons.Count,
                AnimeClickId = cardId,
                CardIsResolved = traversed is not null || !string.IsNullOrWhiteSpace(storedSeasonId),
                CardRowCount = catalog?.Episodes.Count,
                Reason = seasonReason.ToString(),
                ReasonLabel = AnimeClickLibraryAudit.Describe(seasonReason)
            });
        }

        result.MissingTitleCount = reasons.Count;
        var reason = reasons.Count == 0 ? AnimeClickAuditReason.Ok : AnimeClickLibraryAudit.Summarize(reasons);
        result.Reason = reason.ToString();
        result.ReasonLabel = AnimeClickLibraryAudit.Describe(reason);
        result.CardRowCount = result.Seasons.Sum(item => item.CardRowCount ?? 0);
        return Ok(result);
    }

    /// <summary>
    /// Reads one card's episode table, from the cache when it is warm and from AnimeClick when it is
    /// not. Only the single-series audit uses this; the library-wide report never leaves the cache.
    /// </summary>
    private async Task<AnimeClickEpisodeCatalog?> LoadCatalogAsync(
        string animeClickId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, animeClickId, out var animeUrl))
        {
            return null;
        }

        var summary = await _cache
            .GetAsync<AnimeClickAnime>($"anime::{animeUrl}", config.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        var cached = await _cache
            .GetAsync<AnimeClickEpisodeCatalog>(
                AnimeClickEpisodeProvider.BuildCatalogCacheKey(
                    animeClickId,
                    summary?.EpisodeCount,
                    summary?.SeasonsCount ?? 0),
                config.CacheHours,
                cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null && cached.Episodes.Count > 0)
        {
            return cached;
        }

        var loaded = await _episodeListLoader.LoadAsync(
                animeUrl + "/episodi",
                config.BaseUrl,
                summary?.SeasonsCount > 0 ? summary.SeasonsCount : null,
                summary?.EpisodeCount,
                config,
                cancellationToken)
            .ConfigureAwait(false);
        return loaded.Catalog;
    }

    /// <summary>
    /// Classifies the language and completeness of metadata already stored in Jellyfin. The scan is
    /// deliberately local: it does not contact AnimeClick, TMDB, TVDB or the configured AI service.
    /// </summary>
    [HttpGet("LibraryQualityAudit")]
    public async Task<ActionResult<AnimeClickLibraryQualityReport>> LibraryQualityAudit(
        CancellationToken cancellationToken)
    {
        var report = await _qualityService.AuditAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "AnimeClick local metadata audit: groups={Groups} items={Items} english={English} missing={Missing} unknown={Unknown} locked={Locked} repairable={Repairable} waiting={Waiting} noSource={NoSource} attempted={Attempted}",
            report.GroupCount,
            report.ItemCount,
            report.EnglishCount,
            report.MissingCount,
            report.UnknownCount,
            report.LockedCount,
            report.RepairableCount,
            report.WaitingTranslationCount,
            report.NoSourceCount,
            report.AttemptedCount);
        return Ok(report);
    }

    /// <summary>
    /// Queues a bounded set of non-destructive metadata refreshes. Every ID is revalidated against
    /// the current library state, and only English or missing unlocked fields remain eligible.
    /// </summary>
    [HttpPost("LibraryQualityRepair")]
    public async Task<ActionResult<AnimeClickLibraryQualityRepairResult>> LibraryQualityRepair(
        [FromBody] LibraryQualityRepairRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ItemIds is null || request.ItemIds.Count == 0)
        {
            return BadRequest(new { error = "itemIds must contain at least one Jellyfin item ID" });
        }

        return Ok(await _qualityService
            .QueueRepairAsync(request.ItemIds, request.Force, cancellationToken)
            .ConfigureAwait(false));
    }

    public sealed class LibraryQualityRepairRequest
    {
        public List<string> ItemIds { get; set; } = [];

        /// <summary>
        /// Re-attempts items whose recorded attempt found no source. Off by default: without it a
        /// batch would keep being spent on the same unfixable items.
        /// </summary>
        public bool Force { get; set; }
    }

    /// <summary>
    /// Queues the synopsis completion task, so one click replaces the queue/wait/analyse cycle the
    /// page could otherwise only do a hundred items at a time.
    /// </summary>
    [HttpPost("RunSynopsisRepairTask")]
    public ActionResult<RunTaskResponse> RunSynopsisRepairTask()
    {
        _taskManager.CancelIfRunningAndQueue<AnimeClickRepairSynopsesTask>();
        _logger.LogInformation("AnimeClick: completamento sinossi accodato dalla pagina di configurazione");
        return Ok(new RunTaskResponse
        {
            Queued = true,
            Message = "Completamento delle sinossi avviato. Procede a lotti finché non resta niente da "
                + "sistemare; l'avanzamento è visibile in Attività pianificate."
        });
    }

    /// <summary>
    /// Queues the weekly title re-check immediately, so the user does not have to go looking for it
    /// among Jellyfin's scheduled tasks.
    /// </summary>
    [HttpPost("RunMissingTitlesTask")]
    public ActionResult<RunTaskResponse> RunMissingTitlesTask()
    {
        _taskManager.CancelIfRunningAndQueue<AnimeClickRefreshMissingTitlesTask>();
        _logger.LogInformation("AnimeClick: title re-check queued from the configuration page");
        return Ok(new RunTaskResponse
        {
            Queued = true,
            Message = "Ricontrollo dei titoli accodato. L'avanzamento è visibile in Attività pianificate."
        });
    }

    /// <summary>
    /// True when this library asks AnimeClick for its metadata. A library that does not is none of
    /// the report's business; when Jellyfin does not say, the plugin's own ID is the evidence.
    /// </summary>
    private bool AnimeClickAppliesTo(Series series)
    {
        if (!string.IsNullOrWhiteSpace(series.GetProviderId("AnimeClick")))
        {
            return true;
        }

        var fetchers = _libraryManager
            .GetLibraryOptions(series)?
            .TypeOptions?
            .FirstOrDefault(option => string.Equals(option.Type, "Series", StringComparison.OrdinalIgnoreCase))?
            .MetadataFetchers;
        return fetchers is not null
            && fetchers.Contains("AnimeClick", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cached catalog for one card, or null when nothing is on disk. Reads are memoized because
    /// every season of a series asks for the same card.
    /// </summary>
    private async Task<AnimeClickEpisodeCatalog?> GetCachedCatalogAsync(
        string animeClickId,
        PluginConfiguration config,
        Dictionary<string, AnimeClickEpisodeCatalog?> memo,
        CancellationToken cancellationToken)
    {
        if (memo.TryGetValue(animeClickId, out var cached))
        {
            return cached;
        }

        AnimeClickEpisodeCatalog? catalog = null;
        if (AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, animeClickId, out var animeUrl))
        {
            var summary = await _cache
                .GetAsync<AnimeClickAnime>($"anime::{animeUrl}", config.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            var key = AnimeClickEpisodeProvider.BuildCatalogCacheKey(
                animeClickId,
                summary?.EpisodeCount,
                summary?.SeasonsCount ?? 0);
            catalog = await _cache
                .GetAsync<AnimeClickEpisodeCatalog>(key, config.CacheHours, cancellationToken)
                .ConfigureAwait(false);
        }

        memo[animeClickId] = catalog;
        return catalog;
    }

    [HttpPost("ClearCache")]
    public ActionResult<ClearCacheResponse> ClearCache([FromBody] ClearCacheRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var hasKey = !string.IsNullOrWhiteSpace(request.Key);
        var hasPrefix = !string.IsNullOrWhiteSpace(request.Prefix);
        var hasAnimeClickId = !string.IsNullOrWhiteSpace(request.AnimeClickId);
        string? normalizedId = null;
        string? canonicalAnimeUrl = null;

        // Validate every AnimeClick-specific input before deleting any requested key, so a
        // malformed ID/BaseUrl cannot leave a partially-cleared cache and then return 500.
        if (hasAnimeClickId)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!AnimeClickClient.TryNormalizeAnimeClickId(request.AnimeClickId, out normalizedId)
                || !AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, normalizedId, out canonicalAnimeUrl))
            {
                return BadRequest(new
                {
                    error = "animeClickId or configured BaseUrl is invalid; expected 'number' or 'number/slug'"
                });
            }
        }

        // Hold publication for the complete administrative clear. Targeted requests invalidate
        // only matching translation work; unrelated series keep their pending callbacks.
        Func<string, bool>? translationInvalidationPredicate = null;
        if (hasKey || hasPrefix || normalizedId is not null)
        {
            var stableId = normalizedId?.Split('/', 2)[0];
            translationInvalidationPredicate = workKey =>
                (hasKey
                    && (string.Equals(workKey, request.Key, StringComparison.Ordinal)
                        || string.Equals(workKey + "::backoff", request.Key, StringComparison.Ordinal)))
                || (hasPrefix
                    && (workKey.StartsWith(request.Prefix!, StringComparison.Ordinal)
                        || (workKey + "::backoff").StartsWith(request.Prefix!, StringComparison.Ordinal)))
                || (stableId is not null
                    && (workKey.StartsWith(
                            "translation:v4::" + stableId + "::",
                            StringComparison.Ordinal)
                        || workKey.StartsWith(
                            "translation:v4::" + stableId + "/",
                            StringComparison.Ordinal)));
        }

        using var translationInvalidation = _translationQueue.BeginInvalidation(
            translationInvalidationPredicate);

        if (!hasKey && !hasPrefix && !hasAnimeClickId)
        {
            return Ok(new ClearCacheResponse { Removed = _cache.ClearAll() });
        }

        var removed = 0;
        if (hasKey)
        {
            removed += _cache.ClearKey(request.Key!);
        }

        if (hasPrefix)
        {
            removed += _cache.ClearByPrefix(request.Prefix!);
        }

        if (normalizedId is not null && canonicalAnimeUrl is not null)
        {
            var stableAnimeClickId = normalizedId.Split('/', 2)[0];

            // Episode synopsis entries are keyed by the stable /episodio ID, which
            // cannot be derived from a series ID. A targeted series reset therefore
            // invalidates this small cache family as a whole.
            removed += _cache.ClearByPrefix("episodeOverview:v2::");
            removed += _cache.ClearByPrefix("episodeOverview:v1::");

            // Raw catalog keys include declared-count suffixes. Clear the current family and the
            // legacy version so an administrative reset cannot leave semantically stale rows.
            foreach (var rawPrefix in new[] { "episodes:raw:v6::", "episodes:raw:v5::" })
            {
                // SanitizeFileKey hash-shortens filenames over 200 bytes. A full long slug can no
                // longer be matched as a prefix after that boundary, while these stable numeric
                // family prefixes remain short and clear both numeric and every slug variant.
                removed += _cache.ClearByPrefix(rawPrefix + stableAnimeClickId + "::");
                removed += _cache.ClearByPrefix(rawPrefix + stableAnimeClickId + "/");
            }

            var episodePrefixes = new[] { "episodes:v4::", "episodes:v3::", "episodes:v2::", "episodes::" };
            foreach (var prefix in episodePrefixes)
            {
                removed += _cache.ClearKey(prefix + stableAnimeClickId);
                removed += _cache.ClearByPrefix(prefix + stableAnimeClickId + "/");
            }

            var seasonPrefixes = new[]
            {
                "seasonMap:v6::",
                "seasonMap:v5::",
                "seasonMap:v4::",
                "seasonMap:v3::",
                "seasonMap:v2::",
                "seasonMap::"
            };
            foreach (var prefix in seasonPrefixes)
            {
                removed += _cache.ClearByPrefix(prefix + stableAnimeClickId + "::");
                removed += _cache.ClearByPrefix(prefix + stableAnimeClickId + "/");
            }

            // Clear language-aware source resolution and content-addressed translations
            // associated with this AnimeClick entry. Numeric IDs also clear slug variants.
            var externalIdPrefixes = new[]
            {
                "tmdbTvId:v3::",
                "tvdbSeriesId:v3::",
                "tmdbTvId:v2::",
                "tvdbSeriesId:v2::",
                "tmdbId::",
                "tvdbSeriesId::"
            };
            foreach (var prefix in externalIdPrefixes)
            {
                var mappingKey = prefix + stableAnimeClickId;
                removed += _cache.ClearKey(mappingKey);
                removed += _cache.ClearKey(mappingKey + "::miss");
                removed += _cache.ClearByPrefix(prefix + stableAnimeClickId + "/");
            }

            removed += _cache.ClearByPrefix("anilistId:v3::" + stableAnimeClickId + "::");
            removed += _cache.ClearByPrefix("anilistId:v2::" + stableAnimeClickId + "::");

            foreach (var translationPrefix in new[]
                     {
                         "translation:v4::",
                         "translation:v3::",
                         "translation:v2::"
                     })
            {
                removed += _cache.ClearByPrefix(translationPrefix + stableAnimeClickId + "::");
                removed += _cache.ClearByPrefix(translationPrefix + stableAnimeClickId + "/");
            }

            if (normalizedId.Contains('/', StringComparison.Ordinal))
            {
                removed += _cache.ClearKey("anime::" + canonicalAnimeUrl);
            }
            else
            {
                var lastSlash = canonicalAnimeUrl.LastIndexOf('/');
                var numericAnimePrefix = canonicalAnimeUrl[..(lastSlash + 1)];
                removed += _cache.ClearByPrefix("anime::" + numericAnimePrefix);
            }
        }

        return Ok(new ClearCacheResponse { Removed = removed });
    }

    /// <summary>
    /// Validates the TMDB API key (as currently entered in the form) by running a
    /// search/tv with a known query. Returns a detailed result for the diagnostics UI.
    /// </summary>
    [HttpPost("TestTmdb")]
    public async Task<ActionResult<TmdbTestResult>> TestTmdb(
        [FromBody] TestTmdbRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var apiKey = request.ApiKey ?? (Plugin.Instance?.Configuration ?? new PluginConfiguration()).TmdbApiKey;
        var result = await _tmdbClient.TestConnectionAsync(apiKey, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Sends a trivial prompt to the AI profile currently entered in the form — provider, endpoint,
    /// key and model — and reports what came back. Kept reachable under the historical route name
    /// too, so anything scripted against it keeps working.
    /// </summary>
    [HttpPost("TestAi")]
    [HttpPost("TestOllama")]
    public async Task<ActionResult<AnimeClickAiTestResult>> TestAi(
        [FromBody] TestAiRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!TryResolveAiProfile(
                request.Provider,
                request.Endpoint,
                request.ApiKey,
                request.Model,
                config,
                out var provider,
                out var endpoint,
                out var apiKey,
                out var model,
                out var profileError))
        {
            return BadRequest(new { error = profileError });
        }

        var timeoutSec = request.TimeoutSec is > 0 ? request.TimeoutSec.Value : config.EpisodeTranslationTimeoutSec;

        var result = await _translator.TestConnectionAsync(
                endpoint,
                apiKey,
                model,
                timeoutSec,
                AnimeClickAiProviders.ResolveDialect(provider, endpoint),
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// The models the configured credential can actually use. This is what makes a model field
    /// usable: names change with every vendor release, so the plugin asks instead of shipping a
    /// list that goes stale.
    /// </summary>
    [HttpPost("AiModels")]
    public async Task<ActionResult<AnimeClickAiModelsResult>> AiModels(
        [FromBody] TestAiRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!TryResolveAiProfile(
                request.Provider,
                request.Endpoint,
                request.ApiKey,

                // The model is what this call is meant to discover, so it must not be required.
                request.Model ?? "-",
                config,
                out var provider,
                out var endpoint,
                out var apiKey,
                out _,
                out var profileError))
        {
            return BadRequest(new { error = profileError });
        }

        var result = await _translator.ListModelsAsync(
                AnimeClickAiProviders.ResolveModelsEndpoint(provider, endpoint),
                apiKey,
                AnimeClickAiProviders.ResolveDialect(provider, endpoint),
                request.TimeoutSec is > 0 ? request.TimeoutSec.Value : 30,
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The selectable AI services, so the configuration page never hardcodes the list.</summary>
    [HttpGet("AiProviders")]
    public ActionResult<IEnumerable<AiProviderInfo>> AiProviders()
        => Ok(AnimeClickAiProviders.Presets.Select(preset => new AiProviderInfo
        {
            Id = preset.Id,
            DisplayName = preset.DisplayName,
            ChatEndpoint = preset.ChatEndpoint,
            RequiresApiKey = preset.RequiresApiKey,
            SupportsModelListing = !string.IsNullOrWhiteSpace(preset.ModelsEndpoint)
                || preset.Id == AnimeClickAiProviders.CustomId,
            CredentialUrl = preset.CredentialUrl,
            Note = preset.Note
        }).ToList());

    /// <summary>
    /// Produces an EN→IT preview with the same model, prompt, cache and global
    /// concurrency gate used by metadata refreshes. Credentials can be tested before
    /// saving but are never echoed in the response.
    /// </summary>
    [HttpPost("PreviewTranslation")]
    public async Task<ActionResult<TranslationPreviewResponse>> PreviewTranslation(
        [FromBody] TranslationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SourceText))
        {
            return BadRequest(new { error = "sourceText is required" });
        }

        if (request.SourceText.Length > 8000)
        {
            return BadRequest(new { error = "sourceText must not exceed 8000 characters" });
        }

        var stored = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!TryResolveAiProfile(
                request.Provider,
                request.Endpoint,
                request.ApiKey,
                request.Model,
                stored,
                out var provider,
                out var endpoint,
                out var apiKey,
                out var model,
                out var profileError))
        {
            return BadRequest(new { error = profileError });
        }

        var effective = new PluginConfiguration
        {
            AiProvider = provider,
            AiEndpoint = endpoint,
            AiApiKey = apiKey,
            AiModel = model,
            EpisodeTranslationTimeoutSec = request.TimeoutSec is > 0
                ? request.TimeoutSec.Value
                : stored.EpisodeTranslationTimeoutSec,
            TranslationCacheHours = stored.TranslationCacheHours
        };

        var sourceText = request.SourceText.Trim();
        var translated = await _translator.TranslateMetadataFieldAsync(
                sourceText,
                "diagnostics",
                "manual-preview",
                "episode.overview",
                "en",
                "it",
                effective,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(new TranslationPreviewResponse
        {
            Success = !string.IsNullOrWhiteSpace(translated),
            Translation = translated,
            Model = effective.AiModel,
            SourceLanguage = "en",
            TargetLanguage = "it",
            SourceCharacterCount = sourceText.Length,
            ErrorMessage = string.IsNullOrWhiteSpace(translated)
                ? "Nessuna traduzione prodotta. Usa «Verifica AI» per il dettaglio."
                : null
        });
    }

    /// <summary>
    /// Runs the production episode overview chain and reports the winning source.
    /// The episode detail identity is resolved internally from series, season and
    /// episode so the diagnostics UI never asks users for a technical /episodio ID.
    /// </summary>
    [HttpPost("PreviewEpisodeFallback")]
    public async Task<ActionResult<EpisodeFallbackPreviewResponse>> PreviewEpisodeFallback(
        [FromBody] EpisodeFallbackPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AnimeClickId))
        {
            return BadRequest(new { error = "animeClickId is required" });
        }

        if (request.Season < 0 || request.Episode <= 0)
        {
            return BadRequest(new { error = "season must be >= 0 and episode must be > 0" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryNormalizeAnimeClickId(request.AnimeClickId, out var normalizedId)
            || !AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, normalizedId, out _))
        {
            return BadRequest(new { error = "animeClickId or configured BaseUrl is invalid" });
        }

        AnimeClickEpisodeMatch? animeClickMatch = null;
        var episodeMatchLookupFailed = false;
        try
        {
            animeClickMatch = await ResolveEpisodeMatchForPreviewAsync(
                    normalizedId,
                    request.Season,
                    request.Episode,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            episodeMatchLookupFailed = true;
            // Match/list failures must not hide a valid TVDB/TMDB fallback preview.
            _logger.LogDebug(
                ex,
                "AnimeClick diagnostics could not resolve the detail ID for {Id} S{Season}E{Episode}",
                normalizedId,
                request.Season,
                request.Episode);
        }

        var episodeAnimeClickId = animeClickMatch?.Episode?.ProviderId;

        var tvdbConfigured = config.EnableTvdbSynopsis
            && !string.IsNullOrWhiteSpace(config.TvdbApiKey);
        var tmdbConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var aiConfigured = AnimeClickAiTranslator.IsConfigured(config, out _);

        var fallback = await _fallbackService.ResolveEpisodeOverviewAsync(
                normalizedId,
                request.Season,
                request.Episode,
                episodeAnimeClickId,
                config,
                cancellationToken,
                allowSynchronousTranslation: true)
            .ConfigureAwait(false);

        return Ok(new EpisodeFallbackPreviewResponse
        {
            Success = fallback is not null,
            Overview = fallback?.Value,
            Source = fallback?.Source,
            SourceLanguage = fallback?.SourceLanguage,
            UsedAi = fallback?.UsedAi ?? false,
            Model = fallback?.Model,
            // This endpoint has no Jellyfin item title, file range or complete library
            // topology, so its episode match is always advisory even when it resolves an ID.
            AnimeClickMatchConclusive = false,
            AnimeClickMatchStrategy = animeClickMatch?.Strategy,
            AnimeClickMatchConfidence = animeClickMatch?.Confidence,
            AnimeClickMatchReason = episodeMatchLookupFailed
                ? "episode-list-unavailable"
                : animeClickMatch?.Reason,
            Chain =
            [
                new FallbackChainStep("AnimeClick", "it", false, episodeAnimeClickId is not null),
                new FallbackChainStep("TheTVDB", "ita", false, tvdbConfigured),
                new FallbackChainStep("TMDB", "it-IT", false, tmdbConfigured),
                new FallbackChainStep("TMDB", "en-US", true, tmdbConfigured && aiConfigured),
                new FallbackChainStep("TheTVDB", "eng", true, tvdbConfigured && aiConfigured),
                new FallbackChainStep(AnimeClickAiProviders.Resolve(config.AiProvider).DisplayName, "en→it", true, aiConfigured)
            ],
            ErrorMessage = fallback is null
                ? "Né AnimeClick né le fonti esterne configurate hanno prodotto una sinossi italiana."
                : null
        });
    }

    private async Task<AnimeClickEpisodeMatch?> ResolveEpisodeMatchForPreviewAsync(
        string animeClickId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var pageAnimeClickId = animeClickId;
        var resolvedSeasonId = await _seasonResolver
            .ResolveAsync(animeClickId, season, configuration, cancellationToken)
            .ConfigureAwait(false);
        var isSeasonSpecificPage = !string.IsNullOrWhiteSpace(resolvedSeasonId);
        if (isSeasonSpecificPage)
        {
            pageAnimeClickId = resolvedSeasonId!;
        }

        if (!AnimeClickClient.TryBuildAnimeUrl(
                configuration.BaseUrl,
                pageAnimeClickId,
                out var animeUrl))
        {
            return null;
        }

        var seriesCacheKey = $"anime::{animeUrl}";
        var series = await _cache
            .GetAsync<AnimeClickAnime>(seriesCacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            // Direct-ID lookup uses the same client/parser and fills the production cache.
            await _searchProvider.SearchAsync(
                    pageAnimeClickId,
                    configuration,
                    cancellationToken,
                    productionYear: null,
                    seriesRequest: true)
                .ConfigureAwait(false);
            series = await _cache
                .GetAsync<AnimeClickAnime>(seriesCacheKey, configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
        }

        var loaded = await _episodeListLoader.LoadAsync(
                animeUrl + "/episodi",
                configuration.BaseUrl,
                series?.SeasonsCount is > 0 ? series.SeasonsCount : null,
                series?.EpisodeCount,
                configuration,
                cancellationToken)
            .ConfigureAwait(false);
        var pageSeason = isSeasonSpecificPage ? 1 : season;
        var layoutOverride = AnimeClickEpisodeLayoutOverrideParser.ParseFor(
                                 configuration.EpisodeLayoutOverrides,
                                 pageAnimeClickId)
                             ?? AnimeClickEpisodeLayoutOverrideParser.ParseFor(
                                 configuration.EpisodeLayoutOverrides,
                                 animeClickId);
        var match = AnimeClickEpisodeMatcher.Match(
            loaded.Episodes,
            new AnimeClickEpisodeMatchContext(pageSeason, episode)
            {
                LayoutOverride = layoutOverride,
                DeclaredSeasonsCount = loaded.Catalog.DeclaredSeasonsCount > 0
                    ? loaded.Catalog.DeclaredSeasonsCount
                    : null,
                IsSeasonSpecificPage = isSeasonSpecificPage
            });
        return match;
    }

    /// <summary>
    /// Validates the TheTVDB API key (as currently entered in the form) by logging in
    /// and running a series search. Returns a detailed result.
    /// </summary>
    [HttpPost("TestTvdb")]
    public async Task<ActionResult<TvdbTestResult>> TestTvdb(
        [FromBody] TestTvdbRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var apiKey = request.ApiKey ?? config.TvdbApiKey;

        // Production deliberately probes fixed ita/eng sources. Test the primary
        // production language instead of a UI-only custom value.
        var result = await _tvdbClient.TestConnectionAsync(apiKey, "ita", cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Works out which destination a diagnostics call should use: what the form sent when it sent
    /// something, otherwise what is saved. A changed destination has to come with its own freshly
    /// typed key, so a secret saved for one service is never replayed against another — that rule is
    /// the reason this is not just three null-coalescing operators.
    /// </summary>
    private static bool TryResolveAiProfile(
        string? requestedProvider,
        string? requestedEndpoint,
        string? requestedApiKey,
        string? requestedModel,
        PluginConfiguration stored,
        out string provider,
        out string endpoint,
        out string apiKey,
        out string model,
        out string error)
    {
        const string endpointError =
            "L'endpoint deve essere HTTPS — oppure HTTP verso un indirizzo della tua rete — "
            + "senza credenziali, query o frammenti.";

        provider = string.IsNullOrWhiteSpace(requestedProvider)
            ? stored.AiProvider
            : requestedProvider.Trim();

        // A provider chosen in the form without an endpoint means "use this service's own": that is
        // what makes the preset menu work before anything has been saved.
        var preset = AnimeClickAiProviders.Resolve(provider);
        if (string.IsNullOrWhiteSpace(requestedEndpoint)
            && !string.IsNullOrWhiteSpace(requestedProvider)
            && !string.IsNullOrWhiteSpace(preset.ChatEndpoint))
        {
            requestedEndpoint = preset.ChatEndpoint;
        }

        var storedEndpointIsValid = AnimeClickAiTranslator.TryNormalizeEndpoint(
            stored.AiEndpoint,
            out var storedEndpointUri);
        Uri endpointUri;
        if (string.IsNullOrWhiteSpace(requestedEndpoint))
        {
            if (!storedEndpointIsValid)
            {
                endpoint = string.Empty;
                apiKey = string.Empty;
                model = string.Empty;
                error = endpointError;
                return false;
            }

            endpointUri = storedEndpointUri;
        }
        else if (!AnimeClickAiTranslator.TryNormalizeEndpoint(requestedEndpoint, out endpointUri))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = endpointError;
            return false;
        }

        var endpointChanged = !string.IsNullOrWhiteSpace(requestedEndpoint)
            && (!storedEndpointIsValid || !IsSameDestination(storedEndpointUri, endpointUri));
        var explicitApiKey = requestedApiKey?.Trim() ?? string.Empty;
        var storedApiKey = stored.AiApiKey?.Trim() ?? string.Empty;

        // A destination on the user's own network authenticates nothing and is never sent the key,
        // so demanding one would only make a local service impossible to test.
        var needsCredential = endpointUri.Scheme == Uri.UriSchemeHttps && preset.RequiresApiKey;

        // Endpoint and key are one atomic security profile. A changed destination
        // requires a freshly supplied key and may not reuse the persisted secret.
        if (needsCredential && endpointChanged && string.IsNullOrWhiteSpace(explicitApiKey))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = "Serve la chiave API del servizio quando cambi destinazione.";
            return false;
        }

        // Deliberately not gated on needsCredential: replaying a saved secret towards a different
        // destination must be refused whatever the chosen preset claims about needing a key. The
        // first version tied this to the preset, and since an unknown provider id resolves to
        // "custom" — which needs no key — asking for custom plus an arbitrary HTTPS endpoint walked
        // straight past both guards and had the saved key sent there.
        if (endpointChanged
            && !string.IsNullOrEmpty(storedApiKey)
            && string.Equals(explicitApiKey, storedApiKey, StringComparison.Ordinal))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = "La chiave salvata non può essere riusata verso una destinazione diversa.";
            return false;
        }

        endpoint = endpointUri.AbsoluteUri;

        // Same reason: the saved key belongs to the saved destination. Towards a new one only a key
        // typed in this very request may be used, and towards a plain-HTTP endpoint none at all.
        apiKey = endpointUri.Scheme != Uri.UriSchemeHttps
            ? string.Empty
            : endpointChanged
                ? explicitApiKey
                : (string.IsNullOrWhiteSpace(explicitApiKey) ? storedApiKey : explicitApiKey);
        model = string.IsNullOrWhiteSpace(requestedModel) ? stored.AiModel : requestedModel.Trim();
        error = string.Empty;
        return true;
    }

    private static bool IsSameDestination(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal);
}

public sealed class LookupDiagnosticResponse
{
    public string? Name { get; set; }
    public int? Year { get; set; }
    public string? ImageUrl { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class EpisodesDiagnosticResponse
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int EpisodeCount { get; set; }
    public int? DeclaredEpisodeCount { get; set; }
    public int DeclaredSeasonsCount { get; set; }
    public string LayoutFingerprint { get; set; } = string.Empty;
    public bool PaginationComplete { get; set; }
    public List<EpisodeDiagnosticItem> Episodes { get; set; } = [];
    public string? MatchStrategy { get; set; }
    public double? MatchConfidence { get; set; }
    public string? MatchReason { get; set; }
    public EpisodeDiagnosticItem? MatchedEpisode { get; set; }
}

public sealed class EpisodeDiagnosticItem
{
    public int? SeasonNumber { get; set; }
    public int? RawSeasonNumber { get; set; }
    public bool SeasonNumberIsSynthetic { get; set; }
    public string RawNumberLabel { get; set; } = string.Empty;
    public int Number { get; set; }
    public int? NumberEnd { get; set; }
    public int AbsoluteNumber { get; set; }
    public int GlobalOrdinal { get; set; }
    public int SeasonOrdinalNumber { get; set; }
    public int SpecialOrdinalNumber { get; set; }
    public bool IsSpecial { get; set; }
    public bool HasNonStandardNumber { get; set; }
    public bool NumberIsAmbiguous { get; set; }
    public string? Title { get; set; }
    public string? ProviderId { get; set; }
    public string? DetailUrl { get; set; }

    public static EpisodeDiagnosticItem From(AnimeClickEpisode episode)
        => new()
        {
            SeasonNumber = episode.SeasonNumber,
            RawSeasonNumber = episode.RawSeasonNumber,
            SeasonNumberIsSynthetic = episode.SeasonNumberIsSynthetic,
            RawNumberLabel = episode.RawNumberLabel,
            Number = episode.Number,
            NumberEnd = episode.NumberEnd,
            AbsoluteNumber = episode.AbsoluteNumber,
            GlobalOrdinal = episode.GlobalOrdinal,
            SeasonOrdinalNumber = episode.SeasonOrdinalNumber,
            SpecialOrdinalNumber = episode.SpecialOrdinalNumber,
            IsSpecial = episode.IsSpecial,
            HasNonStandardNumber = episode.HasNonStandardNumber,
            NumberIsAmbiguous = episode.NumberIsAmbiguous,
            Title = episode.Title,
            ProviderId = episode.ProviderId,
            DetailUrl = episode.DetailUrl
        };
}

public sealed class ClearCacheRequest
{
    public string? Key { get; set; }
    public string? Prefix { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class ClearCacheResponse
{
    public int Removed { get; set; }
}

public sealed class TestTmdbRequest
{
    public string? ApiKey { get; set; }
}

public sealed class TestAiRequest
{
    /// <summary>Identifier of the selected service, empty to use the saved one.</summary>
    public string? Provider { get; set; }

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TestTvdbRequest
{
    public string? ApiKey { get; set; }
}


public sealed class TranslationPreviewRequest
{
    public string? SourceText { get; set; }
    public string? Provider { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TranslationPreviewResponse
{
    public bool Success { get; set; }
    public string? Translation { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "it";
    public int SourceCharacterCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class EpisodeFallbackPreviewRequest
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int Season { get; set; } = 1;
    public int Episode { get; set; } = 1;
}

public sealed class EpisodeFallbackPreviewResponse
{
    public bool Success { get; set; }
    public string? Overview { get; set; }
    public string? Source { get; set; }
    public string? SourceLanguage { get; set; }
    public bool UsedAi { get; set; }
    public string? Model { get; set; }
    public bool AnimeClickMatchConclusive { get; set; }
    public string? AnimeClickMatchStrategy { get; set; }
    public double? AnimeClickMatchConfidence { get; set; }
    public string? AnimeClickMatchReason { get; set; }
    public List<FallbackChainStep> Chain { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public sealed record FallbackChainStep(
    string Source,
    string Language,
    bool RequiresTranslation,
    bool Configured);


public sealed class LibraryAuditResponse
{
    /// <summary>False when the whole feature is off, which explains every missing title at once.</summary>
    public bool EpisodeTitlesEnabled { get; set; }

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }

    public int MissingTitleCount { get; set; }

    /// <summary>Episodes a recheck can still fix.</summary>
    public int RecoverableTitleCount { get; set; }

    /// <summary>Episodes whose row exists but whose title AnimeClick has not published yet.</summary>
    public int WaitingTitleCount { get; set; }

    /// <summary>
    /// Episodes no request can fix: the card lists them without titles, the numbering is ambiguous,
    /// the field is locked, or the season needs its own card ID.
    /// </summary>
    public int UnavailableTitleCount { get; set; }

    public List<LibraryAuditReasonCount> Totals { get; set; } = [];

    public List<LibraryAuditSeriesItem> Series { get; set; } = [];
}

public sealed class LibraryAuditReasonCount
{
    public string Reason { get; set; } = string.Empty;

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }
}

public sealed class LibraryAuditSeriesItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? AnimeClickId { get; set; }

    public int EpisodeCount { get; set; }

    public int MissingTitleCount { get; set; }

    /// <summary>Of the missing ones, those a recheck can still fix.</summary>
    public int RecoverableTitleCount { get; set; }

    /// <summary>Of the missing ones, those only waiting for AnimeClick to publish the title.</summary>
    public int WaitingTitleCount { get; set; }

    /// <summary>Of the missing ones, those no request can change.</summary>
    public int UnavailableTitleCount { get; set; }

    /// <summary>Rows read from the card. Only filled by the single-series, network-allowed audit.</summary>
    public int? CardRowCount { get; set; }

    public string Reason { get; set; } = nameof(AnimeClickAuditReason.Ok);

    public string ReasonLabel { get; set; } = string.Empty;

    public List<LibraryAuditSeasonItem> Seasons { get; set; } = [];
}

public sealed class LibraryAuditSeasonItem
{
    public int? SeasonNumber { get; set; }

    public int MissingTitleCount { get; set; }

    public string? AnimeClickId { get; set; }

    /// <summary>True when this card came from the traversal or from an ID stored on the season.</summary>
    public bool CardIsResolved { get; set; }

    /// <summary>Rows read from the card used for this season.</summary>
    public int? CardRowCount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string ReasonLabel { get; set; } = string.Empty;
}

public sealed class LibraryAuditSeriesRequest
{
    public string? ItemId { get; set; }
}

public sealed class RunTaskResponse
{
    public bool Queued { get; set; }

    public string Message { get; set; } = string.Empty;
}


/// <summary>One selectable AI service, as the configuration page needs to render it.</summary>
public sealed class AiProviderInfo
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ChatEndpoint { get; set; } = string.Empty;

    public bool RequiresApiKey { get; set; }

    public bool SupportsModelListing { get; set; }

    public string CredentialUrl { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}
