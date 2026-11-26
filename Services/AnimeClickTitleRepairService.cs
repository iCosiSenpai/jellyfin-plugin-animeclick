using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Tasks;
using AnimeClick.Plugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace AnimeClick.Plugin.Services;

public sealed class AnimeClickTitleRepairSession
{
    internal HashSet<string> Refreshed { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, AnimeClickEpisodeCatalog> Catalogs { get; } = new(StringComparer.Ordinal);
    internal Action<string>? ReportPhase { get; set; }
    internal string? LastSource { get; set; }
    internal bool LastUsedAi { get; set; }

    /// <summary>Episodi per cui AnimeClick non ha risposto (verifica anti-bot): l'attività lo dice.</summary>
    internal int AnimeClickUnavailable { get; set; }
}

public interface IAnimeClickTitleResolver
{
    Task<string?> ResolveTitleAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken cancellationToken);
}

/// <summary>Repairs one missing or English name without a metadata or image refresh.</summary>
public sealed class AnimeClickTitleRepairService(ILibraryManager libraryManager, IAnimeClickTitleResolver resolver)
{
    public async Task<bool> RepairAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken cancellationToken)
        => (await RepairCoreAsync(episode, refreshedCatalogs, unlockName: false, cancellationToken).ConfigureAwait(false)).Applied;

    /// <summary>
    /// With <paramref name="unlockName"/> a locked name is repaired too, and its lock is removed in
    /// the same save that writes the title: an episode no source has a title for keeps its lock.
    /// </summary>
    private async Task<(bool Applied, bool Unlocked)> RepairCoreAsync(
        Episode episode,
        AnimeClickTitleRepairSession refreshedCatalogs,
        bool unlockName,
        CancellationToken cancellationToken)
    {
        if ((!unlockName && AnimeClickRefreshMissingTitlesTask.IsNameLocked(episode))
            || !AnimeClickRefreshMissingTitlesTask.NeedsTitle(episode)
            || !ProviderEnabled(episode))
            return (false, false);
        var expected = episode.Name;
        var seriesId = episode.Series?.GetProviderId("AnimeClick");
        var seasonId = episode.Season?.GetProviderId("AnimeClick");
        var number = episode.IndexNumber;
        var numberEnd = episode.IndexNumberEnd;
        var seasonNumber = episode.ParentIndexNumber;
        var path = episode.Path;
        var episodeId = episode.GetProviderId("AnimeClick");
        var seriesTmdb = episode.Series?.GetProviderId("Tmdb");
        var seriesTvdb = episode.Series?.GetProviderId("Tvdb");
        var episodeTmdb = episode.GetProviderId("Tmdb");
        var episodeTvdb = episode.GetProviderId("Tvdb");
        var title = await resolver.ResolveTitleAsync(episode, refreshedCatalogs, cancellationToken).ConfigureAwait(false);
        title = AnimeClickMetadataText.Title(title);
        if (title is null || AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(title)
            || string.Equals(title, expected, StringComparison.Ordinal))
            return (false, false);
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is not null &&
            (refreshedCatalogs.LastSource == "TheTVDB" && (!configuration.EnableTvdbSynopsis || string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
            || refreshedCatalogs.LastSource == "TMDB" && string.IsNullOrWhiteSpace(configuration.TmdbApiKey))) return (false, false);

        // Re-read after network work: an administrator's correction or new identity wins.
        if (libraryManager.GetItemById(episode.Id) is not Episode current
            || (!unlockName && AnimeClickRefreshMissingTitlesTask.IsNameLocked(current))
            || !AnimeClickRefreshMissingTitlesTask.NeedsTitle(current)
            || !string.Equals(current.Name, expected, StringComparison.Ordinal)
            || !string.Equals(current.Series?.GetProviderId("AnimeClick"), seriesId, StringComparison.Ordinal)
            || !string.Equals(current.Season?.GetProviderId("AnimeClick"), seasonId, StringComparison.Ordinal)
            || current.IndexNumber != number || current.ParentIndexNumber != seasonNumber
            || current.IndexNumberEnd != numberEnd || current.Path != path
            || current.GetProviderId("AnimeClick") != episodeId
            || current.Series?.GetProviderId("Tmdb") != seriesTmdb || current.Series?.GetProviderId("Tvdb") != seriesTvdb
            || current.GetProviderId("Tmdb") != episodeTmdb || current.GetProviderId("Tvdb") != episodeTvdb
            || refreshedCatalogs.LastSource is not (null or "AnimeClick") && Plugin.Instance?.Configuration.EnableEpisodeTitleFallback == false
            || refreshedCatalogs.LastUsedAi && Plugin.Instance?.Configuration.EnableAiTranslation == false
            || !ProviderEnabled(current)
            || Plugin.Instance?.Configuration.EnableEpisodeTitles == false)
            return (false, false);

        var wasLocked = current.IsLocked;
        var lockedFields = current.LockedFields;
        var unlocked = unlockName && AnimeClickLocks.Release(current, MetadataField.Name);
        current.Name = title;
        try
        {
            await libraryManager.UpdateItemAsync(current, current.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (string.Equals(current.Name, title, StringComparison.Ordinal)) current.Name = expected;
            if (unlocked)
            {
                current.IsLocked = wasLocked;
                current.LockedFields = lockedFields;
            }

            throw;
        }
        return (true, unlocked);
    }

    /// <summary>
    /// Repairs the titles of one series inside the request the page is waiting on.
    /// <para>
    /// With <paramref name="unlock"/>, an episode loses its lock only when a source has its title, in
    /// the same save that writes it: a lock protects a manual edit, and the series, its seasons, the
    /// episodes whose title is already fine, those no source has a title for and those this call
    /// does not reach keep theirs.
    /// </para>
    /// <para>
    /// A long series whose titles need other sources or a translation can take minutes, so the work
    /// stops at <paramref name="budget"/> and reports how many episodes are left, instead of the page
    /// timing out with nothing to show. One slow episode cannot run away with the request either:
    /// past the budget plus <see cref="OvertimeAllowance"/> the episode in hand is abandoned.
    /// </para>
    /// <para>
    /// <paramref name="continueAfter"/> is the episode a previous call stopped at. Without it the
    /// next call would start again from the episodes just found to have no title anywhere, and a
    /// series that opens with enough of them would never get past them.
    /// </para>
    /// </summary>
    public async Task<AnimeClickSeriesTitleRepair> RepairSeriesAsync(
        IReadOnlyList<Episode> episodes,
        bool unlock,
        TimeSpan budget,
        CancellationToken cancellationToken,
        Guid? continueAfter = null)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var resumeAt = 0;
        if (continueAfter is { } cursor)
        {
            for (var index = 0; index < episodes.Count; index++)
            {
                if (episodes[index].Id == cursor)
                {
                    resumeAt = index + 1;
                    break;
                }
            }
        }

        // The same conditions RepairAsync writes under, apart from the lock: an episode is unlocked
        // only for a repair that is allowed to write.
        var titlesEnabled = Plugin.Instance?.Configuration.EnableEpisodeTitles != false;
        var candidates = episodes
            .Skip(resumeAt)
            .Where(episode => titlesEnabled
                && AnimeClickRefreshMissingTitlesTask.NeedsTitle(episode)
                && ProviderEnabled(episode)
                && (unlock || !AnimeClickRefreshMissingTitlesTask.IsNameLocked(episode)))
            .ToList();
        var session = new AnimeClickTitleRepairSession();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var hardStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardStop.CancelAfter(budget + OvertimeAllowance);
        var checkedCount = 0;
        var applied = 0;
        var unlocked = 0;
        Guid? lastChecked = null;
        foreach (var episode in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkedCount > 0 && clock.Elapsed > budget)
            {
                break;
            }

            try
            {
                var outcome = await RepairCoreAsync(episode, session, unlock, hardStop.Token).ConfigureAwait(false);
                if (outcome.Applied)
                {
                    applied++;
                }

                if (outcome.Unlocked)
                {
                    unlocked++;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Out of time in the middle of this episode: it is left for the next call.
                break;
            }

            checkedCount++;
            lastChecked = episode.Id;
        }

        var remaining = candidates.Count - checkedCount;
        return new AnimeClickSeriesTitleRepair(checkedCount, applied, unlocked, remaining, remaining > 0 ? lastChecked ?? continueAfter : null);
    }

    /// <summary>
    /// How long a single-series repair keeps starting new episodes. The page waits 130 seconds for
    /// the answer (the default of its <c>request()</c>): this, plus <see cref="OvertimeAllowance"/>,
    /// plus the audit of the series that follows, has to stay inside that.
    /// </summary>
    public static readonly TimeSpan SeriesBudget = TimeSpan.FromSeconds(75);

    /// <summary>How far past its budget a single-series repair lets the episode in hand run.</summary>
    internal static readonly TimeSpan OvertimeAllowance = TimeSpan.FromSeconds(15);

    private bool ProviderEnabled(Episode episode)
    {
        var fetchers = libraryManager.GetLibraryOptions(episode)?.TypeOptions?
            .FirstOrDefault(option => string.Equals(option.Type, "Episode", StringComparison.OrdinalIgnoreCase))?.MetadataFetchers;
        return fetchers is null || fetchers.Contains("AnimeClick", StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What one single-series title repair did, and how much it left for the next call.</summary>
/// <param name="ContinueAfter">The episode to resume after, while <paramref name="Remaining"/> is not zero.</param>
public sealed record AnimeClickSeriesTitleRepair(int Checked, int Applied, int Unlocked, int Remaining, Guid? ContinueAfter = null);
