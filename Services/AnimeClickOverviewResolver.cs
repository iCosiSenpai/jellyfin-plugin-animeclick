using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public interface IAnimeClickOverviewResolver
{
    Task<string?> ResolveAsync(BaseItem item, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves only the Overview value needed by an administrative repair. It deliberately bypasses
/// the broad metadata merge so names, genres, studios and every other field remain untouched.
/// </summary>
public sealed class AnimeClickOverviewResolver : IAnimeClickOverviewResolver
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickMetadataFallbackService _fallbackService;
    private readonly ILogger<AnimeClickOverviewResolver> _logger;

    public AnimeClickOverviewResolver(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickMetadataFallbackService fallbackService,
        ILogger<AnimeClickOverviewResolver> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _fallbackService = fallbackService;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        try
        {
            return item switch
            {
                Episode episode => await ResolveEpisodeAsync(episode, configuration, cancellationToken)
                    .ConfigureAwait(false),
                Series or Movie => await ResolveAnimeAsync(item, configuration, cancellationToken)
                    .ConfigureAwait(false),
                _ => null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AnimeClick Overview-only repair failed for item={ItemId} type={ItemType}",
                item.Id,
                item.GetType().Name);
            return null;
        }
    }

    private async Task<string?> ResolveAnimeAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var animeClickId = item.GetProviderId("AnimeClick");
        if (!configuration.EnablePlot
            || !AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var animeUrl))
        {
            return null;
        }

        var cacheKey = $"anime::{animeUrl}";
        var anime = await _cache
            .GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (anime is null)
        {
            var html = await _client.GetStringAsync(animeUrl, configuration, cancellationToken)
                .ConfigureAwait(false);
            anime = _parser.ParseAnimePage(animeUrl, html);
            await _cache.SetAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(anime.Overview) ? null : anime.Overview.Trim();
    }

    private async Task<string?> ResolveEpisodeAsync(
        Episode episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableEpisodeSynopsisTranslation
            || episode.ParentIndexNumber is null or < 0
            || episode.IndexNumber is null or < 0)
        {
            return null;
        }

        var identity = AnimeClickEpisodeIdentity.Resolve(
            episode.Series?.GetProviderId("AnimeClick"),
            episode.Season?.GetProviderId("AnimeClick"));
        var animeClickId = identity.ExternalSourceId ?? identity.MatchingId;
        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return null;
        }

        var seasonNumber = identity.ExternalNumbersRestartAtOne
            ? 1
            : episode.ParentIndexNumber.Value;
        var resolved = await _fallbackService.ResolveEpisodeOverviewAsync(
                animeClickId,
                seasonNumber,
                episode.IndexNumber.Value,
                episode.GetProviderId("AnimeClick"),
                configuration,
                cancellationToken,
                episode.Path)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved?.Value) ? null : resolved.Value.Trim();
    }
}
