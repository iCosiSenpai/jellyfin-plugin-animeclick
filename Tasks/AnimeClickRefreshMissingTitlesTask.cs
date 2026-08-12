using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Tasks;

/// <summary>
/// Re-reads the AnimeClick episode list for the episodes whose identity is known but whose title
/// is still a placeholder.
/// <para>
/// AnimeClick publishes an episode row as soon as it airs and fills the Italian title later, so a
/// weekly show is matched — the row exists, the identity is written — while its title is still
/// "Episodio 17". The provider correctly refuses to copy that placeholder, but nothing ever goes
/// back to look: Jellyfin only refreshes an episode when its file changes, so the real title
/// published days later never arrives. This task closes that loop, and only for episodes where
/// the work is already done: an AnimeClick episode ID is present and the title is still a
/// placeholder or the bare file name.
/// </para>
/// </summary>
public class AnimeClickRefreshMissingTitlesTask : IScheduledTask
{
    /// <summary>
    /// Upper bound on the episodes queued by one run. Every one of them costs at least one
    /// AnimeClick request, paced by the plugin's own one-second gate, so an unbounded task on a
    /// large library would hammer the site for hours.
    /// </summary>
    private const int MaximumEpisodesPerRun = 200;
    private const string CandidateCursorCacheKey = "taskState::missingEpisodeTitlesCursor:v1";

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly AnimeClickEpisodeLayoutResolver _layoutResolver;
    private readonly ILogger<AnimeClickRefreshMissingTitlesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeClickRefreshMissingTitlesTask"/> class.
    /// </summary>
    public AnimeClickRefreshMissingTitlesTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        AnimeClickCacheService cache,
        AnimeClickSeasonResolver seasonResolver,
        AnimeClickEpisodeLayoutResolver layoutResolver,
        ILogger<AnimeClickRefreshMissingTitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _cache = cache;
        _seasonResolver = seasonResolver;
        _layoutResolver = layoutResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AnimeClick: ricontrolla i titoli episodio mancanti";

    /// <inheritdoc />
    public string Key => "AnimeClickRefreshMissingEpisodeTitles";

    /// <inheritdoc />
    public string Description =>
        "Rilegge la lista episodi di AnimeClick per i titoli segnaposto, derivati dal nome file "
        + "o rimasti in una lingua diversa dopo che AnimeClick ha pubblicato il titolo italiano. "
        + "Confronta l'identità numerica stabile della riga, rispetta i campi bloccati e salta "
        + "le schede che non possono migliorare. "
        + $"Ogni esecuzione ne accoda al massimo {MaximumEpisodesPerRun}.";

