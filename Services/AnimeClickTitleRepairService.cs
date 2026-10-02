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
}

public interface IAnimeClickTitleResolver
{
    Task<string?> ResolveTitleAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken cancellationToken);
}

/// <summary>Repairs one placeholder; never runs a broad metadata or image refresh.</summary>
public sealed class AnimeClickTitleRepairService(ILibraryManager libraryManager, IAnimeClickTitleResolver resolver)
{
    public async Task<bool> RepairAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken cancellationToken)
    {
        if (AnimeClickRefreshMissingTitlesTask.IsNameLocked(episode) || !AnimeClickRefreshMissingTitlesTask.NeedsTitle(episode)
            || !ProviderEnabled(episode))
            return false;
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
        if (AnimeClickMetadataText.Title(title) is null)
            return false;
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is not null &&
            (refreshedCatalogs.LastSource == "TheTVDB" && (!configuration.EnableTvdbSynopsis || string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
            || refreshedCatalogs.LastSource == "TMDB" && string.IsNullOrWhiteSpace(configuration.TmdbApiKey))) return false;

        // Re-read after network work: an administrator's correction or new identity wins.
        if (libraryManager.GetItemById(episode.Id) is not Episode current
            || AnimeClickRefreshMissingTitlesTask.IsNameLocked(current)
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
            return false;

        current.Name = title;
        try
        {
            await libraryManager.UpdateItemAsync(current, current.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (string.Equals(current.Name, title, StringComparison.Ordinal)) current.Name = expected;
            throw;
        }
        return true;
    }

    private bool ProviderEnabled(Episode episode)
    {
        var fetchers = libraryManager.GetLibraryOptions(episode)?.TypeOptions?
            .FirstOrDefault(option => string.Equals(option.Type, "Episode", StringComparison.OrdinalIgnoreCase))?.MetadataFetchers;
        return fetchers is null || fetchers.Contains("AnimeClick", StringComparer.OrdinalIgnoreCase);
    }
}
