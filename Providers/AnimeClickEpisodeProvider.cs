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
/// </summary>
public class AnimeClickEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickEpisodeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AnimeClickEpisodeProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickEpisodeProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "AnimeClick";
    public int Order => 0;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Episode> { Item = new Episode() };

        if (!configuration.EnableEpisodeTitles)
        {
            // Propagate provider ID only
            var id = info.GetProviderId("AnimeClick");
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Item.SetProviderId("AnimeClick", id);
                result.HasMetadata = true;
            }
            return result;
        }

        // Get AnimeClick ID from parent series
        var animeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick")
                           ?? info.GetProviderId("AnimeClick");

        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return result;
        }

        var episodeNumber = info.IndexNumber;
        if (!episodeNumber.HasValue || episodeNumber.Value <= 0)
        {
            return result;
        }

        try
        {
            // Try to get cached episode list
            var cacheKey = $"episodes::{animeClickId}";
            var cachedEpisodes = await _cache.GetAsync<List<AnimeClickEpisode>>(cacheKey, configuration.CacheHours, cancellationToken);

            List<AnimeClickEpisode>? episodes = cachedEpisodes;

            if (episodes is null || episodes.Count == 0)
            {
                // Fetch episodes page
                var animeUrl = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
                var episodesUrl = animeUrl + "/episodi";

                _logger.LogInformation("AnimeClick: Fetching episodes from {Url}", episodesUrl);
                var html = await _client.GetStringAsync(episodesUrl, configuration, cancellationToken);
                episodes = _parser.ParseEpisodesPage(html, configuration.BaseUrl);

                // If there are more pages, fetch them too (pagination)
                // Look for page=2, page=3, etc.
                for (var page = 2; page <= 30; page++) // Safety limit
                {
                    var nextUrl = episodesUrl + $"?page={page}";
                    try
                    {
                        var nextHtml = await _client.GetStringAsync(nextUrl, configuration, cancellationToken);
                        var nextEpisodes = _parser.ParseEpisodesPage(nextHtml, configuration.BaseUrl);
                        if (nextEpisodes.Count == 0) break; // No more pages

                        // AnimeClick returns page 1 if the page parameter is out of bounds
                        // If we see the first episode of next page already in our list, it's a loop
                        if (episodes.Any(e => e.Number == nextEpisodes[0].Number)) break;

                        episodes.AddRange(nextEpisodes);
                    }
                    catch
                    {
                        break; // Page doesn't exist or error
                    }
                }

                // Cache the full list
                await _cache.SetAsync(cacheKey, episodes, cancellationToken);
                _logger.LogInformation("AnimeClick: Cached {Count} episodes for {Id}", episodes.Count, animeClickId);
            }

            // Find matching episode
            var match = episodes.FirstOrDefault(e => e.Number == episodeNumber.Value);
            if (match is not null && !string.IsNullOrWhiteSpace(match.Title))
            {
                // Skip generic placeholder titles like "Episodio 1", "Episodio 2", etc.
                // This allows Jellyfin to fall back to English providers (TMDB, TVDB)
                // which typically have better episode titles.
                var isGeneric = System.Text.RegularExpressions.Regex.IsMatch(
                    match.Title, @"^Episodio\s+\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (isGeneric)
                {
                    _logger.LogDebug("AnimeClick: Episode {Num} has generic title \"{Title}\", skipping for English fallback",
                        match.Number, match.Title);
                    return result;
                }
                result.Item.Name = match.Title;
                result.Item.SetProviderId("AnimeClick", animeClickId);

                if (match.DurationMinutes.HasValue)
                {
                    result.Item.RunTimeTicks = TimeSpan.FromMinutes(match.DurationMinutes.Value).Ticks;
                }

                result.HasMetadata = true;
                _logger.LogDebug("AnimeClick: Episode {Num} = \"{Title}\"", match.Number, match.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Error fetching episode {Num} for {Id}", episodeNumber, animeClickId);
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
}
