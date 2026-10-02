using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using MediaBrowser.Controller.Entities;

namespace AnimeClick.Plugin.Services;

/// <summary>Completes individual fields only after the caller has tried AnimeClick.</summary>
public sealed class AnimeClickAnimeTextFallback(AnimeClickTmdbClient tmdb, AnimeClickTvdbClient tvdb,
    AnimeClickAiTranslator translator, AnimeClickTranslationQueue queue)
{
    public async Task<AnimeClickOverviewResolution> ResolveOverviewAsync(string? native,
        IReadOnlyDictionary<string, string> ids, bool isMovie, string? refreshPath,
        PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.EnablePlot) return AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.Disabled, "plot-disabled");
        if (AnimeClickMetadataText.ItalianOverview(native) is { } italian) return AnimeClickOverviewResolution.Found(italian, "native-animeclick");
        return await ResolveAsync(ids, isMovie, "overview", refreshPath, configuration, token).ConfigureAwait(false);
    }

    public async Task FillMissingAsync(BaseItem target, string? currentName, string? path,
        IReadOnlyDictionary<string, string> ids, bool isMovie, PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        target.Name = AnimeClickMetadataText.Title(target.Name)!;
        target.Overview = AnimeClickMetadataText.ItalianOverview(target.Overview);
        target.Genres = AnimeClickMetadataText.Labels(target.Genres);
        target.Tags = AnimeClickMetadataText.Labels(target.Tags);
        if (configuration.PreferItalianTitle && string.IsNullOrWhiteSpace(target.Name)
            && (AnimeClickMetadataText.Title(currentName) is null || IsFileName(currentName, path)))
        {
            var name = await ResolveAsync(ids, isMovie, "name", null, configuration, token).ConfigureAwait(false);
            target.Name = name.Overview!;
        }
        if (configuration.EnablePlot && string.IsNullOrWhiteSpace(target.Overview))
        {
            var overview = await ResolveOverviewAsync(null, ids, isMovie, path, configuration, token).ConfigureAwait(false);
            target.Overview = overview.Overview;
        }
        if (configuration.EnableGenres && target.Genres.Length == 0 && Id(ids, "Tmdb") is { } tmdbId)
            target.Genres = await tmdb.GetAnimeGenresAsync(tmdbId, isMovie, configuration, token).ConfigureAwait(false);
        if (configuration.EnableGenres && target.Genres.Length == 0 && Id(ids, "Tvdb") is { } genreId
            && AnimeClickAiTranslator.IsConfigured(configuration, out _))
        {
            var english = await tvdb.GetAnimeGenresAsync(genreId, isMovie, configuration, token).ConfigureAwait(false);
            target.Genres = await TranslateLabelsAsync(english, "TheTVDB", genreId, "genres").ConfigureAwait(false);
        }
        if (configuration.EnableTags && target.Tags.Length == 0 && Id(ids, "Tmdb") is { } tagId
            && AnimeClickAiTranslator.IsConfigured(configuration, out _))
        {
            var english = await tmdb.GetAnimeKeywordsAsync(tagId, isMovie, configuration, token).ConfigureAwait(false);
            target.Tags = await TranslateLabelsAsync(english, "TMDB", tagId, "tags").ConfigureAwait(false);
        }

        async Task<string[]> TranslateLabelsAsync(string[] labels, string provider, int id, string field)
        {
            if (labels.Length == 0) return [];
            var translated = await translator.TranslateMetadataFieldAsync(JsonSerializer.Serialize(labels), "anime-labels",
                $"{provider}:{(isMovie ? "movie" : "tv")}:{id}", "metadata." + field, "en", "it", configuration, token).ConfigureAwait(false);
            return ParseLabels(translated, labels.Length);
        }
    }

    private async Task<AnimeClickOverviewResolution> ResolveAsync(IReadOnlyDictionary<string, string> ids,
        bool isMovie, string field, string? path, PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sources = new List<(string Name, int Id)>();
        if (configuration.EnableTvdbSynopsis && !string.IsNullOrWhiteSpace(configuration.TvdbApiKey) && Id(ids, "Tvdb") is { } tvdbId)
            sources.Add(("TheTVDB", tvdbId));
        if (!string.IsNullOrWhiteSpace(configuration.TmdbApiKey) && Id(ids, "Tmdb") is { } tmdbId) sources.Add(("TMDB", tmdbId));
        foreach (var source in sources)
            if (Clean(await Fetch(source, true).ConfigureAwait(false)) is { } italian)
                return AnimeClickOverviewResolution.Found(italian, "native-" + source.Name);
        if (!AnimeClickAiTranslator.IsConfigured(configuration, out _))
            return AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.Disabled, "translation-not-configured");
        foreach (var source in sources)
        {
            var english = Clean(await Fetch(source, false).ConfigureAwait(false));
            if (english is null) continue;
            var identity = $"{source.Name}:{(isMovie ? "movie" : "tv")}:{source.Id}";
            var fieldName = (isMovie ? "movie." : "series.") + field;
            if (field == "name")
            {
                var title = Clean(await translator.TranslateMetadataFieldAsync(english, "anime-text", identity,
                    fieldName, "en", "it", configuration, token).ConfigureAwait(false));
                if (title is not null) return AnimeClickOverviewResolution.Found(title, "translated-" + source.Name);
                continue;
            }
            var cached = Clean(await queue.GetCachedTranslationAsync(english, "anime-text", identity, fieldName,
                "en", "it", configuration, token).ConfigureAwait(false));
            if (cached is not null) return AnimeClickOverviewResolution.Found(cached, "translated-" + source.Name);
            var state = await queue.EnqueueAsync(english, "anime-text", identity, fieldName, "en", "it", configuration, token, path).ConfigureAwait(false);
            if (state == AnimeClickTranslationQueueState.Cached)
            {
                cached = Clean(await queue.GetCachedTranslationAsync(english, "anime-text", identity, fieldName,
                    "en", "it", configuration, token).ConfigureAwait(false));
                if (cached is not null) return AnimeClickOverviewResolution.Found(cached, "translated-" + source.Name);
            }
            return state is AnimeClickTranslationQueueState.Queued or AnimeClickTranslationQueueState.AlreadyQueued
                or AnimeClickTranslationQueueState.Backoff or AnimeClickTranslationQueueState.Invalidating
                ? AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.WaitingTranslation, "ai-deferred")
                : AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.Error, "translation-queue-unavailable");
        }
        return AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.NoSource, "no-valid-external-text");

        string? Clean(string? value) => field == "name" ? AnimeClickMetadataText.Title(value) : AnimeClickMetadataText.Clean(value);
        Task<string?> Fetch((string Name, int Id) source, bool italian) => source.Name == "TMDB"
            ? tmdb.GetAnimeTextAsync(source.Id, isMovie, italian ? "it-IT" : "en-US", field, configuration, token)
            : tvdb.GetAnimeTextAsync(source.Id, isMovie, italian ? "ita" : "eng", field, configuration, token);
    }

    internal static string[] ParseLabels(string? json, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != expectedCount) return [];
            var labels = document.RootElement.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? AnimeClickMetadataText.Title(v.GetString()) : null).ToArray();
            return labels.Any(v => v is null) ? [] : labels.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static int? Id(IReadOnlyDictionary<string, string> ids, string provider)
        => ids.TryGetValue(provider, out var value) && int.TryParse(value, out var id) && id > 0 ? id : null;
    private static bool IsFileName(string? name, string? path) => !string.IsNullOrWhiteSpace(path)
        && (string.Equals(name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase));
}
