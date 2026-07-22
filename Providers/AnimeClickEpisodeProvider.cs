using System;
using System.Collections.Concurrent;
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
/// Provides episode-level Italian titles from AnimeClick and optional Italian
/// overview fallback. Raw AnimeClick numbering is reconciled against Jellyfin at
/// match time; ambiguous layouts are left untouched rather than guessed.
/// </summary>
public class AnimeClickEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheFillLocks = new(StringComparer.Ordinal);

    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickEpisodeListLoader _episodeListLoader;
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly AnimeClickEpisodeLayoutResolver _layoutResolver;
    private readonly ILogger<AnimeClickEpisodeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickMetadataFallbackService _fallbackService;

    public AnimeClickEpisodeProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickEpisodeListLoader episodeListLoader,
        AnimeClickSeasonResolver seasonResolver,
        AnimeClickEpisodeLayoutResolver layoutResolver,
        ILogger<AnimeClickEpisodeProvider> logger,
        IHttpClientFactory httpClientFactory,
        AnimeClickMetadataFallbackService fallbackService)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _episodeListLoader = episodeListLoader;
        _seasonResolver = seasonResolver;
        _layoutResolver = layoutResolver;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _fallbackService = fallbackService;
    }

    public string Name => "AnimeClick";

    public int Order => 0;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Episode> { Item = new Episode() };
        var existingEpisodeId = info.GetProviderId("AnimeClick");
        using var authorityLease = AnimeClickMetadataAuthorityStore.Begin<Episode>(
            info.Path,
            existingEpisodeId);

        var seriesAnimeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick");
        var seasonAnimeClickId = info.SeasonProviderIds?.GetValueOrDefault("AnimeClick");
        var identityIsSeasonSpecific = string.IsNullOrWhiteSpace(seriesAnimeClickId)
            && !string.IsNullOrWhiteSpace(seasonAnimeClickId);
        var mainAnimeClickId = identityIsSeasonSpecific ? seasonAnimeClickId : seriesAnimeClickId;

        _logger.LogInformation(
            "AnimeClick EpisodeProvider.GetMetadata called: name=\"{Name}\" S{Season}E{Episode} seriesProviderId={SeriesProviderId} seasonProviderId={SeasonProviderId} path={Path}",
            info.Name,
            info.ParentIndexNumber,
            info.IndexNumber,
            seriesAnimeClickId ?? "<none>",
            seasonAnimeClickId ?? "<none>",
            info.Path ?? "<none>");

        // An episode provider ID identifies /episodio, never /anime. Use only parent
        // identities here so an orphan episode cannot accidentally fetch an anime page.
        if (string.IsNullOrWhiteSpace(mainAnimeClickId)
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainAnimeClickId, out var normalizedMainId))
        {
            return result;
        }

        var seasonNumber = info.ParentIndexNumber;
        var episodeNumber = info.IndexNumber;
        if (!episodeNumber.HasValue
            || episodeNumber.Value < 0
            || (episodeNumber.Value == 0 && seasonNumber is not 0))
        {
            return result;
        }

        mainAnimeClickId = normalizedMainId;
        if (!configuration.EnableEpisodeTitles)
        {
            if (!string.IsNullOrWhiteSpace(existingEpisodeId))
            {
                result.Item.SetProviderId("AnimeClick", existingEpisodeId);
            }
        }
        else
        {
            try
            {
                await PopulateTitleAsync(
                        result,
                        mainAnimeClickId,
                        identityIsSeasonSpecific,
                        seasonNumber,
                        episodeNumber.Value,
                        info.IndexNumberEnd,
                        existingEpisodeId,
                        info.Name,
                        info.Path,
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "AnimeClick: title lookup failed for episode {Num} of {Id}; synopsis fallback will still run",
                    episodeNumber.Value,
                    mainAnimeClickId);
            }
        }

        if (configuration.EnableEpisodeSynopsisTranslation
            && seasonNumber.HasValue
            && episodeNumber.Value > 0)
        {
            try
            {
                var fallbackSeasonNumber = identityIsSeasonSpecific ? 1 : seasonNumber.Value;
                var fallback = await _fallbackService.ResolveEpisodeOverviewAsync(
                        mainAnimeClickId,
                        fallbackSeasonNumber,
                        episodeNumber.Value,
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.Value))
                {
                    result.Item.Overview = fallback.Value;
                    if (string.IsNullOrWhiteSpace(result.Item.GetProviderId("AnimeClick"))
                        && !string.IsNullOrWhiteSpace(existingEpisodeId))
                    {
                        result.Item.SetProviderId("AnimeClick", existingEpisodeId);
                    }

                    result.HasMetadata = true;
                    _logger.LogInformation(
                        "AnimeClick: episode overview source={Source} sourceLanguage={Language} ollama={UsedOllama} S{Season}E{Episode}",
                        fallback.Source,
                        fallback.SourceLanguage,
                        fallback.UsedOllama,
                        fallbackSeasonNumber,
                        episodeNumber.Value);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "AnimeClick: synopsis fallback failed for episode {Num} of {Id}; field left unchanged",
                    episodeNumber.Value,
                    mainAnimeClickId);
            }
        }

        if (result.HasMetadata)
        {
            authorityLease.Capture(result.Item);
        }

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        EpisodeInfo searchInfo,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private async Task PopulateTitleAsync(
        MetadataResult<Episode> result,
        string mainAnimeClickId,
        bool identityIsSeasonSpecific,
        int? seasonNumber,
        int episodeNumber,
        int? episodeNumberEnd,
        string? existingEpisodeId,
        string? jellyfinTitle,
        string? episodePath,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, mainAnimeClickId, out var mainAnimeUrl))
        {
            _logger.LogWarning(
                "AnimeClick EpisodeProvider ignored invalid series provider ID '{ProviderId}'",
                mainAnimeClickId);
            return;
        }

        string? resolvedAnimeClickId = null;
        var isSeasonSpecificPage = identityIsSeasonSpecific;
        if (!identityIsSeasonSpecific)
        {
            resolvedAnimeClickId = await _seasonResolver
                .ResolveAsync(mainAnimeClickId, seasonNumber, configuration, cancellationToken)
                .ConfigureAwait(false);
            isSeasonSpecificPage = resolvedAnimeClickId is not null;
        }

        var animeClickId = resolvedAnimeClickId ?? mainAnimeClickId;
        var animeUrl = mainAnimeUrl;
        if (resolvedAnimeClickId is not null
            && !AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, resolvedAnimeClickId, out animeUrl))
        {
            _logger.LogWarning(
                "AnimeClick: invalid related provider ID '{ProviderId}', falling back to main series",
                resolvedAnimeClickId);
            animeClickId = mainAnimeClickId;
            animeUrl = mainAnimeUrl;
            resolvedAnimeClickId = null;
            isSeasonSpecificPage = identityIsSeasonSpecific;
        }

        var series = await GetAnimeSummaryBestEffortAsync(
                animeUrl,
                configuration,
                cancellationToken)
            .ConfigureAwait(false);
        var declaredSeasons = series?.SeasonsCount ?? 0;
        var declaredEpisodes = series?.EpisodeCount;

        // Counts are part of the raw cache identity. A refreshed detail page that changes
        // from 1x24 to 2 cours cannot reuse a snapshot created under the old declaration.
        var cacheKey = $"episodes:raw:v5::{animeClickId}::{declaredEpisodes.GetValueOrDefault()}:{declaredSeasons}";
        var catalog = await _cache
            .GetAsync<AnimeClickEpisodeCatalog>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug("AnimeClick episode catalog cache {State}: {Key}", catalog is null ? "miss" : "hit", cacheKey);

        if (catalog is null || catalog.Episodes.Count == 0)
        {
            var fillLock = CacheFillLocks.GetOrAdd(
                "catalog::" + cacheKey,
                static _ => new SemaphoreSlim(1, 1));
            await fillLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Another concurrent episode may have populated the same raw catalog.
                catalog = await _cache
                    .GetAsync<AnimeClickEpisodeCatalog>(cacheKey, configuration.CacheHours, cancellationToken)
                    .ConfigureAwait(false);
                if (catalog is null || catalog.Episodes.Count == 0)
                {
                    var loaded = await _episodeListLoader.LoadAsync(
                            animeUrl + "/episodi",
                            configuration.BaseUrl,
                            declaredSeasons > 0 ? declaredSeasons : null,
                            declaredEpisodes,
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    catalog = loaded.Catalog;

                    if (loaded.PaginationComplete)
                    {
                        await _cache.SetAsync(cacheKey, catalog, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation(
                            "AnimeClick: cached raw catalog {Fingerprint} with {Count} rows for {Id}",
                            catalog.LayoutFingerprint,
                            catalog.Episodes.Count,
                            animeClickId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "AnimeClick: episode pagination for {Id} was incomplete; using {Count} rows without caching",
                            animeClickId,
                            catalog.Episodes.Count);
                    }
                }
            }
            finally
            {
                fillLock.Release();
            }
        }

        var pageSeason = isSeasonSpecificPage ? 1 : seasonNumber;
        var libraryLayout = isSeasonSpecificPage
            ? null
            : _layoutResolver.Resolve(episodePath);
        var layoutOverride = AnimeClickEpisodeLayoutOverrideParser.ParseFor(
                                 configuration.EpisodeLayoutOverrides,
                                 animeClickId)
                             ?? AnimeClickEpisodeLayoutOverrideParser.ParseFor(
                                 configuration.EpisodeLayoutOverrides,
                                 mainAnimeClickId);
        var context = new AnimeClickEpisodeMatchContext(pageSeason, episodeNumber)
        {
            JellyfinEpisodeNumberEnd = episodeNumberEnd,
            ExistingProviderId = existingEpisodeId,
            JellyfinTitle = jellyfinTitle,
            LibraryLayout = libraryLayout,
            LayoutOverride = layoutOverride,
            DeclaredSeasonsCount = catalog.DeclaredSeasonsCount > 0
                ? catalog.DeclaredSeasonsCount
                : null,
            IsSeasonSpecificPage = isSeasonSpecificPage
        };
        var episodeMatch = AnimeClickEpisodeMatcher.Match(catalog.Episodes, context);
        var match = episodeMatch.Episode;

        _logger.LogInformation(
            "AnimeClick: episode match strategy={Strategy} confidence={Confidence:F2} reason={Reason} animeClickId={Id} S{Season}E{Episode} layout={Layout} catalog={Fingerprint}",
            episodeMatch.Strategy,
            episodeMatch.Confidence,
            episodeMatch.Reason,
            animeClickId,
            seasonNumber,
            episodeNumber,
            libraryLayout?.Describe() ?? "unavailable",
            catalog.LayoutFingerprint);

        if (match is null)
        {
            return;
        }

        var wroteMetadata = false;
        if (!string.IsNullOrWhiteSpace(match.Title))
        {
            var isGeneric = System.Text.RegularExpressions.Regex.IsMatch(
                match.Title,
                @"^(?:Episodio|Episode|Ep\.?)\s+\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!isGeneric)
            {
                result.Item.Name = match.Title;
                wroteMetadata = true;
            }
            else
            {
                _logger.LogDebug(
                    "AnimeClick: episode row has generic title \"{Title}\"; identity and duration are still retained",
                    match.Title);
            }
        }

        if (!string.IsNullOrWhiteSpace(match.ProviderId))
        {
            result.Item.SetProviderId("AnimeClick", match.ProviderId);
            wroteMetadata = true;
        }

        if (match.DurationMinutes.HasValue)
        {
            result.Item.RunTimeTicks = TimeSpan.FromMinutes(match.DurationMinutes.Value).Ticks;
            wroteMetadata = true;
        }

        result.HasMetadata |= wroteMetadata;
        _logger.LogDebug(
            "AnimeClick: episode raw=\"{Raw}\" global={Global} seasonOrdinal={Ordinal} providerId={ProviderId} title=\"{Title}\"",
            match.RawNumberLabel,
            match.GlobalOrdinal,
            match.SeasonOrdinalNumber,
            match.ProviderId,
            match.Title);
    }

    private async Task<AnimeClickAnime?> GetAnimeSummaryBestEffortAsync(
        string animeUrl,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"anime::{animeUrl}";
        var cached = await _cache
            .GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var fillLock = CacheFillLocks.GetOrAdd(
            "summary::" + cacheKey,
            static _ => new SemaphoreSlim(1, 1));
        await fillLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await _cache
                .GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            try
            {
                var html = await _client.GetStringAsync(animeUrl, configuration, cancellationToken)
                    .ConfigureAwait(false);
                var anime = _parser.ParseAnimePage(animeUrl, html);
                await _cache.SetAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
                return anime;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "AnimeClick: detail page unavailable for {Url}; episode catalog will continue without declared counts",
                    animeUrl);
                return null;
            }
        }
        finally
        {
            fillLock.Release();
        }
    }
}
