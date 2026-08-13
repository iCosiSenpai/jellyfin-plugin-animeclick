using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public enum AnimeClickOverviewAuditStatus
{
    Italian,
    English,
    Missing,
    Unknown
}

/// <summary>
/// Reads metadata already stored by Jellyfin and queues bounded, non-destructive
/// repairs. The audit itself performs no network or AI request.
/// </summary>
public sealed class AnimeClickLibraryQualityService
{
    public const int MaximumRepairItems = 100;
    private const int PreviewLength = 180;

    private readonly ILibraryManager _libraryManager;
    private readonly AnimeClickMetadataRefreshScheduler _refreshScheduler;
    private readonly AnimeClickRepairLedger _repairLedger;
    private readonly ILogger<AnimeClickLibraryQualityService> _logger;

    public AnimeClickLibraryQualityService(
        ILibraryManager libraryManager,
        AnimeClickMetadataRefreshScheduler refreshScheduler,
        AnimeClickRepairLedger repairLedger,
        ILogger<AnimeClickLibraryQualityService> logger)
    {
        _libraryManager = libraryManager;
        _refreshScheduler = refreshScheduler;
        _repairLedger = repairLedger;
        _logger = logger;
    }

    /// <summary>
    /// Audit with the persisted attempt history loaded first, so the report reflects what previous
    /// runs already discovered.
    /// </summary>
    public async Task<AnimeClickLibraryQualityReport> AuditAsync(CancellationToken cancellationToken)
    {
        await _repairLedger.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return Audit();
    }

    public async Task<AnimeClickLibraryQualityRepairResult> QueueRepairAsync(
        IEnumerable<string>? itemIds,
        bool force,
        CancellationToken cancellationToken)
    {
        await _repairLedger.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return QueueRepair(itemIds, force);
    }

    public AnimeClickLibraryQualityReport Audit()
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = DateTimeOffset.UtcNow;
        var report = new AnimeClickLibraryQualityReport
        {
            MaximumRepairItems = MaximumRepairItems
        };

        var series = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Series>()
            .Where(HasAnimeClickId)
            .ToList();
        var seriesIds = series.Select(item => item.Id).ToHashSet();
        var episodesBySeries = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(episode => seriesIds.Contains(episode.SeriesId))
            .GroupBy(episode => episode.SeriesId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var item in series)
        {
            var children = episodesBySeries.TryGetValue(item.Id, out var found) ? found : [];
            var group = new AnimeClickLibraryQualitySeries
            {
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = item.Name ?? string.Empty,
                Year = item.ProductionYear,
                ItemCount = children.Count + 1
            };
            AddInspection(group, Inspect(item, item.Name, configuration, now));
            foreach (var episode in children
                         .OrderBy(episode => episode.ParentIndexNumber ?? int.MaxValue)
                         .ThenBy(episode => episode.IndexNumber ?? int.MaxValue))
            {
                AddInspection(group, Inspect(episode, item.Name, configuration, now));
            }

            if (group.Items.Count > 0)
            {
                report.Series.Add(group);
            }
        }

        var movies = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Movie>()
            .Where(HasAnimeClickId)
            .ToList();
        foreach (var movie in movies)
        {
            var group = new AnimeClickLibraryQualitySeries
            {
                Id = movie.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = movie.Name ?? string.Empty,
                Year = movie.ProductionYear,
                ItemCount = 1
            };
            AddInspection(group, Inspect(movie, movie.Name, configuration, now));
            if (group.Items.Count > 0)
            {
                report.Series.Add(group);
            }
        }

