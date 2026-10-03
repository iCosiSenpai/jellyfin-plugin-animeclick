using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Tasks;

/// <summary>
/// Re-reads resolved AnimeClick catalogs and repairs placeholder titles sequentially.
/// Progress describes completed inspections and saves, rather than queued full refreshes.
/// </summary>
public class AnimeClickRefreshMissingTitlesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly AnimeClickTitleRepairService _repair;
    private readonly AnimeClickActivityService _activities;
    private readonly ILogger<AnimeClickRefreshMissingTitlesTask> _logger;

    public AnimeClickRefreshMissingTitlesTask(
        ILibraryManager libraryManager,
        AnimeClickTitleRepairService repair,
        AnimeClickActivityService activities,
        ILogger<AnimeClickRefreshMissingTitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _repair = repair;
        _activities = activities;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AnimeClick: sistema i titoli episodio";

    /// <inheritdoc />
    public string Key => "AnimeClickRefreshMissingEpisodeTitles";

    /// <inheritdoc />
    public string Description =>
        "Completa i titoli vuoti, generici o derivati dal file e converte quelli riconoscibilmente inglesi. "
        + "Cerca prima su AnimeClick, poi nelle fonti configurate e traduce solo se necessario. "
        + "Conserva titoli italiani o incerti, identificativi, numerazione, trame e immagini; "
        + "rispetta i blocchi e le modifiche intervenute durante la ricerca.";

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
        const string key = AnimeClickActivityService.Titles;
        if (!_activities.Begin(key)) return;
        var processed = 0;
        var applied = 0;
        var errors = 0;
        var alternativeTitles = 0;
        var translatedTitles = 0;
        try
        {
            var configuration = Plugin.Instance?.Configuration ?? new AnimeClick.Plugin.Configuration.PluginConfiguration();
            if (!configuration.EnableEpisodeTitles)
            {
                _activities.Finish(key, "Completed", "I titoli episodio sono disabilitati nelle preferenze.");
                progress.Report(100);
                return;
            }
            var candidates = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsVirtualItem = false
            }).OfType<Episode>()
                .Where(episode => !IsNameLocked(episode) && NeedsTitle(episode))
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Series?.GetProviderId("AnimeClick"))
                    || !string.IsNullOrWhiteSpace(episode.Season?.GetProviderId("AnimeClick"))
                    || configuration.EnableEpisodeTitleFallback
                        && _libraryManager.GetLibraryOptions(episode)?.TypeOptions?
                            .FirstOrDefault(option => string.Equals(option.Type, "Episode", StringComparison.OrdinalIgnoreCase))?
                            .MetadataFetchers?.Contains("AnimeClick", StringComparer.OrdinalIgnoreCase) == true &&
                        (!string.IsNullOrWhiteSpace(configuration.TmdbApiKey)
                            && !string.IsNullOrWhiteSpace(episode.Series?.GetProviderId("Tmdb"))
                        || configuration.EnableTvdbSynopsis && !string.IsNullOrWhiteSpace(configuration.TvdbApiKey)
                            && !string.IsNullOrWhiteSpace(episode.Series?.GetProviderId("Tvdb"))))
                .OrderBy(episode => episode.SeriesName, StringComparer.Ordinal)
                .ThenBy(episode => episode.ParentIndexNumber).ThenBy(episode => episode.IndexNumber)
                .ToList();
            var refreshedCatalogs = new AnimeClickTitleRepairSession();
            _activities.Update(key, candidates.Count, 0, 0, 0, 0, "Ricontrollo dei titoli mancanti e inglesi…");
            foreach (var episode in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_activities.Get(key).State == "Cancelling") throw new OperationCanceledException();
                _activities.Update(key, candidates.Count, processed, applied, processed - applied - errors, errors,
                    $"{episode.SeriesName} · S{episode.ParentIndexNumber} E{episode.IndexNumber}: lettura del titolo…");
                refreshedCatalogs.LastSource = null;
                refreshedCatalogs.LastUsedAi = false;
                refreshedCatalogs.ReportPhase = phase => _activities.Update(key, candidates.Count, processed,
                    applied, processed - applied - errors, errors,
                    $"{episode.SeriesName} · S{episode.ParentIndexNumber} E{episode.IndexNumber}: {phase}");
                try
                {
                    if (await _repair.RepairAsync(episode, refreshedCatalogs, cancellationToken).ConfigureAwait(false))
                    {
                        applied++;
                        if (refreshedCatalogs.LastSource is not (null or "AnimeClick")) alternativeTitles++;
                        if (refreshedCatalogs.LastUsedAi) translatedTitles++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "AnimeClick title-only repair failed for item={ItemId}", episode.Id);
                }
                processed++;
                progress.Report(processed * 100d / Math.Max(1, candidates.Count));
                _activities.Update(key, candidates.Count, processed, applied, processed - applied - errors, errors,
                    $"{processed}/{candidates.Count} episodi verificati · {applied} titoli aggiornati");
            }
            _activities.Finish(key, errors > 0 ? "Partial" : "Completed",
                candidates.Count == 0 ? "Nessun titolo mancante o inglese da sistemare. I titoli italiani e incerti sono conservati."
                : $"{applied} titoli aggiornati ({alternativeTitles} da altre fonti, {translatedTitles} tradotti) · "
                    + $"{processed - applied - errors} senza titolo disponibile o saltati · {errors} errori.");
            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            _activities.Finish(key, "Cancelled", $"Interrotto: {processed} episodi verificati, {applied} titoli aggiornati.");
            if (cancellationToken.IsCancellationRequested) throw;
        }
        catch (Exception)
        {
            _activities.Finish(key, "Failed", "Ricontrollo non riuscito. Consulta la diagnostica e riprova.");
            throw;
        }
    }

    /// <summary>True when Jellyfin must not let an automated refresh alter the title.</summary>
    internal static bool IsNameLocked(Episode episode)
        => episode.IsLocked
            || (episode.LockedFields?.Contains(MetadataField.Name) ?? false);

    /// <summary>
    /// Selects missing, generic, file-derived or confidently English names.
    /// Lock checks are separate so audits can still report locked candidates.
    /// </summary>
    internal static bool NeedsTitle(Episode episode)
    {
        var name = episode.Name;
        if (AnimeClickMetadataText.Title(name) is null)
        {
            return true;
        }

        if (AnimeClickHtmlParser.IsPlaceholderEpisodeText(name))
        {
            return true;
        }

        if (AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(name)) return true;

        var path = episode.Path;
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(
                Path.GetFileNameWithoutExtension(path),
                name.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }
}
