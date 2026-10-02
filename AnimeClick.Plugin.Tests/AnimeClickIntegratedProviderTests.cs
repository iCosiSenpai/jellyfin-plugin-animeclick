using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickIntegratedProviderTests
{
    private static PluginConfiguration Config() => new() { TmdbApiKey = "test-tmdb", EnableAiTranslation = false,
        EnableTags = false, EnableGenres = false, RequestDelayMilliseconds = 0 };
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    private const string Details = """{"id":1,"original_name":"Original","original_title":"Original","first_air_date":"2002-10-03","release_date":"2002-10-03","vote_average":8.4,"vote_count":100,"production_companies":[{"name":"Pierrot"}],"production_countries":[{"iso_3166_1":"JP"}],"status":"Ended","last_air_date":"2007-02-08","external_ids":{"tvdb_id":81797,"imdb_id":"tt0409591"},"aggregate_credits":{"cast":[{"id":4,"name":"Junko Takeuchi","roles":[{"character":"Naruto"}],"profile_path":"/portrait.jpg"}],"crew":[{"id":5,"name":"Hayato Date","jobs":[{"job":"Director"}]}]},"credits":{"cast":[{"id":4,"name":"Junko Takeuchi","character":"Naruto"}],"crew":[{"id":5,"name":"Hayato Date","job":"Director"}]}}""";
    private const string Search = """{"results":[{"id":1,"name":"Naruto","title":"Naruto","genre_ids":[16],"first_air_date":"2002-10-03","release_date":"2002-10-03"}]}""";
    private const string Italian = """{"id":1,"translations":[{"iso_639_1":"it","iso_3166_1":"IT","data":{"name":"Naruto","title":"Naruto","overview":"La storia italiana di Naruto."}}]}""";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TheActualProviderWorksWithNoOtherMetadataPluginOrExternalIds(bool movie)
    {
        using var rig = new Rig(request => request.RequestUri!.Host != "api.themoviedb.org" ? Json("unavailable", HttpStatusCode.ServiceUnavailable)
            : request.RequestUri.AbsolutePath.Contains("search/") ? Json(Search)
            : request.RequestUri.AbsolutePath.EndsWith("translations") ? Json(Italian) : Json(Details));
        var config = Config();
        if (movie)
        {
            var result = await rig.Movie.GetMetadataAsync(new MovieInfo { Name = "Naruto", Year = 2002, Path = "/Anime/Naruto.mkv" }, config, CancellationToken.None);
            Assert.True(result.HasMetadata); Assert.Equal("Naruto", result.Item.Name); Assert.Equal("1", result.Item.GetProviderId("Tmdb"));
            Assert.Equal("La storia italiana di Naruto.", result.Item.Overview); Assert.Equal("Pierrot", Assert.Single(result.Item.Studios));
            Assert.Contains(result.People, v => v.Name == "Hayato Date");
        }
        else
        {
            var result = await rig.Series.GetMetadataAsync(new SeriesInfo { Name = "Naruto", Year = 2002, Path = "/Anime/Naruto" }, config, CancellationToken.None);
            Assert.True(result.HasMetadata); Assert.Equal("Naruto", result.Item.Name); Assert.Equal("81797", result.Item.GetProviderId("Tvdb"));
            Assert.Equal("tt0409591", result.Item.GetProviderId("Imdb")); Assert.Equal(2002, result.Item.ProductionYear);
            Assert.Equal(SeriesStatus.Ended, result.Item.Status); Assert.NotNull(result.Item.EndDate);
            Assert.Contains(result.People, v => v.Name == "Junko Takeuchi" && v.Role == "Naruto");
        }
        Assert.True(rig.Paths.FindIndex(v => v.Host == "www.animeclick.it") < rig.Paths.FindIndex(v => v.Host == "api.themoviedb.org"));
    }

    [Fact]
    public async Task NativeAnimeClickValuesAndManualIdentitiesWinPerField()
    {
        using var rig = new Rig(_ => Json(Details));
        var result = new MetadataResult<Series> { Item = new Series { OriginalTitle = "Titolo AC", ProductionYear = 1999,
            Studios = ["Studio AC"], CommunityRating = 9, Overview = "La trama italiana di AnimeClick.", Name = "Titolo italiano" },
            People = [new PersonInfo { Name = "Voce AC", Type = Jellyfin.Data.Enums.PersonKind.Actor }] };
        var ids = new Dictionary<string, string> { ["Tmdb"] = "1", ["AniList"] = "123" };
        await rig.Integrated.CompleteAsync(result, "Naruto", 2002, ids, null, false, Config(), CancellationToken.None);
        Assert.Equal("Titolo AC", result.Item.OriginalTitle); Assert.Equal(1999, result.Item.ProductionYear);
        Assert.Equal(9, result.Item.CommunityRating); Assert.Equal(["Studio AC"], result.Item.Studios);
        Assert.Equal("La trama italiana di AnimeClick.", result.Item.Overview); Assert.Equal("123", result.Item.GetProviderId("AniList"));
        Assert.Contains(result.People, v => v.Name == "Voce AC"); Assert.DoesNotContain(result.People, v => v.Name == "Junko Takeuchi");
        Assert.Contains(result.People, v => v.Name == "Hayato Date");
    }

    [Theory]
    [InlineData(null, false, 2)] [InlineData(2002, false, 1)] [InlineData(2009, false, 0)] [InlineData(2002, true, 0)]
    public void AutomaticIdentificationChecksAnimationYearAndMovieNamespace(int? year, bool movie, int count)
    {
        var json = """{"results":[{"id":1,"name":"Naruto","genre_ids":[16],"first_air_date":"2002-10-03"},{"id":2,"name":"Naruto","genre_ids":[16],"first_air_date":"2015-01-01"},{"id":3,"name":"Naruto","genre_ids":[28],"first_air_date":"2002-10-03"},{"id":4,"name":"Narutoooooooo","genre_ids":[16],"first_air_date":"2002-10-03"}]}""";
        Assert.Equal(count, AnimeClickTmdbClient.SelectAnimeIds(json, "Naruto", year, movie).Length);
    }

    [Fact]
    public async Task AmbiguousRemakesDoNotGetAProviderId()
    {
        using var rig = new Rig(_ => Json("""{"results":[{"id":1,"name":"Naruto","genre_ids":[16]},{"id":2,"name":"Naruto","genre_ids":[16]}]}"""));
        Assert.Null(await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string>(), null, "Naruto", null, false, Config(), CancellationToken.None));
    }

    [Fact]
    public async Task VerifiedRomajiAliasWorksWithoutAcceptingAFirstSearchResult()
    {
        using var rig = new Rig(request => Json(request.RequestUri!.AbsolutePath.EndsWith("alternative_titles")
            ? """{"id":1,"results":[{"title":"Sousou no Frieren"}]}"""
            : """{"results":[{"id":1,"name":"Frieren","genre_ids":[16],"first_air_date":"2023-09-29"}]}"""));
        Assert.Equal(1, await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string>(), "Sousou no Frieren", null, 2023, false, Config(), CancellationToken.None));
    }

    [Fact]
    public async Task SequelIndicatorsAreNotRemovedDuringExternalIdentification()
    {
        using var rig = new Rig(request => Json(request.RequestUri!.AbsolutePath.EndsWith("alternative_titles") ? """{"id":1,"results":[]} """ : Search));
        Assert.Null(await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string>(), null, "Naruto 2nd Season", null, false, Config(), CancellationToken.None));
        Assert.Contains(rig.Paths, v => v.Query.Contains("Naruto%202nd%20Season"));
    }

    [Theory]
    [InlineData("Imdb", "tt0409591", "imdb_id")] [InlineData("Tvdb", "81797", "tvdb_id")]
    public async Task PublicIdsAreResolvedDirectlyWithoutTitleSearch(string provider, string value, string source)
    {
        var calls = 0;
        using var rig = new Rig(request => { Assert.Contains(source, request.RequestUri!.Query); return Json(++calls == 1 ? """{"tv_results":"invalid"}""" : """{"tv_results":[{"id":1}]}"""); });
        Assert.Null(await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string> { [provider] = value }, null, null, null, false, Config(), CancellationToken.None));
        Assert.Equal(1, await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string> { [provider] = value }, null, null, null, false, Config(), CancellationToken.None));
        Assert.Equal(2, rig.Paths.Count);
    }

    [Theory]
    [InlineData("-1")] [InlineData("bad")] [InlineData("https://evil.invalid/1")]
    public async Task InvalidStoredIdNeverBecomesAnAutomaticDifferentMatch(string value)
    {
        using var rig = new Rig(_ => throw new Exception("No HTTP allowed"));
        Assert.Null(await rig.Tmdb.ResolveAnimeIdAsync(new Dictionary<string, string> { ["Tmdb"] = value }, null, "Naruto", 2002, false, Config(), CancellationToken.None));
        Assert.Empty(rig.Paths);
    }

    [Fact]
    public async Task OutagesAndWrongIdentityResponsesAreRetryableNotCachedMatches()
    {
        var requests = 0;
        using var rig = new Rig(_ => ++requests == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable) : requests == 2 ? Json("""{"id":99}""") : Json(Details));
        Assert.Null(await rig.Tmdb.GetIntegratedDetailsAsync(1, false, Config(), CancellationToken.None));
        Assert.Null(await rig.Tmdb.GetIntegratedDetailsAsync(1, false, Config(), CancellationToken.None));
        Assert.NotNull(await rig.Tmdb.GetIntegratedDetailsAsync(1, false, Config(), CancellationToken.None));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task PublicIdConflictsRejectTheEntireAlternativeMetadata()
    {
        using var rig = new Rig(_ => Json(Details));
        var result = new MetadataResult<Series> { Item = new Series() };
        await rig.Integrated.CompleteAsync(result, "Naruto", 2002, new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt00000" }, null, false, Config(), CancellationToken.None);
        Assert.False(result.HasMetadata); Assert.Null(result.Item.OriginalTitle); Assert.Equal("tt00000", result.Item.GetProviderId("Imdb"));
    }

    [Fact]
    public async Task FieldSwitchesPreventImportsAndUnnecessaryCreditRequests()
    {
        using var rig = new Rig(request => { Assert.DoesNotContain("credits", request.RequestUri!.Query); Assert.DoesNotContain("videos", request.RequestUri.Query); return Json(Details); });
        var config = Config(); config.EnableCast = config.EnableCommunityRating = config.EnableProductionLocations = config.EnableStudios = config.EnableTrailers = false;
        var result = new MetadataResult<Movie> { Item = new Movie() };
        await rig.Integrated.CompleteAsync(result, null, null, new Dictionary<string, string> { ["Tmdb"] = "1" }, null, true, config, CancellationToken.None);
        Assert.Null(result.People); Assert.Empty(result.Item.Studios); Assert.Empty(result.Item.ProductionLocations); Assert.Null(result.Item.CommunityRating);
    }

    [Fact]
    public async Task IntegratedMetadataCanBeDisabledWithoutAnApiRequest()
    {
        using var rig = new Rig(_ => throw new Exception("No HTTP allowed"));
        var config = Config(); config.EnableIntegratedMetadata = false;
        await rig.Integrated.CompleteAsync(new MetadataResult<Movie> { Item = new Movie() }, "Naruto", 2002, new Dictionary<string, string>(), null, true, config, CancellationToken.None);
        Assert.Empty(rig.Paths);
    }

    [Theory]
    [InlineData(0, 0, null, true)] [InlineData(1, 12, null, true)] [InlineData(1, 12, 13, false)]
    public async Task EpisodeNumbersArePreservedAndMultipleEpisodeFilesAreSkipped(int season, int episode, int? end, bool expected)
    {
        using var rig = new Rig(_ => Json(JsonSerializer.Serialize(new { id = 40, season_number = season, episode_number = episode, air_date = "2024-01-01", external_ids = new { tvdb_id = 80 } })));
        var item = new Episode();
        var info = new EpisodeInfo { ParentIndexNumber = season, IndexNumber = episode, IndexNumberEnd = end, SeriesProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" } };
        Assert.Equal(expected, await rig.Integrated.CompleteEpisodeAsync(item, info, Config(), CancellationToken.None));
        if (expected) { Assert.Equal(season, item.ParentIndexNumber); Assert.Equal(episode, item.IndexNumber); Assert.Equal(end, item.IndexNumberEnd); Assert.Equal("40", item.GetProviderId("Tmdb")); }
        else Assert.Empty(rig.Paths);
    }

    [Fact]
    public async Task DiscordantEpisodeCoordinatesOrIdsAreNotImported()
    {
        using var rig = new Rig(_ => Json("""{"id":40,"season_number":1,"episode_number":3}"""));
        Assert.Null(await rig.Tmdb.GetIntegratedChildAsync(1, 1, 2, null, Config(), CancellationToken.None));
        Assert.Null(await rig.Tmdb.GetIntegratedChildAsync(1, 1, 3, "99", Config(), CancellationToken.None));
    }

    [Fact]
    public async Task SeasonMetadataWorksWithoutAnimeClickIdAndRetainsItsNumber()
    {
        using var rig = new Rig(_ => Json("""{"id":20,"season_number":0,"air_date":"2024-01-01","translations":{"translations":[{"iso_639_1":"it","iso_3166_1":"IT","data":{"name":"Speciali","overview":"Gli episodi speciali della serie."}}]}}"""));
        var result = await rig.Season.GetMetadataAsync(new SeasonInfo { IndexNumber = 0, SeriesProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" } }, Config(), CancellationToken.None);
        Assert.True(result.HasMetadata); Assert.Equal(0, result.Item.IndexNumber); Assert.Equal("20", result.Item.GetProviderId("Tmdb"));
        Assert.Equal("Gli episodi speciali della serie.", result.Item.Overview);
    }

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryAnimeClickCache _cache = new();
        private readonly Handler _handler;
        private readonly AnimeClickTranslationQueue _queue;
        public List<Uri> Paths { get; } = [];
        public AnimeClickTmdbClient Tmdb { get; }
        public AnimeClickIntegratedMetadata Integrated { get; }
        public AnimeClickSeriesProvider Series { get; }
        public AnimeClickMovieProvider Movie { get; }
        public AnimeClickSeasonProvider Season { get; }
        public Rig(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _handler = new(request => { Paths.Add(request.RequestUri!); return response(request); });
            var factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));
            Tmdb = new(factory, _cache.Cache, NullLogger<AnimeClickTmdbClient>.Instance);
            var tvdb = new AnimeClickTvdbClient(factory, _cache.Cache, NullLogger<AnimeClickTvdbClient>.Instance);
            var translator = new AnimeClickAiTranslator(factory, _cache.Cache, NullLogger<AnimeClickAiTranslator>.Instance);
            var scheduler = new AnimeClickMetadataRefreshScheduler(TestDoubles.Proxy<ILibraryManager>(), TestDoubles.Proxy<IProviderManager>(), TestDoubles.Proxy<IFileSystem>(), new AnimeClickMetadataRefreshIntentRegistry(), NullLogger<AnimeClickMetadataRefreshScheduler>.Instance);
            _queue = new(translator, _cache.Cache, scheduler, NullLogger<AnimeClickTranslationQueue>.Instance, () => Config());
            var text = new AnimeClickAnimeTextFallback(Tmdb, tvdb, translator, _queue);
            Integrated = new(Tmdb, translator);
            var client = new AnimeClickClient(factory, NullLogger<AnimeClickClient>.Instance);
            var parser = new AnimeClickHtmlParser();
            var search = new AnimeClickSeriesSearchProvider(client, _cache.Cache, parser, NullLogger<AnimeClickSeriesSearchProvider>.Instance);
            var aniList = new AnimeClickAniListResolver(factory, _cache.Cache, NullLogger<AnimeClickAniListResolver>.Instance);
            Series = new(client, _cache.Cache, parser, search, aniList, NullLogger<AnimeClickSeriesProvider>.Instance, Tmdb, factory, textFallback: text, integrated: Integrated);
            Movie = new(client, _cache.Cache, parser, search, aniList, NullLogger<AnimeClickMovieProvider>.Instance, factory, Tmdb, textFallback: text, integrated: Integrated);
            Season = new(new AnimeClickSeasonResolver(client, _cache.Cache, parser, NullLogger<AnimeClickSeasonResolver>.Instance), NullLogger<AnimeClickSeasonProvider>.Instance, factory, Integrated);
        }
        public void Dispose() { _queue.Dispose(); _handler.Dispose(); _cache.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