        report.Series = report.Series
            .OrderByDescending(group => group.EnglishCount + group.MissingCount)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        report.GroupCount = series.Count + movies.Count;
        report.ItemCount = series.Count + movies.Count + episodesBySeries.Values.Sum(list => list.Count);
        report.EnglishCount = report.Series.Sum(group => group.EnglishCount);
        report.MissingCount = report.Series.Sum(group => group.MissingCount);
        report.UnknownCount = report.Series.Sum(group => group.UnknownCount);
        report.LockedCount = report.Series.Sum(group => group.LockedCount);
        report.RepairableCount = report.Series.Sum(group => group.Items.Count(item => item.CanRepair));
        report.WaitingTranslationCount = report.Series.Sum(group => group.WaitingTranslationCount);
        report.NoSourceCount = report.Series.Sum(group => group.NoSourceCount);
        report.AttemptedCount = report.Series.Sum(group => group.Items.Count(item => item.AttemptCount > 0));
        report.SuppressedCount = report.Series.Sum(group => group.Items.Count(item => item.Suppressed));
        report.ItalianCount = report.ItemCount
            - report.EnglishCount
            - report.MissingCount
            - report.UnknownCount;
        return report;
    }

    public AnimeClickLibraryQualityRepairResult QueueRepair(IEnumerable<string>? itemIds)
        => QueueRepair(itemIds, force: false);

    /// <summary>
    /// Queues a bounded set of non-destructive metadata refreshes. Every ID is revalidated against
    /// the current library state, and only English or missing unlocked fields remain eligible.
    /// An item whose last attempt found no source is held back unless <paramref name="force"/> asks
    /// for it explicitly, so a batch is never spent re-trying what cannot succeed.
    /// </summary>
    public AnimeClickLibraryQualityRepairResult QueueRepair(IEnumerable<string>? itemIds, bool force)
    {
        var requested = (itemIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new AnimeClickLibraryQualityRepairResult
        {
            RequestedCount = requested.Count,
            MaximumItems = MaximumRepairItems,
            Truncated = requested.Count > MaximumRepairItems,
            Forced = force
        };
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = DateTimeOffset.UtcNow;

        foreach (var value in requested.Take(MaximumRepairItems))
        {
            result.ConsideredCount++;
            if (!Guid.TryParse(value, out var id)
                || _libraryManager.GetItemById(id) is not BaseItem item
                || !BelongsToAnimeClick(item))
            {
                result.SkippedCount++;
                continue;
            }

            var seriesName = item is Episode episode ? episode.SeriesName : item.Name;
            var inspection = Inspect(item, seriesName, configuration, now, ignoreSuppression: force);
            if (!inspection.CanRepair)
            {
                // Separate counters: "already tried, nothing to fetch" is actionable information,
                // while a locked or already-Italian item is simply not a candidate.
                if (inspection.Suppressed)
                {
                    result.SuppressedCount++;
                }
                else
                {
                    result.SkippedCount++;
                }

                continue;
            }

            if (_refreshScheduler.TryQueue(item, MetadataField.Overview, "library-quality-repair"))
            {
                result.QueuedCount++;
            }
            else
            {
                result.SkippedCount++;
            }
        }

        _logger.LogInformation(
            "AnimeClick library quality repair: requested={Requested} considered={Considered} queued={Queued} skipped={Skipped} suppressed={Suppressed} forced={Forced} truncated={Truncated}",
            result.RequestedCount,
            result.ConsideredCount,
            result.QueuedCount,
            result.SkippedCount,
            result.SuppressedCount,
            result.Forced,
            result.Truncated);
        return result;
    }

    private static bool HasAnimeClickId(BaseItem item)
        => !string.IsNullOrWhiteSpace(item.GetProviderId("AnimeClick"));

    private static bool BelongsToAnimeClick(BaseItem item)
        => item switch
        {
            Series or Movie => HasAnimeClickId(item),
            Episode episode => episode.Series is not null && HasAnimeClickId(episode.Series),
            _ => false
        };

    private AnimeClickLibraryQualityItem Inspect(
        BaseItem item,
        string? seriesName,
        PluginConfiguration configuration,
        DateTimeOffset now,
        bool ignoreSuppression = false)
    {
        var overview = item.Overview?.Trim() ?? string.Empty;
        AnimeClickOverviewAuditStatus status;
        AnimeClickLanguageDetection detection;
        if (overview.Length == 0)
        {
            status = AnimeClickOverviewAuditStatus.Missing;
            detection = new AnimeClickLanguageDetection(AnimeClickTextLanguage.Unknown, 1, 0, 0, 0);
        }
        else
        {
            detection = AnimeClickMetadataLanguageDetector.Detect(overview);
            status = detection.Language switch
            {
                AnimeClickTextLanguage.Italian => AnimeClickOverviewAuditStatus.Italian,
                AnimeClickTextLanguage.English => AnimeClickOverviewAuditStatus.English,
                _ => AnimeClickOverviewAuditStatus.Unknown
            };
        }

        var locked = item.IsLocked || (item.LockedFields?.Contains(MetadataField.Overview) ?? false);
        var featureEnabled = item is Episode
            ? configuration.EnableEpisodeSynopsisTranslation
            : configuration.EnablePlot;

        // What a previous attempt discovered. Language alone cannot say whether a repair is worth
        // queueing: the chain may already have proved that no source carries this synopsis.
        var suppressed = _repairLedger.IsSuppressed(item.Id, now, out var attempt);
        var hasAttempt = attempt is not null;
        var languageRepairable = !locked
            && featureEnabled
            && status is AnimeClickOverviewAuditStatus.English or AnimeClickOverviewAuditStatus.Missing;
        var canRepair = languageRepairable && (ignoreSuppression || !suppressed);
        return new AnimeClickLibraryQualityItem
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ItemType = item switch
            {
                Episode => "Episode",
                Movie => "Movie",
                Series => "Series",
                _ => item.GetType().Name
            },
            Name = item.Name ?? string.Empty,
            SeriesName = seriesName ?? string.Empty,
            SeasonNumber = (item as Episode)?.ParentIndexNumber,
            EpisodeNumber = (item as Episode)?.IndexNumber,
            Status = status.ToString(),
            Confidence = Math.Round(detection.Confidence, 3),
            Locked = locked,
            CanRepair = canRepair,
            LanguageRepairable = languageRepairable,
            Suppressed = languageRepairable && suppressed && !ignoreSuppression,
            RepairState = hasAttempt
                ? AnimeClickRepairLedger.DescribeState(attempt!.Outcome)
                : "never-attempted",
            RepairDetail = hasAttempt ? attempt!.Detail : string.Empty,
            LastAttemptUtc = hasAttempt ? attempt!.AttemptedAt : null,
            AttemptCount = hasAttempt ? attempt!.Attempts : 0,
            Preview = BuildPreview(overview)
        };
    }

    private static void AddInspection(
        AnimeClickLibraryQualitySeries group,
        AnimeClickLibraryQualityItem item)
    {
        switch (item.Status)
        {
            case nameof(AnimeClickOverviewAuditStatus.Italian):
                return;
            case nameof(AnimeClickOverviewAuditStatus.English):
                group.EnglishCount++;
                break;
            case nameof(AnimeClickOverviewAuditStatus.Missing):
                group.MissingCount++;
                break;
            default:
                group.UnknownCount++;
                break;
        }

        if (item.Locked)
        {
            group.LockedCount++;
        }

        if (item.Suppressed)
        {
            if (string.Equals(item.RepairState, "waiting-translation", StringComparison.Ordinal))
            {
                group.WaitingTranslationCount++;
            }
            else if (string.Equals(item.RepairState, "no-source", StringComparison.Ordinal))
            {
                group.NoSourceCount++;
            }
        }

        group.Items.Add(item);
    }

    private static string BuildPreview(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= PreviewLength
            ? compact
            : compact[..PreviewLength].TrimEnd() + "…";
    }
}

