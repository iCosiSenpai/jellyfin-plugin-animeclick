using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickEpisodeTitleFallbackTests
{
    private static PluginConfiguration Configuration(bool ai = false) => new()
    {
        TmdbApiKey = "fake-tmdb", EnableAiTranslation = ai,
        AiProvider = "openai", AiModel = "test-model", AiApiKey = "fake-ai",
        AiEndpoint = "https://api.openai.com/v1/chat/completions"
    };
    private static AnimeClickEpisodeTitleRequest Request(string? episodeId = "88") => new(
        new Dictionary<string, string> { ["Tmdb"] = "1" },
        episodeId is null ? [] : new Dictionary<string, string> { ["Tmdb"] = episodeId }, 1, 1);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };
    private static string Translations(string language, string title, string overview = "") => JsonSerializer.Serialize(new
    {
        id = 88, translations = new[] { new { iso_639_1 = language, iso_3166_1 = language == "it" ? "IT" : "US", data = new { name = title, overview } } }
    });

    [Fact]
    public async Task NativeItalianTitleWorksWithoutOverviewOrAiAndUsesCache()
    {
        using var rig = new Rig(_ => Json(Translations("it", "Una nuova giornata")));
        var config = Configuration();
        var first = await rig.Service.ResolveAsync(Request(), config, CancellationToken.None);
        var second = await rig.Service.ResolveAsync(Request(), config, CancellationToken.None);
        Assert.Equal("Una nuova giornata", first!.Title); Assert.False(first.UsedAi);
        Assert.Equal(first, second); Assert.Equal(1, rig.Calls);
    }

    [Fact]
    public async Task EnglishTitleUsesDedicatedPromptAndTranslationCache()
    {
        var aiCalls = 0;
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Translations("en", "A new day"));
            aiCalls++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("titolo di un episodio", body); Assert.DoesNotContain("sinossi seguente", body);
            Assert.DoesNotContain("fake-tmdb", body); Assert.DoesNotContain("fake-ai", body);
            return Json("""{"choices":[{"message":{"content":"Una nuova giornata"}}]}""");
        });
        var result = await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None);
        Assert.Equal("Una nuova giornata", result!.Title); Assert.True(result.UsedAi);
        await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None);
        Assert.Equal(1, aiCalls);
    }

    [Fact]
    public async Task AnEnglishTitleIsNotMisreportedAsItalianWhenAiIsDisabled()
    {
        using var rig = new Rig(_ => Json(Translations("en", "A new day")));
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(), CancellationToken.None));
        Assert.Equal(1, rig.Calls);
    }

    [Theory]
    [InlineData("A new day")]
    [InlineData("The End")]
    public async Task ASourceLabellingEnglishAsItalianDoesNotBypassTranslation(string name)
    {
        using var rig = new Rig(_ => Json(Translations("it", name)));
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(), CancellationToken.None));
    }

    [Theory]
    [InlineData("A new day")]
    [InlineData("The beginning of the end")]
    public async Task AnUntranslatedAiReplyIsRejectedAndNotCached(string reply)
    {
        var aiCalls = 0;
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Translations("en", "A new day"));
            aiCalls++;
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply } } } }));
        });
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None));
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None));
        Assert.Equal(2, aiCalls);
    }

    [Fact]
    public async Task AnEnglishAnimeClickTitleIsTranslatedBeforeLookingForExternalEnglish()
    {
        var aiCalls = 0;
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Translations("en", "The End"));
            aiCalls++;
            Assert.Contains("A new beginning", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json("""{"choices":[{"message":{"content":"Un nuovo inizio"}}]}""");
        });
        var request = Request() with { AnimeClickEnglishTitle = "A new beginning", AnimeClickIdentity = "72" };
        var result = await rig.Service.ResolveAsync(request, Configuration(true), CancellationToken.None);
        Assert.Equal("AnimeClick", result!.Source); Assert.True(result.UsedAi);
        Assert.Equal("Un nuovo inizio", result.Title); Assert.Equal(1, aiCalls);
        Assert.Equal(2, rig.Calls);
    }

    [Fact]
    public async Task AnItalianExternalTitleWinsOverTranslatingAnimeClickEnglish()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "api.themoviedb.org"
            ? Json(Translations("it", "Un nuovo giorno")) : throw new InvalidOperationException("AI should not run"));
        var request = Request() with { AnimeClickEnglishTitle = "A new beginning", AnimeClickIdentity = "72" };
        var result = await rig.Service.ResolveAsync(request, Configuration(true), CancellationToken.None);
        Assert.Equal("Un nuovo giorno", result!.Title); Assert.False(result.UsedAi);
    }

    [Fact]
    public async Task KnownEpisodeIdentityCannotBeReplacedByTheSameCoordinateOfAnotherEpisode()
    {
        using var rig = new Rig(_ => Json(Translations("it", "Titolo del vicino")));
        Assert.Null(await rig.Service.ResolveAsync(Request("99"), Configuration(true), CancellationToken.None));
        Assert.Equal(1, rig.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    public async Task ExistingCoordinateWithoutTranslationsIsNeverRemappedEvenWithoutCache(int cacheHours)
    {
        var paths = new List<string>();
        using var rig = new Rig(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json("""{"id":88,"translations":[]}""");
        });
        var configuration = Configuration(); configuration.CacheHours = cacheHours;
        Assert.Null(await rig.Service.ResolveAsync(Request(null) with { Episode = 15 }, configuration, CancellationToken.None));
        Assert.Single(paths);
        Assert.EndsWith("/episode/15/translations", paths[0]);
    }

    [Fact]
    public async Task ProvenMissingCoordinateCanUseAContiguousAbsoluteMapping()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/tv/1/season/1/episode/15/translations" => Json("{}", HttpStatusCode.NotFound),
            "/3/tv/1" => Json("""{"seasons":[{"season_number":1,"episode_count":12},{"season_number":2,"episode_count":12}]}"""),
            "/3/tv/1/season/2/episode/3/translations" => Json(Translations("it", "Un nuovo capitolo")),
            _ => throw new InvalidOperationException("Unexpected mapping")
        });
        Assert.Equal("Un nuovo capitolo", (await rig.Service.ResolveAsync(Request() with { Episode = 15 }, Configuration(), CancellationToken.None))!.Title);
        Assert.Equal(3, rig.Calls);
    }

    [Theory]
    [InlineData("Episodio 01")]
    [InlineData("Episode 1")]
    [InlineData("```json")]
    [InlineData("Una riga\nUna sinossi")]
    public async Task GenericAndMalformedTitlesAreNotWrittenOrSentToAi(string title)
    {
        using var rig = new Rig(_ => Json(Translations("en", title)));
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None));
        Assert.Equal(1, rig.Calls);
    }

    [Fact]
    public async Task InvalidAiOutputIsNotCachedOrReturnedAsATitle()
    {
        var aiCalls = 0;
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Translations("en", "A new day"));
            aiCalls++;
            return Json("""{"choices":[{"message":{"content":"Episodio 1"}}]}""");
        });
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None));
        Assert.Null(await rig.Service.ResolveAsync(Request(), Configuration(true), CancellationToken.None));
        Assert.Equal(2, aiCalls);
    }

    [Fact]
    public async Task TvdbTitleComesFromAnExplicitTranslationOfTheKnownEpisode()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath switch
        {
            "/v4/login" => Json("""{"data":{"token":"fake-tvdb-token"}}"""),
            "/v4/series/100/episodes/default/eng" => Json("""{"data":{"episodes":[{"id":42,"seasonNumber":2,"number":3,"name":"English original"}]},"links":{"next":null}}"""),
            "/v4/episodes/42/translations/ita" => Json("""{"data":{"language":"ita","name":"Il ritorno"}}"""),
            _ => throw new InvalidOperationException("Unexpected source request")
        });
        var config = new PluginConfiguration { EnableTvdbSynopsis = true, TvdbApiKey = "fake-tvdb" };
        var request = new AnimeClickEpisodeTitleRequest(new Dictionary<string, string> { ["Tvdb"] = "100" },
            new Dictionary<string, string> { ["Tvdb"] = "42" }, 1, 15);
        var result = await rig.Service.ResolveAsync(request, config, CancellationToken.None);
        Assert.Equal("Il ritorno", result!.Title); Assert.Equal("TheTVDB", result.Source); Assert.False(result.UsedAi);
    }

    [Fact]
    public async Task NativeItalianInAnotherSourceWinsBeforeAnyAiTranslation()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "api.themoviedb.org"
            ? Json(Translations("it", "Un nuovo giorno")) : request.RequestUri!.AbsolutePath switch
            {
                "/v4/login" => Json("""{"data":{"token":"fake-token"}}"""),
                "/v4/series/100/episodes/default/eng" => Json("""{"data":{"episodes":[{"id":42,"seasonNumber":1,"number":1}]},"links":{"next":null}}"""),
                "/v4/episodes/42/translations/ita" => Json("{}", HttpStatusCode.NotFound),
                _ => throw new Xunit.Sdk.XunitException("AI or English lookup must not be called")
            });
        var configuration = Configuration(true); configuration.EnableTvdbSynopsis = true; configuration.TvdbApiKey = "fake-tvdb";
        var request = Request() with { SeriesIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Tvdb"] = "100" } };
        var result = await rig.Service.ResolveAsync(request, configuration, CancellationToken.None);
        Assert.Equal("Un nuovo giorno", result!.Title); Assert.False(result.UsedAi); Assert.Equal("TMDB", result.Source);
        Assert.Equal(4, rig.Calls);
    }

    [Fact]
    public async Task AnUnavailableSourceDoesNotStopTheOtherConfiguredSource()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "api.themoviedb.org"
            ? Json(Translations("it", "Un nuovo giorno")) : Json("{}", HttpStatusCode.InternalServerError));
        var configuration = Configuration(); configuration.EnableTvdbSynopsis = true; configuration.TvdbApiKey = "fake-tvdb";
        var request = Request() with { SeriesIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Tvdb"] = "100" } };
        Assert.Equal("Un nuovo giorno", (await rig.Service.ResolveAsync(request, configuration, CancellationToken.None))!.Title);
    }

    [Fact]
    public void TvdbRefusesContradictoryIdsAndDuplicateCoordinates()
    {
        var records = new List<TvdbEpisodeRecord>
        {
            new() { Id = 42, SeasonNumber = 1, Number = 1 },
            new() { Id = 43, SeasonNumber = 1, Number = 1 }
        };
        Assert.Null(AnimeClickTvdbClient.SelectTitleEpisode(records, 1, 1, "99"));
        Assert.Null(AnimeClickTvdbClient.SelectTitleEpisode(records, 1, 1, null));
        Assert.Equal(42, AnimeClickTvdbClient.SelectTitleEpisode(records, 1, 1, "42")!.Id);
        Assert.Null(AnimeClickTvdbClient.ParseTitleTranslation("""{"data":{"language":"eng","name":"Original title"}}""", "ita"));
    }

    [Fact]
    public async Task DisablingFallbackOrEpisodeTitlesAndMultiEpisodeFilesMakeNoRequests()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        var config = Configuration(); config.EnableEpisodeTitleFallback = false;
        Assert.Null(await rig.Service.ResolveAsync(Request(), config, CancellationToken.None));
        config.EnableEpisodeTitleFallback = true; config.EnableEpisodeTitles = false;
        Assert.Null(await rig.Service.ResolveAsync(Request(), config, CancellationToken.None));
        config.EnableEpisodeTitles = true;
        Assert.Null(await rig.Service.ResolveAsync(Request() with { EpisodeEnd = 2 }, config, CancellationToken.None));
        Assert.Null(await rig.Service.ResolveAsync(Request() with { SeriesIds = new Dictionary<string, string>() }, config, CancellationToken.None));
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeARequest()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ResolveAsync(Request(), Configuration(), cancellation.Token));
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task ConcurrentExternalIdentityChangePreventsSavingAFallbackTitle()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episodio 1", IndexNumber = 1, ParentIndexNumber = 1 };
        episode.SetProviderId("Tvdb", "42"); var writes = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById") return episode;
            if (method.Name == "UpdateItemAsync") writes++;
            return TestDoubles.DefaultReturn(method);
        });
        var resolver = TestDoubles.Proxy<IAnimeClickTitleResolver>((_, _) =>
        {
            episode.SetProviderId("Tvdb", "43");
            return Task.FromResult<string?>("Un nuovo giorno");
        });
        Assert.False(await new AnimeClickTitleRepairService(library, resolver).RepairAsync(episode, new(), CancellationToken.None));
        Assert.Equal(0, writes); Assert.Equal("Episodio 1", episode.Name);
    }

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryAnimeClickCache _cache = new();
        private readonly Handler _handler;
        public int Calls { get; private set; }
        public AnimeClickEpisodeTitleFallback Service { get; }
        public Rig(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _handler = new Handler(request => { Calls++; return response(request); });
            var factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));
            var tmdb = new AnimeClickTmdbClient(factory, _cache.Cache, NullLogger<AnimeClickTmdbClient>.Instance);
            var tvdb = new AnimeClickTvdbClient(factory, _cache.Cache, NullLogger<AnimeClickTvdbClient>.Instance);
            var translator = new AnimeClickAiTranslator(factory, _cache.Cache, NullLogger<AnimeClickAiTranslator>.Instance);
            Service = new(tmdb, tvdb, translator, NullLogger<AnimeClickEpisodeTitleFallback>.Instance);
        }
        public void Dispose() { _handler.Dispose(); _cache.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
