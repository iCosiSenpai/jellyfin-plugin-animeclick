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
    private readonly AnimeClickAniListResolver _aniListResolver;
    private readonly ILogger<AnimeClickMovieProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AnimeClickMovieProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickSeriesSearchProvider searchProvider,
        AnimeClickAniListResolver aniListResolver,
        ILogger<AnimeClickMovieProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _searchProvider = searchProvider;
        _aniListResolver = aniListResolver;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "AnimeClick";

    /// <summary>
    /// AnimeClick runs first because Jellyfin merges remote metadata with first-value-wins
    /// semantics. The post-merge authority provider then reapplies only enabled fields.
    /// </summary>
    public int Order => 0;

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new MetadataResult<Movie> { Item = new Movie() };

        var animeClickId = info.GetProviderId("AnimeClick");
        using var authorityLease = AnimeClickMetadataAuthorityStore.Begin<Movie>(info.Path, animeClickId);
        _logger.LogInformation(
            "AnimeClick MovieProvider.GetMetadata called: name=\"{Name}\" year={Year} providerId={ProviderId} path={Path}",
            info.Name,
            info.Year,
            string.IsNullOrWhiteSpace(animeClickId) ? "<none>" : animeClickId,
            info.Path ?? "<none>");
        string? url = null;

        if (!string.IsNullOrWhiteSpace(animeClickId)
            && !AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out url))
        {
            _logger.LogWarning("AnimeClick MovieProvider ignored invalid provider ID '{ProviderId}'", animeClickId);
            url = null;
        }

        if (url is null && !string.IsNullOrWhiteSpace(info.Name))
        {
            var search = await _searchProvider.SearchAsync(info.Name, configuration, cancellationToken, info.Year, seriesRequest: false);
            var first = search.FirstOrDefault();
            if (first is not null
                && first.ProviderIds.TryGetValue("AnimeClick", out var searchId)
                && AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, searchId, out var searchUrl))
            {
                url = searchUrl;
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
        }

        if ((configuration.EnableThemeSongs || configuration.EnableTrailers)
            && !anime.MultimediaLoaded)
        {
            await FetchMultimediaAsync(anime, configuration, cancellationToken);
        }

        if (configuration.EnableCast || configuration.EnableThemeSongs || configuration.EnableTrailers)
        {
            await _cache.SetAsync(cacheKey, anime, cancellationToken);
        }

        Map(result.Item, anime, configuration);

        // Map people to Jellyfin PersonInfo
        if (configuration.EnableCast)
        {
            result.People = anime.People
                .Select(p =>
                {
                    var person = new PersonInfo
                    {
                        Name = p.Name,
                        Type = MapPersonType(p.Type),
                        Role = p.Role,
                        ImageUrl = p.ImageUrl
                    };

                    if (!string.IsNullOrWhiteSpace(p.Id))
                    {
                        person.ProviderIds = new Dictionary<string, string>
                        {
                            ["AnimeClick"] = p.Id
                        };
                    }

                    return person;
                })
                .ToList();
        }

        result.HasMetadata = true;

        // Preserve a verified Jellyfin ID. Only discover a new mapping when none
        // exists, and require AniList title/year/format confidence before writing it.
        var existingAniListId = info.GetProviderId("AniList");
        if (!string.IsNullOrWhiteSpace(existingAniListId))
        {
            result.Item.SetProviderId("AniList", existingAniListId);
        }
        else if (string.IsNullOrWhiteSpace(result.Item.GetProviderId("AniList")))
        {
            var aniListId = await _aniListResolver.ResolveAniListIdAsync(
                anime.Id,
                anime.OriginalTitle ?? anime.Title,
                anime.Title,
                anime.ProductionYear,
                seriesRequest: false,
                configuration,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(aniListId))
            {
                result.Item.SetProviderId("AniList", aniListId);
                _logger.LogInformation(
                    "AnimeClick MovieProvider resolved validated AniList ID={AniListId} for \"{Title}\"",
                    aniListId,
                    anime.Title);
            }
        }

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

        // Consumed once by AnimeClickMovieAuthorityProvider after all remote providers.
        authorityLease.Capture(result.Item);
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
        // Defense in depth: see AnimeClickSeriesProvider.GetImageResponse.
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, url, out var imageUri))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(imageUri, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

            // The sigle are role sections of this same page. Reading them here costs no extra
            // request and covers the titles whose /multimedia page has no OP/ED block.
            var staffSongs = _parser.ParseStaffThemeSongs(staffHtml);
            anime.AddThemeSongs(staffSongs);

            _logger.LogInformation("AnimeClick cast: {Actors} doppiatori, {Staff} staff, {Songs} sigle da /staff per {Title}",
                actors.Count, staff.Count, staffSongs.Count, anime.Title);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore fetch cast/staff per {Title}", anime.Title);
        }
    }

    private async Task FetchMultimediaAsync(
        AnimeClickAnime anime,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var animeUrl = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, anime.Id);

        try
        {
            var html = await _client.GetStringAsync(animeUrl + "/multimedia", configuration, cancellationToken);
            var diagnostics = _parser.ParseMultimediaDiagnostics(html);
            anime.AddThemeSongs(diagnostics.Songs);
            anime.Trailers.AddRange(diagnostics.Trailers);
            anime.MultimediaLoaded = true;

            _logger.LogInformation(
                "AnimeClick multimedia: {Songs} OP/ED and {Trailers} labelled trailers/PV for {Title}",
                diagnostics.Songs.Count,
                diagnostics.Trailers.Count,
                anime.Title);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore fetch multimedia per {Title}", anime.Title);
        }
    }

    private static void Map(Movie target, AnimeClickAnime source, PluginConfiguration configuration)
    {
        // ── Campi localizzati (Italian-wins): sempre sovrascriventi quando AnimeClick
        //    ha un valore reale. Empty-guard per non svuotare campi già riempiti da
        //    altri provider con un valore nullo/vuoto. ──
        var italianName = configuration.PreferItalianTitle ? source.Title : source.OriginalTitle ?? source.Title;
        if (!string.IsNullOrWhiteSpace(italianName))
        {
            target.Name = italianName;
        }

        if (configuration.EnablePlot && !string.IsNullOrWhiteSpace(source.Overview))
        {
            target.Overview = source.Overview;
        }

        if (configuration.EnableGenres && source.Genres.Count > 0)
        {
            target.Genres = source.Genres.ToArray();
        }

        if (configuration.EnableTags && source.Tags.Count > 0)
        {
            var allTags = new List<string>(source.Tags);
            if (configuration.EnableThemeSongs)
            {
                allTags.AddRange(source.ThemeSongs.Select(song => song.DisplayName));
            }

            target.Tags = allTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else if (configuration.EnableThemeSongs && source.ThemeSongs.Count > 0)
        {
            target.Tags = source.ThemeSongs.Select(song => song.DisplayName).ToArray();
        }

        if (configuration.EnableProductionLocations && source.ProductionLocations.Count > 0)
        {
            target.ProductionLocations = source.ProductionLocations.ToArray();
        }

        if (configuration.EnableTrailers && source.Trailers.Count > 0)
        {
            target.RemoteTrailers = source.Trailers
                .Select(trailer => new MediaUrl { Name = trailer.Name, Url = trailer.Url })
                .ToArray();
        }

        // ── Campi non-italiani (language-neutral): solo se l'utente ha attivato
        //    OverwriteNonItalianFields. Default false = li lascia ad AniList/TMDB/OMDb
        //    (fill-gaps). Empty-guard comunque, per non cancellare valori esistenti. ──
        if (configuration.OverwriteNonItalianFields)
        {
            if (!string.IsNullOrWhiteSpace(source.OriginalTitle))
            {
                target.OriginalTitle = source.OriginalTitle;
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

            if (configuration.EnableStudios && source.Studios.Count > 0)
            {
                target.Studios = source.Studios.ToArray();
            }

            if (!string.IsNullOrWhiteSpace(source.OfficialRating))
            {
                target.OfficialRating = source.OfficialRating;
            }
        }

        foreach (var pair in source.ProviderIds)
        {
            target.SetProviderId(pair.Key, pair.Value);
        }
    }

    private static PersonKind MapPersonType(string type) => AnimeClickPersonKinds.Map(type);
}