    /// <inheritdoc />
    public string Category => "AnimeClick";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!configuration.EnableEpisodeTitles)
        {
            _logger.LogInformation(
                "AnimeClick: i titoli episodio sono disabilitati, il ricontrollo non ha nulla da fare");
            progress.Report(100);
            return;
        }

        var candidates = await FindCandidatesAsync(configuration, cancellationToken).ConfigureAwait(false);
        progress.Report(5);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("AnimeClick: nessun episodio recuperabile con un ricontrollo");
            progress.Report(100);
            return;
        }

        var queued = 0;
        foreach (var episode in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.None,

                // Never ReplaceAllMetadata: that path sets RemoveOldMetadata, which is how a
                // refresh erases the episode numbering parsed from the file name. The Italian
                // title still lands, because the post-merge authority provider reapplies it.
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                IsAutomated = true
            };
            _providerManager.QueueRefresh(episode.Id, options, RefreshPriority.Low);
            queued++;
            progress.Report(5 + (95d * queued / candidates.Count));
        }

        _logger.LogInformation(
            "AnimeClick: accodato il ricontrollo del titolo per {Queued} episodi (tetto {Cap} per esecuzione)",
            queued,
            MaximumEpisodesPerRun);
        progress.Report(100);
    }

    /// <summary>
    /// Picks the episodes worth a request, in the order most likely to gain a title.
    /// <para>
    /// The first version queued the first two hundred episodes it found that carried an AnimeClick
    /// ID and a placeholder title. Both halves of that rule were wrong on a real library. Requiring
    /// an ID meant an episode that was never matched — because it was added while the card was still
    /// bare — could never be retried, since Jellyfin only refreshes an episode when its file
    /// changes. And taking them in library order spent the whole budget on the cards that publish no
    /// titles at all: a hundred and eighteen episodes here, week after week, none of which can ever
    /// gain one. So the audit's own classification decides, and its verdict orders the queue.
    /// </para>
    /// </summary>
    private async Task<List<BaseItem>> FindCandidatesAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        });

        // Ranked by expected yield. A cached catalog that already holds the title costs no request
        // at all; a mismatch, whose data has not changed since it failed, comes last.
        var priority = new Dictionary<AnimeClickAuditReason, int>
        {
            [AnimeClickAuditReason.PendingRefresh] = 0,
            [AnimeClickAuditReason.RowVanished] = 1,
            [AnimeClickAuditReason.TitleNotPublished] = 2,
            [AnimeClickAuditReason.CatalogNotCached] = 3,
            [AnimeClickAuditReason.NotMatched] = 4
        };

        var catalogs = new Dictionary<string, AnimeClickEpisodeCatalog?>(StringComparer.OrdinalIgnoreCase);
        var seasonMaps = new Dictionary<(string SeriesId, int SeasonNumber, int? AirYear), string?>();
        var ranked = new List<(int Priority, Episode Episode)>();
        foreach (var item in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not Episode episode || IsNameLocked(episode))
            {
                continue;
            }

            var seriesId = episode.Series?.GetProviderId("AnimeClick");
            if (string.IsNullOrWhiteSpace(seriesId))
            {
                continue;
            }

            var seasonId = episode.Season?.GetProviderId("AnimeClick");
            string? traversedCard = null;
            if (episode.ParentIndexNumber is > 1)
            {
                var layout = _layoutResolver.Resolve(episode.Path);
                var airYears = layout?.GetSeasonAirYears();
                var airYear = airYears?.GetValueOrDefault(episode.ParentIndexNumber.Value);
                var seasonMapKey = (seriesId, episode.ParentIndexNumber.Value, airYear);
                if (!seasonMaps.TryGetValue(seasonMapKey, out traversedCard))
                {
                    traversedCard = await _seasonResolver
                        .ResolveCachedAsync(
                            seriesId,
                            episode.ParentIndexNumber,
                            configuration,
                            cancellationToken,
                            airYears)
                        .ConfigureAwait(false);
                    seasonMaps[seasonMapKey] = traversedCard;
                }
            }

            // Match the provider's card priority while staying cache-only: a traversal already
            // proved during a real refresh wins, then an explicit season ID, then the series card.
            var cardId = traversedCard
                ?? (string.IsNullOrWhiteSpace(seasonId) ? seriesId : seasonId);
            var catalog = await GetCachedCatalogAsync(cardId, configuration, catalogs, cancellationToken)
                .ConfigureAwait(false);

            var reason = AnimeClickLibraryAudit.ClassifyEpisode(
                episode.GetProviderId("AnimeClick"),
                episode.Name,
                NeedsTitle(episode),
                catalog);
            if (!priority.TryGetValue(reason, out var rank))
            {
                // CardHasNoTitles and NotIdentified: nothing a request could change.
                continue;
            }

            ranked.Add((rank, episode));
        }

        var ordered = ranked
            .OrderBy(entry => entry.Priority)

            // Keep a total order before applying the persisted cursor. Stable ties make the cursor
            // meaningful across runs instead of depending on the database's incidental row order.
            .ThenBy(entry => entry.Episode.SeriesName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => entry.Episode.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(entry => entry.Episode.IndexNumber ?? int.MaxValue)
            .ThenBy(entry => entry.Episode.Id)
            .Select(entry => entry.Episode)
            .ToList();
        if (ordered.Count <= MaximumEpisodesPerRun)
        {
            return [.. ordered.Select(episode => (BaseItem)episode)];
        }

        // A hard cap without rotation permanently starves every candidate after the first 200 when
        // upstream keeps those first rows unresolved. Persist a circular cursor so each weekly run
        // starts where the previous one stopped while the first run still favors the highest yield.
        var cursor = await _cache
            .GetAsync<int>(CandidateCursorCacheKey, cancellationToken)
            .ConfigureAwait(false);
        var window = SelectRotatingWindow(ordered, cursor, MaximumEpisodesPerRun);
        await _cache
            .SetAsync(
                CandidateCursorCacheKey,
                window.NextCursor!.Value,
                cancellationToken)
            .ConfigureAwait(false);
        return [.. window.Items.Select(episode => (BaseItem)episode)];
    }

    /// <summary>
    /// Selects a bounded circular window without touching persistent state. A null next cursor
    /// means the full input fits and callers do not need to persist rotation state.
    /// </summary>
    internal static RotatingWindow<T> SelectRotatingWindow<T>(
        IReadOnlyList<T> items,
        int cursor,
        int cap)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (cap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cap));
        }

        if (items.Count <= cap)
        {
            return new RotatingWindow<T>([.. items], null);
        }

        var offset = cursor % items.Count;
        if (offset < 0)
        {
            offset += items.Count;
        }

        var selected = items
            .Skip(offset)
            .Concat(items.Take(offset))
            .Take(cap)
            .ToList();
        return new RotatingWindow<T>(selected, (offset + cap) % items.Count);
    }

    internal sealed record RotatingWindow<T>(IReadOnlyList<T> Items, int? NextCursor);

    /// <summary>The cached catalog for one card, memoized, never fetched.</summary>
    private async Task<AnimeClickEpisodeCatalog?> GetCachedCatalogAsync(
        string animeClickId,
        PluginConfiguration configuration,
        Dictionary<string, AnimeClickEpisodeCatalog?> memo,
        CancellationToken cancellationToken)
    {
        if (memo.TryGetValue(animeClickId, out var cached))
        {
            return cached;
        }

        AnimeClickEpisodeCatalog? catalog = null;
        if (AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var animeUrl))
        {
            var summary = await _cache
                .GetAsync<AnimeClickAnime>($"anime::{animeUrl}", configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            catalog = await _cache
                .GetAsync<AnimeClickEpisodeCatalog>(
                    AnimeClickEpisodeProvider.BuildCatalogCacheKey(
                        animeClickId,
                        summary?.EpisodeCount,
                        summary?.SeasonsCount ?? 0),
                    configuration.CacheHours,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        memo[animeClickId] = catalog;
        return catalog;
    }

    /// <summary>True when Jellyfin must not let an automated refresh alter the title.</summary>
    internal static bool IsNameLocked(Episode episode)
        => episode.IsLocked
            || (episode.LockedFields?.Contains(MetadataField.Name) ?? false);

    /// <summary>
    /// True when the stored name carries no information: a number restated as a title, or the
    /// bare file name Jellyfin falls back to. A locked name is never touched.
    /// </summary>
    internal static bool NeedsTitle(Episode episode)
    {
        var name = episode.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (AnimeClickHtmlParser.IsPlaceholderEpisodeText(name))
        {
            return true;
        }

        var path = episode.Path;
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(
                Path.GetFileNameWithoutExtension(path),
                name.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }
}