public sealed class AnimeClickLibraryQualityReport
{
    public int GroupCount { get; set; }
    public int ItemCount { get; set; }
    public int ItalianCount { get; set; }
    public int EnglishCount { get; set; }
    public int MissingCount { get; set; }
    public int UnknownCount { get; set; }
    public int LockedCount { get; set; }
    public int RepairableCount { get; set; }

    /// <summary>Items excluded from the actionable set because a translation is still pending.</summary>
    public int WaitingTranslationCount { get; set; }

    /// <summary>Items excluded because a previous attempt found no source with this synopsis.</summary>
    public int NoSourceCount { get; set; }

    /// <summary>Items a repair has already been attempted on at least once.</summary>
    public int AttemptedCount { get; set; }

    /// <summary>Items held back by their recorded attempt; an explicit retry can still force them.</summary>
    public int SuppressedCount { get; set; }

    public int MaximumRepairItems { get; set; }
    public List<AnimeClickLibraryQualitySeries> Series { get; set; } = [];
}

public sealed class AnimeClickLibraryQualitySeries
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int ItemCount { get; set; }
    public int EnglishCount { get; set; }
    public int MissingCount { get; set; }
    public int UnknownCount { get; set; }
    public int LockedCount { get; set; }
    public int WaitingTranslationCount { get; set; }
    public int NoSourceCount { get; set; }
    public List<AnimeClickLibraryQualityItem> Items { get; set; } = [];
}

public sealed class AnimeClickLibraryQualityItem
{
    public string Id { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public bool Locked { get; set; }

    /// <summary>True when queueing this item now can still accomplish something.</summary>
    public bool CanRepair { get; set; }

    /// <summary>True when only the stored language makes it a candidate, before attempt history.</summary>
    public bool LanguageRepairable { get; set; }

    /// <summary>True when attempt history is what keeps it out of the actionable set.</summary>
    public bool Suppressed { get; set; }

    /// <summary>never-attempted, applied, waiting-translation, no-source, disabled, blocked or error.</summary>
    public string RepairState { get; set; } = "never-attempted";

    public string RepairDetail { get; set; } = string.Empty;
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public int AttemptCount { get; set; }
    public string Preview { get; set; } = string.Empty;
}

public sealed class AnimeClickLibraryQualityRepairResult
{
    public int RequestedCount { get; set; }
    public int ConsideredCount { get; set; }
    public int QueuedCount { get; set; }
    public int SkippedCount { get; set; }

    /// <summary>Items held back by their recorded attempt rather than by lock or language.</summary>
    public int SuppressedCount { get; set; }

    public bool Forced { get; set; }
    public int MaximumItems { get; set; }
    public bool Truncated { get; set; }
}
