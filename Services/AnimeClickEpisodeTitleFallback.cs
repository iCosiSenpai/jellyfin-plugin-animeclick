using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public sealed record AnimeClickEpisodeTitleRequest(IReadOnlyDictionary<string, string> SeriesIds,
    IReadOnlyDictionary<string, string> EpisodeIds, int Season, int Episode, int? EpisodeEnd = null);

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

        // A native Italian title from either source always wins over a translation.
        foreach (var source in sources)
        {
            phase?.Invoke($"Cerco un titolo italiano su {source.Name}…");
            var title = await Fetch(source, true).ConfigureAwait(false);
            if (CleanTitle(title) is { } native) return new(native, source.Name, false);
        }
        if (!AnimeClickAiTranslator.IsConfigured(configuration, out _)) return null;
        foreach (var source in sources)
        {
            phase?.Invoke($"Cerco un titolo inglese su {source.Name}…");
            var english = CleanTitle(await Fetch(source, false).ConfigureAwait(false));
            if (english is null) continue;
            phase?.Invoke($"Traduco il titolo da {source.Name} in italiano…");
            var translated = await translator.TranslateMetadataFieldAsync(english, "episode-title",
                $"{source.Name}:{source.Id}:{source.EpisodeId}:S{request.Season}E{request.Episode}",
                "episode-title", "en", "it", configuration, token).ConfigureAwait(false);
            if (CleanTitle(translated) is { } italian) return new(italian, source.Name, true);
        }
        return null;

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
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Contains('\n') || value.Contains('\r')) return null;
        var title = AnimeClickAiTranslator.NormalizeSourceText(value).Trim();
        return title.Length == 0 || AnimeClickHtmlParser.IsPlaceholderEpisodeText(title)
            || title.StartsWith("```", StringComparison.Ordinal) || title.StartsWith('{') || title.StartsWith('[') ? null : title;
    }
}
