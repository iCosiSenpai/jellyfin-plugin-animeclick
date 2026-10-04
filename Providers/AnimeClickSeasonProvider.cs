using System;
using System.Collections.Generic;
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
/// Resolves season-specific AnimeClick pages for multi-season anime where each
/// season has a separate AnimeClick entry, without crossing related franchises.
/// </summary>
public class AnimeClickSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly AnimeClickSeasonResolver _seasonResolver;
    private readonly ILogger<AnimeClickSeasonProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickIntegratedMetadata? _integrated;
    private readonly AnimeClickClient? _client;
    private readonly AnimeClickCacheService? _cache;
    private readonly AnimeClickHtmlParser? _parser;
    private readonly AnimeClickCommunityService? _community;

    public AnimeClickSeasonProvider(
        AnimeClickSeasonResolver seasonResolver,
        ILogger<AnimeClickSeasonProvider> logger,
        IHttpClientFactory httpClientFactory, AnimeClickIntegratedMetadata? integrated = null,
        AnimeClickClient? client = null, AnimeClickCacheService? cache = null, AnimeClickHtmlParser? parser = null,
        AnimeClickCommunityService? community = null)
    {
        _seasonResolver = seasonResolver;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _integrated = integrated;
        _client = client;
        _cache = cache;
        _parser = parser;
        _community = community;
    }

    public string Name => "AnimeClick";

    public int Order => 0;

    public Task<MetadataResult<Season>> GetMetadata(
        SeasonInfo info,
        CancellationToken cancellationToken)
        => GetMetadataAsync(info, Plugin.Instance?.Configuration ?? new PluginConfiguration(), cancellationToken);

    internal async Task<MetadataResult<Season>> GetMetadataAsync(SeasonInfo info,
        PluginConfiguration configuration, CancellationToken cancellationToken)
    {
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

            return await CompleteAsync().ConfigureAwait(false);
        }

        var seasonNumber = info.IndexNumber;

        // A card the community approved for this exact season layout wins over the sequel traversal:
        // that is what the correction was made for. A season that already has its own ID keeps it.
        string? resolvedId = null;
        if (_community is not null
            && seasonNumber is >= 0 and <= AnimeClickCommunityData.MaximumSeasonNumber
            && string.IsNullOrWhiteSpace(info.ProviderIds?.GetValueOrDefault("AnimeClick")))
        {
            var approved = await _community
                .ResolveLibrarySeasonAsync(info.SeriesProviderIds ?? new Dictionary<string, string>(), seasonNumber.Value, info.Path, configuration, cancellationToken)
                .ConfigureAwait(false);
            if (approved is not null && AnimeClickClient.TryNormalizeAnimeClickId(approved, out var normalizedApproved))
            {
                resolvedId = normalizedApproved;
                _logger.LogInformation("AnimeClick: Season {Season} uses the community card {Id}", seasonNumber.Value, resolvedId);
            }
        }

        if (resolvedId is null)
        {
            if (!seasonNumber.HasValue || seasonNumber.Value <= 1)
            {
                return await CompleteAsync().ConfigureAwait(false);
            }

            resolvedId = await _seasonResolver
                .ResolveAsync(normalizedMainId, seasonNumber, configuration, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            result.Item.SetProviderId("AnimeClick", resolvedId);

            // A published result without the season number would erase it. See
            // AnimeClickNumberingGuard.
            AnimeClickNumberingGuard.Preserve(result.Item, info);
            result.HasMetadata = true;
            // Only a distinct season card describes this season; never copy the entire series plot.
            if (_client is not null && _cache is not null && _parser is not null
                && AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, resolvedId, out var url))
            {
                try
                {
                    var key = "anime::" + url;
                    var anime = await _cache.GetAsync<AnimeClickAnime>(key, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
                    if (anime is null)
                    {
                        anime = _parser.ParseAnimePage(url, await _client.GetStringAsync(url, configuration, cancellationToken).ConfigureAwait(false));
                        await _cache.SetAsync(key, anime, cancellationToken).ConfigureAwait(false);
                    }
                    if (configuration.PreferItalianTitle) result.Item.Name = AnimeClickMetadataText.Title(anime.Title)!;
                    if (configuration.EnablePlot) result.Item.Overview = AnimeClickMetadataText.ItalianOverview(anime.Overview);
                    if (configuration.EnableIntegratedMetadata || configuration.OverwriteNonItalianFields)
                    {
                        result.Item.PremiereDate = anime.PremiereDate?.UtcDateTime;
                        result.Item.ProductionYear = anime.ProductionYear;
                        if (configuration.EnableCommunityRating) result.Item.CommunityRating = anime.CommunityRating;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { _logger.LogWarning("AnimeClick season card unavailable; trying configured internal sources"); }
            }
            _logger.LogInformation(
                "AnimeClick: Season {Season} provider ID set → {Id}",
                seasonNumber,
                resolvedId);
        }

        return await CompleteAsync().ConfigureAwait(false);

        async Task<MetadataResult<Season>> CompleteAsync()
        {
            if (_integrated is not null)
                result.HasMetadata |= await _integrated.CompleteSeasonAsync(result.Item, info, configuration, cancellationToken).ConfigureAwait(false);
            if (result.HasMetadata) AnimeClickNumberingGuard.Preserve(result.Item, info);
            return result;
        }
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeasonInfo searchInfo,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => AnimeClickHttp.GetImageAsync(_httpClientFactory, url,
            Plugin.Instance?.Configuration ?? new PluginConfiguration(), cancellationToken);
}
