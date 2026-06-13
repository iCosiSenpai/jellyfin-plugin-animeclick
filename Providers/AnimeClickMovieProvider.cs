using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Provides metadata for anime movies from AnimeClick.
/// </summary>
public class AnimeClickMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickSeriesSearchProvider _searchProvider;
    private readonly ILogger<AnimeClickMovieProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AnimeClickMovieProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickSeriesSearchProvider searchProvider,
        ILogger<AnimeClickMovieProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _searchProvider = searchProvider;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "AnimeClick";
    /// <summary>
    /// Run AFTER the other metadata providers so that fields we don't populate
    /// (Studios, OfficialRating, Genres when empty, etc.) are filled in first
    /// by AniList / TheMovieDb / OMDb, and only then we overlay the Italian
    /// title, Italian overview, Italian genres/tags, cast, etc.
    /// </summary>
    public int Order => 100;

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Movie> { Item = new Movie() };

        var animeClickId = info.GetProviderId("AnimeClick");
        _logger.LogInformation(
            "AnimeClick MovieProvider.GetMetadata called: name=\"{Name}\" year={Year} providerId={ProviderId} path={Path}",
            info.Name,
            info.Year,
            string.IsNullOrWhiteSpace(animeClickId) ? "<none>" : animeClickId,
            info.Path ?? "<none>");
        string? url = null;

        if (!string.IsNullOrWhiteSpace(animeClickId))
        {
            url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, animeClickId);
        }
        else if (!string.IsNullOrWhiteSpace(info.Name))
        {
            var search = await _searchProvider.SearchAsync(info.Name, configuration, cancellationToken, info.Year, seriesRequest: false);
            var first = search.FirstOrDefault();
            if (first is not null && first.ProviderIds.TryGetValue("AnimeClick", out var searchId))
            {
                url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, searchId);
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return result;
        }

        var cacheKey = $"anime::{url}";
        var cached = await _cache.GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken);
        var anime = cached ?? await FetchAnimeAsync(url, configuration, cacheKey, cancellationToken);
        if (anime is null)
        {
            return result;
        }

        // Fetch cast & staff if enabled and not already cached
        if (configuration.EnableCast && anime.People.Count == 0)
        {
            await FetchPeopleAsync(anime, configuration, cancellationToken);
            await _cache.SetAsync(cacheKey, anime, cancellationToken);
        }

        Map(result.Item, anime, configuration);

        // Map people to Jellyfin PersonInfo
        if (configuration.EnableCast)
        {
            result.People = anime.People
                .Select(p => new PersonInfo
                {
                    Name = p.Name,
                    Type = MapPersonType(p.Type),
                    Role = p.Role,
                    ImageUrl = p.ImageUrl
                })
                .ToList();
        }

        result.HasMetadata = true;

        // Diagnostic: report which fields we left empty so the next provider
        // (AniList, TMDB, OMDb) can fill them in.
        var emptyFields = new List<string>();
        if (configuration.EnableGenres && anime.Genres.Count == 0) emptyFields.Add("Genres");
        if (configuration.EnableStudios && anime.Studios.Count == 0) emptyFields.Add("Studios");
        if (string.IsNullOrWhiteSpace(anime.OfficialRating)) emptyFields.Add("OfficialRating");
        if (emptyFields.Count > 0)
        {
            _logger.LogInformation(
                "AnimeClick MovieProvider leaving fields for downstream providers: {Fields} (title=\"{Title}\")",
                string.Join(", ", emptyFields), anime.Title);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (searchInfo.ProviderIds.TryGetValue("AnimeClick", out var providerId) && !string.IsNullOrWhiteSpace(providerId))
        {
            return await _searchProvider.SearchAsync(providerId, configuration, cancellationToken, searchInfo.Year, seriesRequest: false);
        }

        return string.IsNullOrWhiteSpace(searchInfo.Name)
            ? []
            : await _searchProvider.SearchAsync(searchInfo.Name, configuration, cancellationToken, searchInfo.Year, seriesRequest: false);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private async Task<AnimeClickAnime?> FetchAnimeAsync(string url, PluginConfiguration configuration, string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var anime = _parser.ParseAnimePage(url, html);
            await _cache.SetAsync(cacheKey, anime, cancellationToken);
            return anime;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore parsing AnimeClick per {Url}", url);
            return null;
        }
    }

    private async Task FetchPeopleAsync(AnimeClickAnime anime, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var baseUrl = configuration.BaseUrl;
        var animeUrl = AnimeClickClient.BuildAnimeUrl(baseUrl, anime.Id);

        try
        {
            var charsHtml = await _client.GetStringAsync(animeUrl + "/personaggi", configuration, cancellationToken);
            var actors = _parser.ParseCharactersPage(charsHtml, baseUrl);
            anime.People.AddRange(actors);

            var staffHtml = await _client.GetStringAsync(animeUrl + "/staff", configuration, cancellationToken);
            var staff = _parser.ParseStaffPage(staffHtml, baseUrl);
            anime.People.AddRange(staff);

            _logger.LogInformation("AnimeClick cast: {Actors} doppiatori, {Staff} staff per {Title}",
                actors.Count, staff.Count, anime.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore fetch cast/staff per {Title}", anime.Title);
        }
    }

    private static void Map(Movie target, AnimeClickAnime source, PluginConfiguration configuration)
    {
        target.Name = configuration.PreferItalianTitle ? source.Title : source.OriginalTitle ?? source.Title;
        target.OriginalTitle = source.OriginalTitle;

        if (configuration.EnablePlot)
        {
            target.Overview = source.Overview;
        }

        if (source.ProductionYear.HasValue)
        {
            target.ProductionYear = source.ProductionYear.Value;
        }

        if (source.PremiereDate.HasValue)
        {
            target.PremiereDate = source.PremiereDate.Value.UtcDateTime;
        }

        if (configuration.EnableCommunityRating && source.CommunityRating.HasValue)
        {
            target.CommunityRating = source.CommunityRating.Value;
        }

        if (configuration.EnableGenres && source.Genres.Count > 0)
        {
            target.Genres = source.Genres.ToArray();
        }

        if (configuration.EnableStudios && source.Studios.Count > 0)
        {
            target.Studios = source.Studios.ToArray();
        }

        if (configuration.EnableTags && source.Tags.Count > 0)
        {
            target.Tags = source.Tags.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(source.OfficialRating))
        {
            target.OfficialRating = source.OfficialRating;
        }

        foreach (var pair in source.ProviderIds)
        {
            target.SetProviderId(pair.Key, pair.Value);
        }
    }

    private static PersonKind MapPersonType(string type) => type switch
    {
        "Director" => PersonKind.Director,
        "Writer" => PersonKind.Writer,
        "Composer" => PersonKind.Composer,
        _ => PersonKind.Actor
    };
}
