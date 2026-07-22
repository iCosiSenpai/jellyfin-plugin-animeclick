using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Resolves episode overviews through a strict language-aware chain. AnimeClick's
/// native Italian description is tried first; uncached Ollama work is queued so a
/// library refresh never waits for cloud model inference.
/// </summary>
public sealed class AnimeClickMetadataFallbackService
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickTmdbClient _tmdbClient;
    private readonly AnimeClickTvdbClient _tvdbClient;
    private readonly AnimeClickOllamaTranslator _translator;
    private readonly AnimeClickTranslationQueue _translationQueue;
    private readonly ILogger<AnimeClickMetadataFallbackService> _logger;

    public AnimeClickMetadataFallbackService(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickTmdbClient tmdbClient,
        AnimeClickTvdbClient tvdbClient,
        AnimeClickOllamaTranslator translator,
        AnimeClickTranslationQueue translationQueue,
        ILogger<AnimeClickMetadataFallbackService> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _translator = translator;
        _translationQueue = translationQueue;
        _logger = logger;
    }

    public Task<AnimeClickFallbackResult?> ResolveEpisodeOverviewAsync(
        string animeClickId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => ResolveEpisodeOverviewAsync(
            animeClickId,
            season,
            episode,
            animeClickEpisodeId: null,
            configuration,
            cancellationToken,
            allowSynchronousTranslation: false);

    public Task<AnimeClickFallbackResult?> ResolveEpisodeOverviewAsync(
        string animeClickId,
        int season,
        int episode,
        string? animeClickEpisodeId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => ResolveEpisodeOverviewAsync(
            animeClickId,
            season,
            episode,
            animeClickEpisodeId,
            configuration,
            cancellationToken,
            allowSynchronousTranslation: false);

    public Task<AnimeClickFallbackResult?> ResolveEpisodeOverviewAsync(
        string animeClickId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        bool allowSynchronousTranslation)
        => ResolveEpisodeOverviewAsync(
            animeClickId,
            season,
            episode,
            animeClickEpisodeId: null,
            configuration,
            cancellationToken,
            allowSynchronousTranslation);

    public async Task<AnimeClickFallbackResult?> ResolveEpisodeOverviewAsync(
        string animeClickId,
        int season,
        int episode,
        string? animeClickEpisodeId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        bool allowSynchronousTranslation)
    {
        if (!configuration.EnableEpisodeSynopsisTranslation
            || season < 0
            || episode <= 0
            || !AnimeClickClient.TryNormalizeAnimeClickId(animeClickId, out var normalizedId))
        {
            return null;
        }

        var total = Stopwatch.StartNew();
        long animeClickEpisodeMs = 0;
        long animeMs = 0;
        long tvdbMs = 0;
        long tmdbMs = 0;
        long englishMs = 0;
        long translationMs = 0;

        AnimeClickFallbackResult? Finish(
            AnimeClickFallbackResult? result,
            string outcome,
            AnimeClickTranslationQueueState? queueState = null)
        {
            total.Stop();
            _logger.LogInformation(
                "AnimeClick episode fallback: id={Id} episodeId={EpisodeId} S{Season}E{Episode} outcome={Outcome} queue={QueueState} elapsedMs={ElapsedMs} animeClickEpisodeMs={AnimeClickEpisodeMs} animeMs={AnimeMs} tvdbMs={TvdbMs} tmdbMs={TmdbMs} englishMs={EnglishMs} translationMs={TranslationMs}",
                normalizedId,
                animeClickEpisodeId ?? "<none>",
                season,
                episode,
                outcome,
                queueState?.ToString() ?? "none",
                total.ElapsedMilliseconds,
                animeClickEpisodeMs,
                animeMs,
                tvdbMs,
                tmdbMs,
                englishMs,
                translationMs);
            return result;
        }

        try
        {
            var stage = Stopwatch.StartNew();
            if (!string.IsNullOrWhiteSpace(animeClickEpisodeId))
            {
                try
                {
                    var animeClickOverview = await GetAnimeClickEpisodeOverviewAsync(
                            animeClickEpisodeId,
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stage.Stop();
                    animeClickEpisodeMs = stage.ElapsedMilliseconds;
                    if (!string.IsNullOrWhiteSpace(animeClickOverview))
                    {
                        return Finish(
                            AnimeClickFallbackResult.NativeItalian(animeClickOverview, "AnimeClick"),
                            "native-animeclick");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    stage.Stop();
                    animeClickEpisodeMs = stage.ElapsedMilliseconds;
                    _logger.LogDebug(
                        ex,
                        "AnimeClick episode detail unavailable for {EpisodeId}; trying configured fallback sources",
                        animeClickEpisodeId);
                }
            }
            else
            {
                stage.Stop();
            }

            var tvdbConfigured = configuration.EnableTvdbSynopsis
                && !string.IsNullOrWhiteSpace(configuration.TvdbApiKey);
            var tmdbConfigured = !string.IsNullOrWhiteSpace(configuration.TmdbApiKey);
            if (!tvdbConfigured && !tmdbConfigured)
            {
                return Finish(null, "no-external-source");
            }

            stage.Restart();
            var anime = await GetAnimeAsync(normalizedId, configuration, cancellationToken)
                .ConfigureAwait(false);
            stage.Stop();
            animeMs = stage.ElapsedMilliseconds;
            if (anime is null)
            {
                return Finish(null, "anime-unavailable");
            }

            int? tvdbId = null;
            int? tmdbId = null;

            // 2) Native Italian: TVDB translations, then TMDB it-IT.
            if (tvdbConfigured)
            {
                stage.Restart();
                tvdbId = await _tvdbClient.ResolveTvdbSeriesIdAsync(
                        anime.OriginalTitle,
                        anime.Title,
                        anime.ProductionYear,
                        configuration,
                        $"tvdbSeriesId:v3::{normalizedId}",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (tvdbId.HasValue)
                {
                    var italian = await _tvdbClient.GetEpisodeOverviewAsync(
                            tvdbId.Value,
                            season,
                            episode,
                            "ita",
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stage.Stop();
                    tvdbMs = stage.ElapsedMilliseconds;
                    if (!string.IsNullOrWhiteSpace(italian))
                    {
                        return Finish(
                            AnimeClickFallbackResult.NativeItalian(italian, "TheTVDB"),
                            "native-tvdb");
                    }
                }
                else
                {
                    stage.Stop();
                    tvdbMs = stage.ElapsedMilliseconds;
                }
            }

            if (tmdbConfigured)
            {
                stage.Restart();
                tmdbId = await _tmdbClient.ResolveTmdbTvIdAsync(
                        anime.OriginalTitle,
                        anime.Title,
                        anime.ProductionYear,
                        configuration,
                        $"tmdbTvId:v3::{normalizedId}",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (tmdbId.HasValue)
                {
                    var italian = await _tmdbClient.GetEpisodeOverviewAsync(
                            tmdbId.Value,
                            season,
                            episode,
                            "it-IT",
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stage.Stop();
                    tmdbMs = stage.ElapsedMilliseconds;
                    if (!string.IsNullOrWhiteSpace(italian))
                    {
                        return Finish(
                            AnimeClickFallbackResult.NativeItalian(italian, "TMDB"),
                            "native-tmdb");
                    }
                }
                else
                {
                    stage.Stop();
                    tmdbMs = stage.ElapsedMilliseconds;
                }
            }

            // 3) English source. Prefer TMDB, then TVDB when enabled.
            string? english = null;
            string? sourceIdentity = null;
            string? sourceName = null;

            stage.Restart();
            if (tmdbId.HasValue)
            {
                english = await _tmdbClient.GetEpisodeOverviewAsync(
                        tmdbId.Value,
                        season,
                        episode,
                        "en-US",
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(english))
                {
                    sourceIdentity = $"tmdb:tv:{tmdbId.Value}:s{season}:e{episode}";
                    sourceName = "TMDB";
                }
            }

            if (string.IsNullOrWhiteSpace(english) && tvdbId.HasValue)
            {
                english = await _tvdbClient.GetEpisodeOverviewAsync(
                        tvdbId.Value,
                        season,
                        episode,
                        "eng",
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(english))
                {
                    sourceIdentity = $"tvdb:series:{tvdbId.Value}:s{season}:e{episode}";
                    sourceName = "TheTVDB";
                }
            }

            stage.Stop();
            englishMs = stage.ElapsedMilliseconds;
            if (string.IsNullOrWhiteSpace(english)
                || string.IsNullOrWhiteSpace(sourceIdentity)
                || string.IsNullOrWhiteSpace(configuration.OllamaCloudApiKey))
            {
                return Finish(null, "no-english-source");
            }

            // 4) A cached cloud translation is safe in the request path.
            stage.Restart();
            var translated = await _translationQueue.GetCachedTranslationAsync(
                    english,
                    normalizedId,
                    sourceIdentity,
                    "episode.overview",
                    "en",
                    "it",
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            stage.Stop();
            translationMs = stage.ElapsedMilliseconds;
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return Finish(
                    AnimeClickFallbackResult.Translated(
                        translated,
                        sourceName ?? "external",
                        configuration.OllamaCloudModel),
                    "ollama-cache");
            }

            // Diagnostics may explicitly request an end-to-end synchronous probe.
            if (allowSynchronousTranslation)
            {
                stage.Restart();
                translated = await _translator.TranslateMetadataFieldAsync(
                        english,
                        normalizedId,
                        sourceIdentity,
                        "episode.overview",
                        "en",
                        "it",
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                stage.Stop();
                translationMs += stage.ElapsedMilliseconds;
                return string.IsNullOrWhiteSpace(translated)
                    ? Finish(null, "ollama-synchronous-miss")
                    : Finish(
                        AnimeClickFallbackResult.Translated(
                            translated,
                            sourceName ?? "external",
                            configuration.OllamaCloudModel),
                        "ollama-synchronous");
            }

            // Normal library refreshes never wait for uncached cloud inference.
            stage.Restart();
            var queueState = await _translationQueue.EnqueueAsync(
                    english,
                    normalizedId,
                    sourceIdentity,
                    "episode.overview",
                    "en",
                    "it",
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            stage.Stop();
            translationMs += stage.ElapsedMilliseconds;

            // A concurrent worker may have completed between the cache read and enqueue.
            if (queueState == AnimeClickTranslationQueueState.Cached)
            {
                translated = await _translationQueue.GetCachedTranslationAsync(
                        english,
                        normalizedId,
                        sourceIdentity,
                        "episode.overview",
                        "en",
                        "it",
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return Finish(
                        AnimeClickFallbackResult.Translated(
                            translated,
                            sourceName ?? "external",
                            configuration.OllamaCloudModel),
                        "ollama-cache-race",
                        queueState);
                }
            }

            return Finish(null, "ollama-deferred", queueState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "AnimeClick fallback failed for {Id} S{Season}E{Episode}; field left unchanged",
                animeClickId,
                season,
                episode);
            return Finish(null, "error");
        }
    }

    private async Task<string?> GetAnimeClickEpisodeOverviewAsync(
        string episodeProviderId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryNormalizeAnimeClickId(episodeProviderId, out var normalizedEpisodeId)
            || !AnimeClickClient.TryBuildEpisodeUrl(
                configuration.BaseUrl,
                normalizedEpisodeId,
                out var episodeUrl))
        {
            return null;
        }

        // Numeric episode IDs are stable when AnimeClick changes a slug. The base
        // URL remains part of the identity so alternate hosts cannot share content.
        var stableEpisodeId = normalizedEpisodeId.Split('/', 2)[0];
        var baseUrlIdentity = configuration.BaseUrl.Trim().TrimEnd('/').ToLowerInvariant();
        var cacheKey = $"episodeOverview:v2::{baseUrlIdentity}::{stableEpisodeId}";
        var missCacheKey = cacheKey + "::miss";
        var cached = await _cache
            .GetAsync<string>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var cachedMiss = await _cache
            .GetAsync<string>(missCacheKey, configuration.NegativeCacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(cachedMiss, "empty", StringComparison.Ordinal))
        {
            return null;
        }

        var html = await _client.GetStringAsync(episodeUrl, configuration, cancellationToken)
            .ConfigureAwait(false);
        if (!_parser.TryParseEpisodeOverviewPage(html, out var overview))
        {
            _logger.LogDebug(
                "AnimeClick episode page {EpisodeUrl} did not expose the expected description node; miss not cached",
                episodeUrl);
            return null;
        }

        if (string.IsNullOrWhiteSpace(overview))
        {
            // Cache only a recognized empty/placeholder response. Network failures,
            // interstitials and changed markup remain immediately retryable.
            await _cache.SetAsync(missCacheKey, "empty", cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _cache.SetAsync(cacheKey, overview, cancellationToken).ConfigureAwait(false);
        return overview;
    }

    private async Task<AnimeClickAnime?> GetAnimeAsync(
        string animeClickId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var animeUrl))
        {
            return null;
        }

        var cacheKey = $"anime::{animeUrl}";
        var cached = await _cache
            .GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var html = await _client.GetStringAsync(animeUrl, configuration, cancellationToken)
            .ConfigureAwait(false);
        var anime = _parser.ParseAnimePage(animeUrl, html);
        await _cache.SetAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
        return anime;
    }
}

public sealed record AnimeClickFallbackResult(
    string Value,
    string Source,
    string SourceLanguage,
    bool UsedOllama,
    string? Model)
{
    public static AnimeClickFallbackResult NativeItalian(string value, string source)
        => new(value, source, "it", false, null);

    public static AnimeClickFallbackResult Translated(string value, string source, string model)
        => new(value, source, "en", true, model);
}
