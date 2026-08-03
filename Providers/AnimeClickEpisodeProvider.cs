using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    private static readonly SemaphoreStripe CacheFillLocks = new();

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

    /// <summary>
    /// The cache key of a raw episode catalog. The declared counts are part of the identity: a
    /// detail page that changes from 1x24 to two cours must not reuse the older snapshot. Shared
    /// with the library audit so a read-only inspection looks exactly where the provider writes.
    /// </summary>
    internal static string BuildCatalogCacheKey(
        string animeClickId,
        int? declaredEpisodeCount,
        int declaredSeasonsCount)
        => $"episodes:raw:v5::{animeClickId}::{declaredEpisodeCount.GetValueOrDefault()}:{declaredSeasonsCount}";

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
        var identity = AnimeClickEpisodeIdentity.Resolve(seriesAnimeClickId, seasonAnimeClickId);
        var identityIsSeasonSpecific = identity.IsSeasonSpecific;
        var mainAnimeClickId = identity.MatchingId;

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
        if (!episodeNumber.HasValue || episodeNumber.Value < 0)
        {
            // Episode zero of a regular season is a real shape — a prologue or a recap that the
            // library stores as S01E00 — and AnimeClick files those rows among the specials, so
            // the matcher routes them there. Refusing them here left them without metadata for
            // good.
            return result;
        }

        mainAnimeClickId = normalizedMainId;

        // Only a series-level identity can walk the sequel chain: it is the card the relations
        // hang off. Normalised here so it hits the same season-map cache keys as before.
        string? traversalRootId = null;
        if (!string.IsNullOrWhiteSpace(seriesAnimeClickId)
            && AnimeClickClient.TryNormalizeAnimeClickId(seriesAnimeClickId, out var normalizedSeriesId))
        {
            traversalRootId = normalizedSeriesId;
        }

        if (!configuration.EnableEpisodeTitles
            && !configuration.EnableEpisodeSynopsisTranslation
            && !string.IsNullOrWhiteSpace(existingEpisodeId))
        {
            result.Item.SetProviderId("AnimeClick", existingEpisodeId);
        }

        AnimeClickEpisode? matchedEpisode = null;
        var episodeMatchCompleted = false;
        var needsEpisodeMatch = configuration.EnableEpisodeTitles
            || (configuration.EnableEpisodeSynopsisTranslation && seasonNumber.HasValue);
        if (needsEpisodeMatch)
        {
            try
            {
                matchedEpisode = await ResolveEpisodeAsync(
                        result,
                        mainAnimeClickId,
                        identityIsSeasonSpecific,
                        traversalRootId,
                        seasonNumber,
                        episodeNumber.Value,
                        info.IndexNumberEnd,
                        existingEpisodeId,
                        info.Name,
                        info.Path,
                        populateMetadata: configuration.EnableEpisodeTitles,
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                episodeMatchCompleted = true;
                if (!configuration.EnableEpisodeTitles
                    && !string.IsNullOrWhiteSpace(matchedEpisode?.ProviderId))
                {
                    // Persist a newly verified identity even when every synopsis source
                    // misses, replacing any stale provider ID from an earlier layout.
                    result.Item.SetProviderId("AnimeClick", matchedEpisode.ProviderId);
                    result.HasMetadata = true;
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
                    "AnimeClick: episode lookup failed for episode {Num} of {Id}; synopsis fallback will continue when possible",
                    episodeNumber.Value,
                    mainAnimeClickId);
            }
        }

        if (configuration.EnableEpisodeSynopsisTranslation && seasonNumber.HasValue)
        {
            try
            {
                // Season 1 only when the season card is the only identity we have: then the
                // external IDs were resolved from that card and its own numbering applies.
                var fallbackSeasonNumber = identity.ExternalNumbersRestartAtOne ? 1 : seasonNumber.Value;
                var fallbackAnimeClickId = identity.ExternalSourceId ?? mainAnimeClickId;
                var episodeAnimeClickId = matchedEpisode?.ProviderId;
                if (!episodeMatchCompleted && string.IsNullOrWhiteSpace(episodeAnimeClickId))
                {
                    // A previously persisted detail ID remains useful when the list page is
                    // temporarily unavailable. A completed safe miss does not reuse it.
                    episodeAnimeClickId = existingEpisodeId;
                }

                var fallback = await _fallbackService.ResolveEpisodeOverviewAsync(
                        fallbackAnimeClickId,
                        fallbackSeasonNumber,
                        episodeNumber.Value,
                        episodeAnimeClickId,
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.Value))
                {
                    result.Item.Overview = fallback.Value;
                    var providerIdToPersist = matchedEpisode?.ProviderId
                        ?? (!episodeMatchCompleted ? existingEpisodeId : null);
                    if (!string.IsNullOrWhiteSpace(providerIdToPersist))
                    {
                        // A newly matched detail ID replaces a stale persisted identity.
                        // The old ID is retained only when the list lookup itself failed.
                        result.Item.SetProviderId("AnimeClick", providerIdToPersist);
                    }

                    result.HasMetadata = true;
                    _logger.LogInformation(
                        "AnimeClick: episode overview source={Source} sourceLanguage={Language} ai={UsedAi} S{Season}E{Episode}",
                        fallback.Source,
                        fallback.SourceLanguage,
                        fallback.UsedAi,
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
            // Must happen for every published result: Jellyfin would otherwise overwrite the
            // episode numbering with the nulls of this bare item. See AnimeClickNumberingGuard.
            AnimeClickNumberingGuard.Preserve(result.Item, info);
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
        // Defense in depth: see AnimeClickSeriesProvider.GetImageResponse.
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, url, out var imageUri))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(imageUri, cancellationToken);
    }

    private async Task<AnimeClickEpisode?> ResolveEpisodeAsync(
        MetadataResult<Episode> result,
        string mainAnimeClickId,
        bool identityIsSeasonSpecific,
        string? traversalRootId,
        int? seasonNumber,
        int episodeNumber,
        int? episodeNumberEnd,
        string? existingEpisodeId,
        string? jellyfinTitle,
        string? episodePath,
        bool populateMetadata,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, mainAnimeClickId, out var mainAnimeUrl))
        {
            _logger.LogWarning(
                "AnimeClick EpisodeProvider ignored invalid series provider ID '{ProviderId}'",
                mainAnimeClickId);
            return null;
        }

        // Resolved before the traversal, because the years it carries are what lets the traversal
        // choose on the AnimeClick pages that declare no relation type.
        var libraryLayout = _layoutResolver.Resolve(episodePath);

        // The traversal from the series card always gets the first word when the series has an ID
        // of its own: it is held to today's safety rules, while an ID sitting on the season may
        // have been written by an older and laxer version of this plugin. The stored season ID is
        // then the fallback for exactly the case it exists for — a chain the traversal cannot
        // prove, like a franchise whose relations are ambiguous even with the year in hand.
        string? resolvedAnimeClickId = null;
        var isSeasonSpecificPage = false;
        if (!string.IsNullOrWhiteSpace(traversalRootId))
        {
            resolvedAnimeClickId = await _seasonResolver
                .ResolveAsync(
                    traversalRootId,
                    seasonNumber,
                    configuration,
                    cancellationToken,
                    libraryLayout?.GetSeasonAirYears())
                .ConfigureAwait(false);
            isSeasonSpecificPage = resolvedAnimeClickId is not null;
        }

        if (resolvedAnimeClickId is null && identityIsSeasonSpecific)
        {
            resolvedAnimeClickId = mainAnimeClickId;
            isSeasonSpecificPage = true;
            _logger.LogDebug(
                "AnimeClick: S{Season} uses the ID stored on the season ({Id}); the sequel traversal had no answer",
                seasonNumber,
                mainAnimeClickId);
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
        var cacheKey = BuildCatalogCacheKey(animeClickId, declaredEpisodes, declaredSeasons);
        var catalog = await _cache
            .GetAsync<AnimeClickEpisodeCatalog>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug("AnimeClick episode catalog cache {State}: {Key}", catalog is null ? "miss" : "hit", cacheKey);

        if (catalog is null || catalog.Episodes.Count == 0)
        {
            var fillLock = CacheFillLocks.Get("catalog::" + cacheKey);
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

        // A resolved season card numbers its own episodes from one, so the library boundaries stop
        // being the reference for this match.
        if (isSeasonSpecificPage)
        {
            libraryLayout = null;
        }

        // The library may number a standalone work as a later season because the rest of its
        // franchise sits in other folders. When the card accounts for exactly that one season,
        // read it flat instead of looking for an offset that has nothing to measure against.
        if (!isSeasonSpecificPage
            && seasonNumber.HasValue
            && libraryLayout is not null
            && libraryLayout.IsStandaloneSeason(
                seasonNumber.Value,
                catalog.Episodes.Count(episode => !episode.IsSpecial)))
        {
            _logger.LogInformation(
                "AnimeClick: {Id} read as a standalone season: the library holds only S{Season} and the card lists exactly its episodes",
                animeClickId,
                seasonNumber.Value);
            isSeasonSpecificPage = true;
            libraryLayout = null;
        }

        var pageSeason = isSeasonSpecificPage ? 1 : seasonNumber;
        var libraryRuntimeMinutes = _layoutResolver.GetKnownRuntimeMinutes(episodePath);
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
            DeclaredEpisodeCount = catalog.DeclaredEpisodeCount,
            LibraryRuntimeMinutes = libraryRuntimeMinutes,
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
            return null;
        }

        if (populateMetadata)
        {
            var wroteMetadata = false;
            if (!string.IsNullOrWhiteSpace(match.Title))
            {
                // Shared with the overview check in the parser: a title that only restates the
                // number is worse than leaving Jellyfin's own, because it looks deliberate.
                if (!AnimeClickHtmlParser.IsPlaceholderEpisodeText(match.Title))
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
                // Deliberately not written to the item. Jellyfin has already probed the file, and
                // its exact runtime beats a figure rounded to whole minutes on a web page — the
                // 24.1' of a real episode became 24'. When the two disagree by more than rounding
                // the row is not this file at all, which the matcher now treats as missing
                // corroboration; overwriting the runtime on top of that used to turn a 24 minute
                // episode into a 5 minute one and made Jellyfin mark it watched after four.
                _logger.LogDebug(
                    "AnimeClick: row declares {Duration}' for episode {Num}; Jellyfin's own runtime is kept",
                    match.DurationMinutes.Value,
                    episodeNumber);
            }

            result.HasMetadata |= wroteMetadata;
        }

        _logger.LogDebug(
            "AnimeClick: episode raw=\"{Raw}\" global={Global} seasonOrdinal={Ordinal} providerId={ProviderId} title=\"{Title}\"",
            match.RawNumberLabel,
            match.GlobalOrdinal,
            match.SeasonOrdinalNumber,
            match.ProviderId,
            match.Title);
        return match;
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

        var fillLock = CacheFillLocks.Get("summary::" + cacheKey);
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
