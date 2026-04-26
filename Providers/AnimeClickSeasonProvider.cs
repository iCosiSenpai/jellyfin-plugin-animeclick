using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Resolves season-specific AnimeClick pages for multi-season anime
/// where each season has a separate AnimeClick entry.
/// Sets the AnimeClick provider ID on Season entities.
/// </summary>
public class AnimeClickSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickSeasonProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AnimeClickSeasonProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickSeasonProvider> logger,
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

    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Season> { Item = new Season() };

        var mainAnimeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick");

        if (string.IsNullOrWhiteSpace(mainAnimeClickId))
        {
            return result;
        }

        var seasonNumber = info.IndexNumber;
        if (!seasonNumber.HasValue || seasonNumber.Value <= 1)
        {
            return result;
        }

        var resolvedId = await ResolveSeasonAnimeClickIdAsync(
            mainAnimeClickId, seasonNumber.Value, configuration, cancellationToken);

        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            result.Item.SetProviderId("AnimeClick", resolvedId);
            result.HasMetadata = true;

            _logger.LogInformation("AnimeClick: Season {S} provider ID set → {Id}",
                seasonNumber.Value, resolvedId);
        }

        return result;
    }

    private async Task<string?> ResolveSeasonAnimeClickIdAsync(
        string mainId, int seasonNumber,
        PluginConfiguration config, CancellationToken ct)
    {
        var cacheKey = $"seasonMap::{mainId}::{seasonNumber}";
        var cached = await _cache.GetAsync<string>(cacheKey, config.CacheHours, ct);
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
                var relIndex = seasonNumber - 2;
                if (relIndex >= 0 && relIndex < tvRelations.Count)
                {
                    var candidateId = tvRelations[relIndex].AnimeClickId;
                    if (!string.IsNullOrWhiteSpace(candidateId) && candidateId != mainId)
                    {
                        resolvedId = candidateId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: Season relations resolution failed for {Id} S{S}", mainId, seasonNumber);
        }

        await _cache.SetAsync(cacheKey, resolvedId ?? "__same__", ct);
        return resolvedId;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
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
