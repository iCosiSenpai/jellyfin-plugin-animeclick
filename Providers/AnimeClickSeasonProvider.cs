using System;
using System.Collections.Generic;
using System.Net;
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
/// Resolves season-specific AnimeClick pages for multi-season anime where each
/// season has a separate AnimeClick entry, without crossing related franchises.
/// </summary>
public class AnimeClickSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly ILogger<AnimeClickSeasonProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AnimeClickSeasonProvider(
        AnimeClickSeasonResolver seasonResolver,
        ILogger<AnimeClickSeasonProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _seasonResolver = seasonResolver;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "AnimeClick";

    public int Order => 0;

    public async Task<MetadataResult<Season>> GetMetadata(
        SeasonInfo info,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Season> { Item = new Season() };

        var mainAnimeClickId = info.SeriesProviderIds?.GetValueOrDefault("AnimeClick");
        _logger.LogInformation(
            "AnimeClick SeasonProvider.GetMetadata called: name=\"{Name}\" S{Season} seriesProviderId={SeriesProviderId}",
            info.Name,
            info.IndexNumber,
            string.IsNullOrWhiteSpace(mainAnimeClickId) ? "<none>" : mainAnimeClickId);

        if (string.IsNullOrWhiteSpace(mainAnimeClickId)
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainAnimeClickId, out var normalizedMainId)
            || !AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, normalizedMainId, out _))
        {
            if (!string.IsNullOrWhiteSpace(mainAnimeClickId))
            {
                _logger.LogWarning(
                    "AnimeClick SeasonProvider ignored invalid series provider ID '{ProviderId}'",
                    mainAnimeClickId);
            }

            return result;
        }

        var seasonNumber = info.IndexNumber;
        if (!seasonNumber.HasValue || seasonNumber.Value <= 1)
        {
            return result;
        }

        var resolvedId = await _seasonResolver
            .ResolveAsync(normalizedMainId, seasonNumber, configuration, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            result.Item.SetProviderId("AnimeClick", resolvedId);
            result.HasMetadata = true;
            _logger.LogInformation(
                "AnimeClick: Season {Season} provider ID set → {Id}",
                seasonNumber.Value,
                resolvedId);
        }

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeasonInfo searchInfo,
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
}
