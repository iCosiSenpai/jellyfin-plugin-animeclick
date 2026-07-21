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
/// Provides episode-level Italian titles from AnimeClick and optional Italian
/// overview fallback. The two paths are independent: failure or disablement of
/// /episodi never suppresses the language-aware overview chain.
/// </summary>
public class AnimeClickEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickEpisodeListLoader _episodeListLoader;
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly ILogger<AnimeClickEpisodeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickMetadataFallbackService _fallbackService;

    public AnimeClickEpisodeProvider(
        AnimeClickCacheService cache,
        AnimeClickEpisodeListLoader episodeListLoader,
        AnimeClickSeasonResolver seasonResolver,
        ILogger<AnimeClickEpisodeProvider> logger,
        IHttpClientFactory httpClientFactory,
        AnimeClickMetadataFallbackService fallbackService)
    {
        _cache = cache;
        _episodeListLoader = episodeListLoader;
        _seasonResolver = seasonResolver;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _fallbackService = fallbackService;
    }

    public string Name => "AnimeClick";

    /// <summary>
    /// AnimeClick runs first so Italian episode fields win Jellyfin's first-value merge.
    /// A post-merge authority provider protects values produced during this refresh.
    /// </summary>
    public int Order => 0;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Episode> { Item = new Episode() };
        var existingEpisodeId = info.GetProviderId("AnimeClick");
        using var authorityLease = AnimeClickMetadataAuthorityStore.Begin<Episode>(
            info.Path,
            existingEpisodeId);

        _logger.LogInformation(
            "AnimeClick EpisodeProvider.GetMetadata called: name=\"{Name}\" S{Season}E{Episode} seriesProviderId={SeriesProviderId} path={Path}",
            info.Name,
            info.ParentIndexNumber,
            info.IndexNumber,
            info.SeriesProviderIds?.GetValueOrDefault("AnimeClick") ?? "<none>",
            info.Path ?? "<none>");

        var mainAnimeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick")
                               ?? info.GetProviderId("AnimeClick");
        if (string.IsNullOrWhiteSpace(mainAnimeClickId)
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainAnimeClickId, out var normalizedMainId))
        {
            return result;
        }

        var episodeNumber = info.IndexNumber;
        if (!episodeNumber.HasValue || episodeNumber.Value <= 0)
        {
            return result;
        }

        mainAnimeClickId = normalizedMainId;
        var seasonNumber = info.ParentIndexNumber;

        if (!configuration.EnableEpisodeTitles)
        {
            // Preserve only an existing episode identity. The parent series ID is
            // never a valid substitute for an episode provider ID.
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
                        seasonNumber,
                        episodeNumber.Value,
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

        if (configuration.EnableEpisodeSynopsisTranslation && seasonNumber.HasValue)
        {
            try
            {
                var fallback = await _fallbackService.ResolveEpisodeOverviewAsync(
                        mainAnimeClickId,
                        seasonNumber.Value,
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
                        seasonNumber.Value,
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
            // Consumed once after all remote providers have filled their gaps.
            authorityLease.Capture(result.Item);
        }

        return result;
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

    private async Task PopulateTitleAsync(
        MetadataResult<Episode> result,
        string mainAnimeClickId,
        int? seasonNumber,
        int episodeNumber,
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

        var resolvedAnimeClickId = await _seasonResolver
            .ResolveAsync(mainAnimeClickId, seasonNumber, configuration, cancellationToken)
            .ConfigureAwait(false);
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
        }

        // v4 records whether season groups were inferred. Older entries cannot
        // safely distinguish synthetic boundaries from explicit AnimeClick seasons.
        var cacheKey = $"episodes:v4::{animeClickId}";
        var episodes = await _cache
            .GetAsync<List<AnimeClickEpisode>>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug("AnimeClick episodes cache {State}: {Key}", episodes is null ? "miss" : "hit", cacheKey);

        if (episodes is null || episodes.Count == 0)
        {
            var episodesUrl = animeUrl + "/episodi";

            // SeasonsCount comes from the complete anime detail page. It is applied by
            // the shared loader only after every paginated table has been merged.
            int? seasonsCount = null;
            var seriesCacheKey = $"anime::{animeUrl}";
            var series = await _cache
                .GetAsync<AnimeClickAnime>(seriesCacheKey, configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (series is not null && series.SeasonsCount > 0)
            {
                seasonsCount = series.SeasonsCount;
                _logger.LogDebug(
                    "AnimeClick: using SeasonsCount={SeasonsCount} after episode pagination for {Id}",
                    seasonsCount,
                    animeClickId);
            }

            var loaded = await _episodeListLoader.LoadAsync(
                    episodesUrl,
                    configuration.BaseUrl,
                    seasonsCount,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            episodes = loaded.Episodes;

            if (loaded.PaginationComplete)
            {
                await _cache.SetAsync(cacheKey, episodes, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("AnimeClick: Cached {Count} episodes for {Id}", episodes.Count, animeClickId);
            }
            else
            {
                _logger.LogWarning(
                    "AnimeClick: episode pagination for {Id} was incomplete; using {Count} entries without caching",
                    animeClickId,
                    episodes.Count);
            }
        }

        // A season-specific AnimeClick entry numbers its own episodes as S1,
        // even when Jellyfin stores it as S2/S3 of a grouped series.
        var animeClickPageSeason = resolvedAnimeClickId is null ? seasonNumber : 1;
        var episodeMatch = AnimeClickEpisodeMatcher.Match(
            episodes,
            animeClickPageSeason,
            episodeNumber);
        var match = episodeMatch.Episode;
        _logger.LogInformation(
            "AnimeClick: Episode match strategy={Strategy} animeClickId={Id} S{Season}E{Episode}",
            episodeMatch.Strategy,
            animeClickId,
            seasonNumber,
            episodeNumber);

        if (match is null || string.IsNullOrWhiteSpace(match.Title))
        {
            return;
        }

        var isGeneric = System.Text.RegularExpressions.Regex.IsMatch(
            match.Title,
            @"^Episodio\s+\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (isGeneric)
        {
            _logger.LogDebug(
                "AnimeClick: Episode {Num} has generic title \"{Title}\", skipping Italian title",
                match.Number,
                match.Title);
            return;
        }

        result.Item.Name = match.Title;
        if (!string.IsNullOrWhiteSpace(match.ProviderId))
        {
            result.Item.SetProviderId("AnimeClick", match.ProviderId);
        }

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
}
