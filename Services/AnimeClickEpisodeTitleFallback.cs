using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public sealed record AnimeClickEpisodeTitleRequest(IReadOnlyDictionary<string, string> SeriesIds,
    IReadOnlyDictionary<string, string> EpisodeIds, int Season, int Episode, int? EpisodeEnd = null,
    string? AnimeClickEnglishTitle = null, string? AnimeClickIdentity = null, string? ExistingEnglishTitle = null);

public sealed record AnimeClickEpisodeTitleResult(string Title, string Source, bool UsedAi);

/// <summary>Uses known external identities; never searches a series by a guessed title.</summary>
public sealed class AnimeClickEpisodeTitleFallback(AnimeClickTmdbClient tmdb, AnimeClickTvdbClient tvdb,
    AnimeClickAiTranslator translator, ILogger<AnimeClickEpisodeTitleFallback> logger)
{
    public async Task<AnimeClickEpisodeTitleResult?> ResolveAsync(AnimeClickEpisodeTitleRequest request,
        PluginConfiguration configuration, CancellationToken token, Action<string>? phase = null)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.EnableEpisodeTitles || !configuration.EnableEpisodeTitleFallback
            || request.Season < 0 || request.Episode < 0 || request.EpisodeEnd > request.Episode) return null;
        var sources = new List<(string Name, int Id, string? EpisodeId)>();
        Add("TheTVDB", "Tvdb", configuration.EnableTvdbSynopsis && !string.IsNullOrWhiteSpace(configuration.TvdbApiKey));
        Add("TMDB", "Tmdb", !string.IsNullOrWhiteSpace(configuration.TmdbApiKey));

        var animeClickEnglish = CleanTitle(request.AnimeClickEnglishTitle);
        if (!AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(animeClickEnglish)) animeClickEnglish = null;
        var existingEnglish = CleanTitle(request.ExistingEnglishTitle);
        if (!AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(existingEnglish)) existingEnglish = null;
        var expectedEnglish = animeClickEnglish ?? existingEnglish;

        // A native Italian title from either source always wins over a translation.
        foreach (var source in sources)
        {
            phase?.Invoke($"Cerco un titolo italiano su {source.Name}…");
            var title = await Fetch(source, true).ConfigureAwait(false);
            if (CleanTitle(title) is not { } native
                || AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(native)) continue;
            // Sequel cards can restart at S1 while external IDs still name the entire
            // franchise. Verify the original title before accepting that coordinate.
            if (expectedEnglish is not null
                && !AnimeClickEpisodeProviderId.TitlesEquivalent(expectedEnglish, CleanTitle(await Fetch(source, false).ConfigureAwait(false)))) continue;
            return new(native, source.Name, false);
        }
        if (!AnimeClickAiTranslator.IsConfigured(configuration, out _)) return null;
        if (animeClickEnglish is not null)
        {
            var translated = await Translate(animeClickEnglish, "AnimeClick", request.AnimeClickIdentity ?? "").ConfigureAwait(false);
            if (translated is not null) return new(translated, "AnimeClick", true);
        }
        foreach (var source in sources)
        {
            phase?.Invoke($"Cerco un titolo inglese su {source.Name}…");
            var english = CleanTitle(await Fetch(source, false).ConfigureAwait(false));
            if (english is null || expectedEnglish is not null
                && !AnimeClickEpisodeProviderId.TitlesEquivalent(expectedEnglish, english)) continue;
            var translated = await Translate(english, source.Name, $"{source.Id}:{source.EpisodeId}").ConfigureAwait(false);
            if (translated is not null) return new(translated, source.Name, true);
        }
        // A meaningful stored English title can be translated without borrowing the
        // metadata of another season. File-derived names never enter this request.
        if (existingEnglish is not null && (animeClickEnglish is null
            || AnimeClickEpisodeProviderId.TitlesEquivalent(existingEnglish, animeClickEnglish)))
        {
            var translated = await Translate(existingEnglish, "Jellyfin", request.AnimeClickIdentity
                ?? string.Join(':', request.SeriesIds.OrderBy(pair => pair.Key).Select(pair => pair.Value))).ConfigureAwait(false);
            if (translated is not null) return new(translated, "Jellyfin", true);
        }
        return null;

        async Task<string?> Translate(string english, string source, string identity)
        {
            phase?.Invoke(source == "Jellyfin" ? "Traduco in italiano il titolo già presente…" : $"Traduco il titolo da {source} in italiano…");
            var translated = await translator.TranslateMetadataFieldAsync(english, "episode-title",
                $"{source}:{identity}:S{request.Season}E{request.Episode}",
                "episode-title", "en", "it", configuration, token).ConfigureAwait(false);
            return CleanTitle(translated) is { } italian
                && !AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(italian)
                && !string.Equals(italian, english, StringComparison.OrdinalIgnoreCase) ? italian : null;
        }

        void Add(string name, string provider, bool enabled)
        {
            if (!enabled || !request.SeriesIds.TryGetValue(provider, out var value)
                || !int.TryParse(value, out var id) || id <= 0) return;
            request.EpisodeIds.TryGetValue(provider, out var expected);
            if (!string.IsNullOrWhiteSpace(expected))
            {
                if (!int.TryParse(expected, out var episodeId) || episodeId <= 0) return;
                expected = episodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else expected = null;
            sources.Add((name, id, expected));
        }
        async Task<string?> Fetch((string Name, int Id, string? EpisodeId) source, bool italian)
        {
            try
            {
                return source.Name == "TMDB"
                    ? await tmdb.GetEpisodeTitleAsync(source.Id, request.Season, request.Episode, italian ? "it-IT" : "en-US",
                        source.EpisodeId, configuration, token).ConfigureAwait(false)
                    : await tvdb.GetEpisodeTitleAsync(source.Id, request.Season, request.Episode, italian ? "ita" : "eng",
                        source.EpisodeId, configuration, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                logger.LogWarning("Episode title unavailable from {Source}; continuing with configured alternatives", source.Name);
                return null;
            }
        }
    }

    internal static string? CleanTitle(string? value)
    {
        return AnimeClickMetadataText.Title(value);
    }

    internal static string? ExistingEnglishTitle(string? name, string? path)
    {
        var title = CleanTitle(name);
        if (!AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(title)
            || !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetFileNameWithoutExtension(path), name?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return title;
    }
}
