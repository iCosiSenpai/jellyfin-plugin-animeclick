using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Provides episode-level metadata (Italian titles) for anime from AnimeClick.
/// Fetches the episode list from /episodi and matches by episode number.
/// Resolves season-specific AnimeClick pages via relations for multi-season shows.
/// </summary>
public class AnimeClickEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickEpisodeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickTmdbClient _tmdbClient;
    private readonly AnimeClickOllamaTranslator _translator;

    public AnimeClickEpisodeProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickEpisodeProvider> logger,
        IHttpClientFactory httpClientFactory,
        AnimeClickTmdbClient tmdbClient,
        AnimeClickOllamaTranslator translator)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _tmdbClient = tmdbClient;
        _translator = translator;
    }

    public string Name => "AnimeClick";
    /// <summary>
    /// Run AFTER the other metadata providers so that fields we don't populate
    /// are filled in first by AniList / TheMovieDb / OMDb, and only then we
    /// overlay the Italian episode title.
    /// </summary>
    public int Order => 100;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Episode> { Item = new Episode() };

        _logger.LogInformation(
            "AnimeClick EpisodeProvider.GetMetadata called: name=\"{Name}\" S{Season}E{Episode} seriesProviderId={SeriesProviderId} path={Path}",
            info.Name,
            info.ParentIndexNumber,
            info.IndexNumber,
            info.SeriesProviderIds?.GetValueOrDefault("AnimeClick") ?? "<none>",
            info.Path ?? "<none>");

        if (!configuration.EnableEpisodeTitles)
        {
            var id = info.GetProviderId("AnimeClick");
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Item.SetProviderId("AnimeClick", id);
                result.HasMetadata = true;
            }
            return result;
        }

        // Get AnimeClick ID from parent series
        var mainAnimeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick")
                               ?? info.GetProviderId("AnimeClick");

        if (string.IsNullOrWhiteSpace(mainAnimeClickId))
        {
            return result;
        }

        var episodeNumber = info.IndexNumber;
        if (!episodeNumber.HasValue || episodeNumber.Value <= 0)
        {
            return result;
        }

        var seasonNumber = info.ParentIndexNumber;

        // Resolve season-specific AnimeClick page for multi-season shows
        var animeClickId = await ResolveSeasonAnimeClickIdAsync(
            mainAnimeClickId, seasonNumber, configuration, cancellationToken)
            ?? mainAnimeClickId;

        try
        {
            // Try to get cached episode list
            var cacheKey = $"episodes:v2::{animeClickId}";
            var cachedEpisodes = await _cache.GetAsync<List<AnimeClickEpisode>>(cacheKey, configuration.CacheHours, cancellationToken);
            _logger.LogDebug("AnimeClick episodes cache {State}: {Key}", cachedEpisodes is null ? "miss" : "hit", cacheKey);

            List<AnimeClickEpisode>? episodes = cachedEpisodes;

            if (episodes is null || episodes.Count == 0)
            {
                // Fetch episodes page
                var animeUrl = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
                var episodesUrl = animeUrl + "/episodi";

                _logger.LogInformation("AnimeClick: Fetching episodes from {Url}", episodesUrl);
                var html = await _client.GetStringAsync(episodesUrl, configuration, cancellationToken);
                episodes = _parser.ParseEpisodesPage(html, configuration.BaseUrl);
                _logger.LogInformation("AnimeClick: Parsed {Count} episodes from {Url}", episodes.Count, episodesUrl);

                // If there are more pages, fetch them too (pagination)
                for (var page = 2; page <= 30; page++)
                {
                    var nextUrl = episodesUrl + $"?page={page}";
                    try
                    {
                        var nextHtml = await _client.GetStringAsync(nextUrl, configuration, cancellationToken);
                        var nextEpisodes = _parser.ParseEpisodesPage(nextHtml, configuration.BaseUrl);
                        if (nextEpisodes.Count == 0) break;

                        if (episodes.Any(e => e.Number == nextEpisodes[0].Number
                            && e.SeasonNumber == nextEpisodes[0].SeasonNumber)) break;

                        episodes.AddRange(nextEpisodes);
                        _logger.LogInformation("AnimeClick: Parsed {Count} more episodes from {Url}", nextEpisodes.Count, nextUrl);
                    }
                    catch
                    {
                        break;
                    }
                }

                await _cache.SetAsync(cacheKey, episodes, cancellationToken);
                _logger.LogInformation("AnimeClick: Cached {Count} episodes for {Id}", episodes.Count, animeClickId);
            }

            var episodeMatch = AnimeClickEpisodeMatcher.Match(episodes, seasonNumber, episodeNumber.Value);
            var match = episodeMatch.Episode;
            _logger.LogInformation(
                "AnimeClick: Episode match strategy={Strategy} animeClickId={Id} S{Season}E{Episode}",
                episodeMatch.Strategy,
                animeClickId,
                seasonNumber,
                episodeNumber.Value);

            if (match is not null && !string.IsNullOrWhiteSpace(match.Title))
            {
                var isGeneric = System.Text.RegularExpressions.Regex.IsMatch(
                    match.Title, @"^Episodio\s+\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!isGeneric)
                {
                    result.Item.Name = match.Title;
                    result.Item.SetProviderId("AnimeClick", match.ProviderId ?? animeClickId);

                    if (match.DurationMinutes.HasValue)
                    {
                        result.Item.RunTimeTicks = TimeSpan.FromMinutes(match.DurationMinutes.Value).Ticks;
                    }

                    result.HasMetadata = true;
                    _logger.LogDebug(
                        "AnimeClick: Episode S{Season} AC#{Absolute} ordinal={Ordinal} providerId={ProviderId} = \"{Title}\"",
                        match.SeasonNumber,
                        match.AbsoluteNumber,
                        match.SeasonOrdinalNumber,
                        match.ProviderId,
                        match.Title);
                }
                else
                {
                    _logger.LogDebug("AnimeClick: Episode {Num} has generic title \"{Title}\", skipping Italian title",
                        match.Number, match.Title);
                }
            }

            // Optional: translate English episode synopsis (TMDB) to Italian (Ollama Cloud).
            // AnimeClick publishes no per-episode synopsis, so we source the EN overview
            // from TMDB and translate it. Runs even when the Italian title is generic/missing
            // (the Italian synopsis has value on its own). Best-effort + cached; on any
            // failure the Overview is left to other providers (AniList/TMDB) in EN (fill-gaps).
            await TrySetTranslatedSynopsisAsync(result, info, mainAnimeClickId, seasonNumber, episodeNumber.Value, configuration, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Error fetching episode {Num} for {Id}", episodeNumber, animeClickId);
        }

        return result;
    }

    /// <summary>
    /// Resolves a TMDB tv id for the series, fetches the English episode overview from TMDB,
    /// and translates it to Italian via Ollama Cloud. Sets <see cref="MetadataResult{T}.Item"/>'s
    /// Overview (and HasMetadata) only on success. No-op when the feature is disabled or any
    /// key is missing, and never throws.
    /// </summary>
    private async Task TrySetTranslatedSynopsisAsync(
        MetadataResult<Episode> result,
        EpisodeInfo info,
        string animeClickId,
        int? seasonNumber,
        int episodeNumber,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableEpisodeSynopsisTranslation
            || string.IsNullOrWhiteSpace(configuration.TmdbApiKey)
            || string.IsNullOrWhiteSpace(configuration.OllamaCloudApiKey))
        {
            return;
        }

        if (!seasonNumber.HasValue || seasonNumber.Value < 0)
        {
            return;
        }

        try
        {
            // The series title + year come from the cached AnimeClickAnime that the
            // SeriesProvider populated (key anime::{url}). OriginalTitle (romaji) is the
            // best search key for TMDB; Italian Title is the fallback. If the cache miss
            // (e.g. episodes scanned before the series), skip — it'll work on a later scan.
            var seriesUrl = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
            var seriesCacheKey = $"anime::{seriesUrl}";
            var series = await _cache.GetAsync<AnimeClickAnime>(seriesCacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
            if (series is null)
            {
                _logger.LogDebug("AnimeClick: series anime not cached for {Id}, skipping synopsis translation", animeClickId);
                return;
            }

            var tmdbId = await _tmdbClient.ResolveTmdbTvIdAsync(
                series.OriginalTitle,
                series.Title,
                series.ProductionYear,
                configuration,
                $"tmdbId::{animeClickId}",
                cancellationToken).ConfigureAwait(false);

            if (!tmdbId.HasValue)
            {
                _logger.LogDebug("AnimeClick: no TMDB tv id for series \"{Series}\", skipping synopsis translation",
                    series.OriginalTitle ?? series.Title ?? "<unknown>");
                return;
            }

            var englishOverview = await _tmdbClient.GetEpisodeOverviewAsync(
                tmdbId.Value, seasonNumber.Value, episodeNumber, configuration, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(englishOverview))
            {
                return;
            }

            var italianOverview = await _translator.TranslateSynopsisAsync(
                englishOverview, tmdbId.Value, seasonNumber.Value, episodeNumber, configuration, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(italianOverview))
            {
                result.Item.Overview = italianOverview;
                result.HasMetadata = true;
                _logger.LogDebug(
                    "AnimeClick: translated synopsis IT for tmdb={Tmdb} S{S}E{E} ({Chars} chars)",
                    tmdbId.Value, seasonNumber.Value, episodeNumber, italianOverview.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: synopsis translation failed for S{S}E{E}", seasonNumber, episodeNumber);
        }
    }

    /// <summary>
    /// Resolves the AnimeClick ID for a specific season by searching relations
    /// on the main anime page. Returns null if the main page should be used.
    /// </summary>
    private async Task<string?> ResolveSeasonAnimeClickIdAsync(
        string mainId, int? seasonNumber,
        PluginConfiguration config, CancellationToken ct)
    {
        if (!seasonNumber.HasValue || seasonNumber.Value <= 1) return null;

        var cacheKey = $"seasonMap:v2::{mainId}::{seasonNumber.Value}";
        var cached = await _cache.GetAsync<string>(cacheKey, config.CacheHours, ct);
        _logger.LogDebug("AnimeClick season map cache {State}: {Key}", cached is null ? "miss" : "hit", cacheKey);
        if (cached is not null) return cached == "__same__" ? null : cached;

        string? resolvedId = null;

        try
        {
            var mainUrl = AnimeClickClient.BuildAnimeUrl(config.BaseUrl, mainId);
            var relHtml = await _client.GetStringAsync(mainUrl + "/relazioni", config, ct);
            var relations = _parser.ParseRelationsPage(relHtml, config.BaseUrl);

            var tvRelations = relations
                .Where(r => r.Format is not null &&
                    (r.Format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) ||
                     r.Format.Contains("TV", StringComparison.OrdinalIgnoreCase)) &&
                    !IsSpinoffTitle(r.Title))
                .OrderBy(r => r.Year ?? 9999)
                .ToList();

            if (tvRelations.Count > 0)
            {
                var relIndex = seasonNumber.Value - 2;
                if (relIndex >= 0 && relIndex < tvRelations.Count)
                {
                    var candidateId = tvRelations[relIndex].AnimeClickId;
                    if (!string.IsNullOrWhiteSpace(candidateId) && candidateId != mainId)
                    {
                        resolvedId = candidateId;
                        _logger.LogInformation("AnimeClick: Season {S} resolved via relations → {Id}",
                            seasonNumber.Value, resolvedId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: Relations-based season resolution failed for {Id} S{S}", mainId, seasonNumber.Value);
        }

        await _cache.SetAsync(cacheKey, resolvedId ?? "__same__", ct);
        return resolvedId;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<RemoteSearchResult>>([]);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private static bool IsSpinoffTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        System.Text.RegularExpressions.Regex.IsMatch(title,
            @"\b(Alternative|Gaiden|Spin[\s-]?[Oo]ff|Bangai[\s-]?[Hh]en)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
