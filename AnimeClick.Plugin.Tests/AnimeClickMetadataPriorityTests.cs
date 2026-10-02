using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using AnimeClick.Plugin.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickMetadataPriorityTests
{
    private static readonly Dictionary<string, string> TmdbIds = new() { ["Tmdb"] = "1" };
    private static PluginConfiguration Config(bool ai = false) => new()
    {
        TmdbApiKey = "fake-tmdb", EnableAiTranslation = ai, EnableGenres = false, EnableTags = false,
        AiProvider = "openai", AiEndpoint = "https://api.openai.com/v1/chat/completions",
        AiApiKey = "fake-ai", AiModel = "test-model", RequestDelayMilliseconds = 0
    };
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };
    private static string Text(string language, string name = "", string overview = "", bool movie = false, int id = 1)
        => JsonSerializer.Serialize(new { id, translations = new[] { new { iso_639_1 = language,
            iso_3166_1 = language == "it" ? "IT" : "US", data = movie ? new Dictionary<string, string> { ["title"] = name, ["overview"] = overview }
                : new Dictionary<string, string> { ["name"] = name, ["overview"] = overview } } } });

    [Fact]
    public async Task AllValidAnimeClickFieldsWinWithoutExternalOrAiRequests()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException("No fallback allowed"));
        var target = new Series { Name = "Il titolo italiano", Overview = "La trama pubblicata da AnimeClick.", Genres = ["Avventura"], Tags = ["Scuola"] };
        var config = Config(true); config.EnableGenres = config.EnableTags = true;
        await rig.Service.FillMissingAsync(target, "Episodio 1", null, TmdbIds, false, config, CancellationToken.None);
        Assert.Equal("Il titolo italiano", target.Name); Assert.Equal("La trama pubblicata da AnimeClick.", target.Overview);
        Assert.Equal(["Avventura"], target.Genres); Assert.Equal(["Scuola"], target.Tags); Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task ALongNativeAnimeClickOverviewIsPreservedInFull()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        var overview = string.Concat(Enumerable.Repeat("Questa è la trama originale italiana. ", 300)).Trim();
        var result = await rig.Service.ResolveOverviewAsync(overview, TmdbIds, false, null, Config(), CancellationToken.None);
        Assert.Equal(overview, result.Overview); Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task MissingOverviewIsFilledIndependentlyWithoutReplacingAnimeClickTitle()
    {
        using var rig = new Rig(_ => Json(Text("it", "Titolo di un altro provider", "La trama italiana alternativa.")));
        var target = new Series { Name = "Titolo AnimeClick", Overview = "Trama non disponibile" };
        await rig.Service.FillMissingAsync(target, null, null, TmdbIds, false, Config(), CancellationToken.None);
        Assert.Equal("Titolo AnimeClick", target.Name); Assert.Equal("La trama italiana alternativa.", target.Overview); Assert.Equal(1, rig.Calls);
    }

    [Fact]
    public async Task AnEnglishAnimeClickOverviewDoesNotBlockItalianMetadataFromAnotherSource()
    {
        using var rig = new Rig(_ => Json(Text("it", overview: "La trama italiana alternativa.")));
        const string english = "When the young friends leave their home, they discover that their journey will change everything. They must work together to find the truth and save the world before their enemies can stop them.";
        Assert.Equal(AnimeClickTextLanguage.English, AnimeClickMetadataLanguageDetector.Detect(english).Language);
        var result = await rig.Service.ResolveOverviewAsync(english, TmdbIds, false, null, Config(), CancellationToken.None);
        Assert.Equal("La trama italiana alternativa.", result.Overview); Assert.Equal("native-TMDB", result.Detail);
    }

    [Theory]
    [InlineData("N/D")]
    [InlineData("n/a")]
    [InlineData("Trama non disponibile.")]
    [InlineData("Nessuna sinossi disponibile")]
    [InlineData("Episode 12")]
    public async Task PlaceholdersNeverStopTheItalianFallback(string placeholder)
    {
        using var rig = new Rig(_ => Json(Text("it", overview: "Una trama pubblicata in italiano.")));
        var result = await rig.Service.ResolveOverviewAsync(placeholder, TmdbIds, false, null, Config(), CancellationToken.None);
        Assert.Equal("Una trama pubblicata in italiano.", result.Overview); Assert.Equal("native-TMDB", result.Detail);
        Assert.True(AnimeClickOverviewRepairPolicy.CanReplace(placeholder));
    }

    [Fact]
    public async Task MissingTitleUsesMovieTranslationFieldAndKeepsAnimeClickOverview()
    {
        using var rig = new Rig(request => { Assert.Equal("/3/movie/1/translations", request.RequestUri!.AbsolutePath); return Json(Text("it", "Un nuovo viaggio", movie: true)); });
        var target = new Movie { Overview = "Trama AnimeClick." };
        await rig.Service.FillMissingAsync(target, "N/D", null, TmdbIds, true, Config(), CancellationToken.None);
        Assert.Equal("Un nuovo viaggio", target.Name); Assert.Equal("Trama AnimeClick.", target.Overview); Assert.Equal(1, rig.Calls);
    }

    [Fact]
    public async Task ValidExistingNameDoesNotTriggerTitleTranslation()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        var target = new Series { Overview = "Trama AnimeClick." };
        await rig.Service.FillMissingAsync(target, "La mia correzione", null, TmdbIds, false, Config(true), CancellationToken.None);
        Assert.Null(target.Name); Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task EnglishIsNeverReportedAsItalianWithoutAiConsent()
    {
        using var rig = new Rig(_ => Json(Text("en", overview: "The original English synopsis.")));
        var result = await rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, Config(), CancellationToken.None);
        Assert.Null(result.Overview); Assert.Equal(AnimeClickRepairOutcome.Disabled, result.Outcome); Assert.Equal(1, rig.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task UnavailableOrContradictorySourcesDoNotProduceAnInventedValue(int returnedId, bool malformed)
    {
        using var rig = new Rig(_ => Json(malformed ? "not-json" : Text("it", overview: "", id: returnedId)));
        var result = await rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, Config(true), CancellationToken.None);
        Assert.Null(result.Overview); Assert.Equal(AnimeClickRepairOutcome.NoSource, result.Outcome);
    }

    [Fact]
    public async Task AnotherItalianProviderWinsBeforeEnglishOrAi()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath switch
        {
            "/v4/login" => Json("""{"data":{"token":"fake-token"}}"""),
            "/v4/series/2/translations/ita" => Json("{}", HttpStatusCode.NotFound),
            "/3/tv/1/translations" => Json(Text("it", overview: "La fonte italiana di riserva.")),
            _ => throw new InvalidOperationException("No English or AI request allowed")
        });
        var config = Config(true); config.EnableTvdbSynopsis = true; config.TvdbApiKey = "fake-tvdb";
        var result = await rig.Service.ResolveOverviewAsync(null, new Dictionary<string, string> { ["Tmdb"] = "1", ["Tvdb"] = "2" }, false, null, config, CancellationToken.None);
        Assert.Equal("native-TMDB", result.Detail); Assert.Equal("La fonte italiana di riserva.", result.Overview);
    }

    [Fact]
    public async Task FailedTvdbSourceLeavesTmdbAvailable()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "api.themoviedb.org"
            ? Json(Text("it", overview: "La fonte italiana di riserva.")) : Json("{}", HttpStatusCode.InternalServerError));
        var config = Config(); config.EnableTvdbSynopsis = true; config.TvdbApiKey = "fake-tvdb";
        var result = await rig.Service.ResolveOverviewAsync(null, new Dictionary<string, string> { ["Tmdb"] = "1", ["Tvdb"] = "2" }, false, null, config, CancellationToken.None);
        Assert.Equal("La fonte italiana di riserva.", result.Overview);
    }

    [Fact]
    public async Task EnglishOverviewIsTranslatedInBackgroundAndReusedFromCache()
    {
        var translated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = Config(true); config.EnableEpisodeSynopsisTranslation = false;
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Text("en", overview: "The English story of the travelling friends."));
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain("fake-tmdb", body); Assert.DoesNotContain("fake-ai", body);
            translated.TrySetResult(); return Json("""{"choices":[{"message":{"content":"La storia italiana degli amici in viaggio."}}]}""");
        }, config);
        var first = await rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, config, CancellationToken.None);
        Assert.Equal(AnimeClickRepairOutcome.WaitingTranslation, first.Outcome);
        await translated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AnimeClickOverviewResolution? result = null;
        for (var i = 0; i < 100; i++)
        {
            result = await rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, config, CancellationToken.None);
            if (result.Overview is not null) break;
            await Task.Delay(20);
        }
        Assert.Equal("La storia italiana degli amici in viaggio.", result!.Overview);
        Assert.Equal("translated-TMDB", result.Detail); Assert.Equal(2, rig.Calls);
    }

    [Fact]
    public async Task GenresUseItalianLabelsAndKeywordsUseASeparateValidatedTranslation()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/tv/1" => Json("""{"id":1,"genres":[{"name":"Avventura"},{"name":"N/D"}]}"""),
            "/3/tv/1/keywords" => Json("""{"id":1,"results":[{"name":"friendship"},{"name":"Tokyo"}]}"""),
            "/v1/chat/completions" => Json("""{"choices":[{"message":{"content":"[\"amicizia\",\"Tokyo\"]"}}]}"""),
            _ => throw new InvalidOperationException("Unexpected request")
        });
        var config = Config(true); config.EnableGenres = config.EnableTags = true;
        var target = new Series { Name = "Titolo AC", Overview = "Trama AC" };
        await rig.Service.FillMissingAsync(target, null, null, TmdbIds, false, config, CancellationToken.None);
        Assert.Equal(["Avventura"], target.Genres); Assert.Equal(["amicizia", "Tokyo"], target.Tags);
        Assert.Equal("Titolo AC", target.Name); Assert.Equal("Trama AC", target.Overview);
    }

    [Fact]
    public async Task EnglishTvdbGenresAreTranslatedWhenAnimeClickAndItalianGenresAreMissing()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath switch
        {
            "/v4/login" => Json("""{"data":{"token":"fake-token"}}"""),
            "/v4/series/2/extended" => Json("""{"data":{"id":2,"genres":[{"name":"Adventure"},{"name":"Animation"}]}}"""),
            "/v1/chat/completions" => Json("""{"choices":[{"message":{"content":"[\"Avventura\",\"Animazione\"]"}}]}"""),
            _ => throw new InvalidOperationException("Unexpected request")
        });
        var config = Config(true); config.EnableGenres = config.EnableTvdbSynopsis = true; config.TvdbApiKey = "fake-tvdb";
        var target = new Series { Name = "Titolo AC", Overview = "Trama AC" };
        await rig.Service.FillMissingAsync(target, null, null, new Dictionary<string, string> { ["Tvdb"] = "2" }, false, config, CancellationToken.None);
        Assert.Equal(["Avventura", "Animazione"], target.Genres);
    }

    [Fact]
    public async Task EnglishTitleUsesTitlePromptRatherThanSynopsisPrompt()
    {
        using var rig = new Rig(request =>
        {
            if (request.RequestUri!.Host == "api.themoviedb.org") return Json(Text("en", name: "The last voyage"));
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var payload = JsonDocument.Parse(body);
            var prompt = string.Join("\n", payload.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("content").GetString()));
            Assert.Contains("titolo di un'opera anime", prompt); Assert.DoesNotContain("sinossi seguente", prompt);
            return Json("""{"choices":[{"message":{"content":"L'ultimo viaggio"}}]}""");
        });
        var target = new Series { Overview = "Trama AnimeClick." };
        await rig.Service.FillMissingAsync(target, "N/D", null, TmdbIds, false, Config(true), CancellationToken.None);
        Assert.Equal("L'ultimo viaggio", target.Name); Assert.Equal("Trama AnimeClick.", target.Overview);
    }

    [Fact]
    public async Task MovieAndSeriesWithTheSameNumericIdHaveSeparateCaches()
    {
        using var rig = new Rig(request => request.RequestUri!.AbsolutePath == "/3/movie/1/translations"
            ? Json(Text("it", overview: "Trama del film.", movie: true)) : Json(Text("it", overview: "Trama della serie.")));
        var movie = await rig.Service.ResolveOverviewAsync(null, TmdbIds, true, null, Config(), CancellationToken.None);
        var series = await rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, Config(), CancellationToken.None);
        Assert.Equal("Trama del film.", movie.Overview); Assert.Equal("Trama della serie.", series.Overview); Assert.Equal(2, rig.Calls);
    }

    [Fact]
    public async Task DisabledFieldsDoNotStartFallbackRequests()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        var config = Config(true); config.EnablePlot = config.PreferItalianTitle = false;
        var target = new Movie();
        await rig.Service.FillMissingAsync(target, null, null, TmdbIds, true, config, CancellationToken.None);
        Assert.Equal(0, rig.Calls);
    }

    [Theory]
    [InlineData("N/D")]
    [InlineData("Unknown")]
    [InlineData("Episode 12")]
    public void GenericExistingNamesBecomeTitleRepairCandidates(string name)
        => Assert.True(AnimeClickRefreshMissingTitlesTask.NeedsTitle(new Episode { Name = name }));

    [Theory]
    [InlineData("amicizia")]
    [InlineData("[\"amicizia\"]")]
    [InlineData("[\"amicizia\",null]")]
    [InlineData("[\"amicizia\",\"N/D\"]")]
    public void MalformedOrIncompleteLabelTranslationsAreRejected(string output)
        => Assert.Empty(AnimeClickAnimeTextFallback.ParseLabels(output, 2));

    [Fact]
    public async Task EpisodeOverviewStillUsesKnownIdentitiesWhenAnimeClickIsUnavailable()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "www.animeclick.it"
            ? Json("{}", HttpStatusCode.ServiceUnavailable) : Json(Text("it", overview: "Una trama per questa puntata.", id: 88)));
        var result = await rig.Episodes.ResolveEpisodeOverviewDetailedAsync("72/naruto", 1, 1, null,
            Config(), CancellationToken.None, false, seriesIds: TmdbIds,
            episodeIds: new Dictionary<string, string> { ["Tmdb"] = "88" });
        Assert.Equal("native-tmdb", result.Outcome); Assert.Equal("Una trama per questa puntata.", result.Result!.Value);
    }

    [Fact]
    public async Task EpisodeOverviewRefusesADifferentEpisodeAndDoesNotRemapAnExistingCoordinate()
    {
        using var rig = new Rig(request => { Assert.EndsWith("/episode/1/translations", request.RequestUri!.AbsolutePath); return Json(Text("it", overview: "La trama del vicino.", id: 99)); });
        var result = await rig.Episodes.ResolveEpisodeOverviewDetailedAsync("", 1, 1, null,
            Config(), CancellationToken.None, false, seriesIds: TmdbIds,
            episodeIds: new Dictionary<string, string> { ["Tmdb"] = "88" });
        Assert.Null(result.Result); Assert.Equal(1, rig.Calls);
    }

    [Fact]
    public async Task CancellationDoesNotStartExternalWork()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ResolveOverviewAsync(null, TmdbIds, false, null, Config(), cancelled.Token));
        Assert.Equal(0, rig.Calls);
    }

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryAnimeClickCache _cache = new();
        private readonly Handler _handler;
        private readonly AnimeClickTranslationQueue _queue;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public AnimeClickAnimeTextFallback Service { get; }
        public AnimeClickMetadataFallbackService Episodes { get; }
        public Rig(Func<HttpRequestMessage, HttpResponseMessage> response, PluginConfiguration? current = null)
        {
            _handler = new Handler(request => { Interlocked.Increment(ref _calls); return response(request); });
            var factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));
            var tmdb = new AnimeClickTmdbClient(factory, _cache.Cache, NullLogger<AnimeClickTmdbClient>.Instance);
            var tvdb = new AnimeClickTvdbClient(factory, _cache.Cache, NullLogger<AnimeClickTvdbClient>.Instance);
            var translator = new AnimeClickAiTranslator(factory, _cache.Cache, NullLogger<AnimeClickAiTranslator>.Instance);
            var scheduler = new AnimeClickMetadataRefreshScheduler(TestDoubles.Proxy<ILibraryManager>(),
                TestDoubles.Proxy<IProviderManager>(), TestDoubles.Proxy<IFileSystem>(),
                new AnimeClickMetadataRefreshIntentRegistry(), NullLogger<AnimeClickMetadataRefreshScheduler>.Instance);
            _queue = new(translator, _cache.Cache, scheduler, NullLogger<AnimeClickTranslationQueue>.Instance, () => current ?? Config(true));
            Service = new(tmdb, tvdb, translator, _queue);
            Episodes = new(new AnimeClickClient(factory, NullLogger<AnimeClickClient>.Instance), _cache.Cache,
                new AnimeClickHtmlParser(), tmdb, tvdb, translator, _queue, NullLogger<AnimeClickMetadataFallbackService>.Instance);
        }
        public void Dispose() { _queue.Dispose(); _handler.Dispose(); _cache.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
