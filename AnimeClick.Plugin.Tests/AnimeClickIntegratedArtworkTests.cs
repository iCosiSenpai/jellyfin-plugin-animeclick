using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickIntegratedArtworkTests
{
    private static PluginConfiguration Config() => new() { FanartPersonalApiKey = "test-personal", TmdbApiKey = "test-tmdb" };
    private const string Fanart = """{"tmdb_id":"1","movieposter":[{"url":"https://assets.fanart.tv/fanart/movies/1/movieposter/poster.jpg","width":"1000","height":"1500","lang":"it"}],"hdmovielogo":[{"url":"https://assets.fanart.tv/fanart/movies/1/hdmovielogo/logo.png","width":"800","height":"310","lang":"00"}]}""";

    [Fact]
    public async Task FanartComesBeforeOriginalResolutionTmdbWithoutLeakingCredentialsInUrls()
    {
        using var rig = new Rig(request =>
        {
            Assert.DoesNotContain("test-personal", request.RequestUri!.ToString());
            if (request.RequestUri.Host == "webservice.fanart.tv")
            {
                Assert.Equal("test-personal", Assert.Single(request.Headers.GetValues("client-key")));
                Assert.False(request.Headers.Contains("api-key"));
                return Json(Fanart);
            }
            Assert.False(request.Headers.Contains("client-key"));
            return Json("""{"id":1,"posters":[{"file_path":"/tmdb.jpg","width":2000,"height":3000,"iso_639_1":"it"}],"backdrops":[{"file_path":"/backdrop.jpg","width":3840,"height":2160}]}""");
        });
        var movie = new Movie(); movie.SetProviderId("Tmdb", "1");
        var images = await rig.Artwork.GetImagesAsync(movie, Config(), CancellationToken.None);
        Assert.Equal(4, images.Count); Assert.Contains("assets.fanart.tv", images[0].Url);
        Assert.Equal("https://image.tmdb.org/t/p/original/tmdb.jpg", images[2].Url);
        Assert.Equal(ImageType.Backdrop, images[3].Type);
        Assert.All(images, v => { Assert.True(AnimeClickArtwork.IsTrustedUrl(v.Url)); Assert.DoesNotContain("key", v.Url); });
    }

    [Fact]
    public async Task ProjectAndPersonalKeysStayOnTheFanartHost()
    {
        using var rig = new Rig(request => { Assert.Equal("test-project", Assert.Single(request.Headers.GetValues("api-key"))); Assert.Equal("test-personal", Assert.Single(request.Headers.GetValues("client-key"))); return Json(Fanart); });
        var cfg = Config(); cfg.FanartProjectApiKey = "test-project";
        Assert.NotNull(await rig.Fanart.GetArtworkAsync(1, true, cfg, CancellationToken.None));
        Assert.Single(rig.Paths);
    }

    [Theory]
    [InlineData("https://evil.invalid/x.jpg")] [InlineData("https://assets.fanart.tv.evil.invalid/fanart/a.jpg")]
    [InlineData("https://assets.fanart.tv:444/fanart/a.jpg")] [InlineData("https://u:p@assets.fanart.tv/fanart/a.jpg")]
    [InlineData("https://assets.fanart.tv/fanart/a.jpg?secret=value")] [InlineData("https://assets.fanart.tv/fanart/a.jpg#fragment")]
    [InlineData("https://image.tmdb.org/t/p/original/../evil.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/a.jpg?api_key=secret")]
    [InlineData("http://image.tmdb.org/t/p/original/a.jpg")]
    public void UntrustedImagesAreRejected(string url) => Assert.False(AnimeClickArtwork.IsTrustedUrl(url));

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/original/a.jpg")]
    [InlineData("https://assets.fanart.tv/fanart/tv/1/tvposter/a.jpg")]
    public void TrustedArtworkUsesTheCommonImageProxy(string url)
    {
        Assert.True(AnimeClickHttp.TryResolve("https://www.animeclick.it", url, true, out var uri));
        Assert.Equal(url, uri.AbsoluteUri);
    }

    [Fact]
    public async Task ImageRedirectsCannotLeaveTheTrustedCdnsOrCarryApiKeys()
    {
        using var rig = new Rig(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/private") } });
        await Assert.ThrowsAsync<HttpRequestException>(() => AnimeClickHttp.GetImageAsync(rig.Factory, "https://image.tmdb.org/t/p/original/a.jpg", Config(), CancellationToken.None));
        Assert.Single(rig.Paths);
    }

    [Fact]
    public void OnlyTheRequestedSeasonArtworkAndAdequatePostersAreAdvertised()
    {
        using var document = JsonDocument.Parse("""{"seasonposter":[{"season":"1","url":"http://assets.fanart.tv/fanart/tv/1/seasonposter/a.jpg","width":"1000","height":"1500"},{"season":"2","url":"https://assets.fanart.tv/fanart/tv/1/seasonposter/b.jpg","width":"1000","height":"1500"},{"season":"1","url":"https://assets.fanart.tv/fanart/tv/1/seasonposter/small.jpg","width":"200","height":"300"}]}""");
        var image = Assert.Single(AnimeClickArtwork.ParseFanart(document.RootElement, false, 1, 400));
        Assert.Equal("https://assets.fanart.tv/fanart/tv/1/seasonposter/a.jpg", image.Url);
    }

    [Fact]
    public async Task IncorrectIdentityIsNotCachedAndServerFailuresRemainRetryable()
    {
        var calls = 0;
        using var rig = new Rig(_ => ++calls == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable)
            : calls == 2 ? Json("""{"tmdb_id":"99"}""") : Json(Fanart));
        Assert.Null(await rig.Fanart.GetArtworkAsync(1, true, Config(), CancellationToken.None));
        Assert.Null(await rig.Fanart.GetArtworkAsync(1, true, Config(), CancellationToken.None));
        Assert.NotNull(await rig.Fanart.GetArtworkAsync(1, true, Config(), CancellationToken.None));
        Assert.NotNull(await rig.Fanart.GetArtworkAsync(1, true, Config(), CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FanartFailureStillOffersTmdbArtwork()
    {
        using var rig = new Rig(request => request.RequestUri!.Host == "webservice.fanart.tv" ? Json("{}", HttpStatusCode.Unauthorized)
            : Json("""{"id":1,"posters":[{"file_path":"/tmdb.jpg","width":2000,"height":3000}]}"""));
        var movie = new Movie(); movie.SetProviderId("Tmdb", "1");
        var images = await rig.Artwork.GetImagesAsync(movie, Config(), CancellationToken.None);
        Assert.Contains("image.tmdb.org", Assert.Single(images).Url);
    }

    [Fact]
    public async Task DisabledArtworkAndMissingKeysDoNotMakeHttpRequests()
    {
        using var rig = new Rig(_ => throw new Exception("No HTTP allowed"));
        var movie = new Movie(); movie.SetProviderId("Tmdb", "1");
        var cfg = Config(); cfg.EnableFanartImages = cfg.EnableIntegratedImages = false;
        Assert.Empty(await rig.Artwork.GetImagesAsync(movie, cfg, CancellationToken.None));
        Assert.Null(await rig.Fanart.GetArtworkAsync(1, true, new PluginConfiguration(), CancellationToken.None));
        Assert.Empty(rig.Paths);
    }

    [Fact]
    public async Task FanartCacheProfilesDoNotReuseAnotherCredentialsAccessTier()
    {
        using var rig = new Rig(_ => Json(Fanart));
        await rig.Fanart.GetArtworkAsync(1, true, Config(), CancellationToken.None);
        var cfg = Config(); cfg.FanartPersonalApiKey = "second-personal";
        await rig.Fanart.GetArtworkAsync(1, true, cfg, CancellationToken.None);
        Assert.Equal(2, rig.Paths.Count);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    private sealed class Rig : IDisposable
    {
        private readonly TemporaryAnimeClickCache _cache = new(); private readonly Handler _handler;
        public List<Uri> Paths { get; } = [];
        public IHttpClientFactory Factory { get; }
        public AnimeClickFanartClient Fanart { get; }
        public AnimeClickArtwork Artwork { get; }
        public Rig(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _handler = new(request => { Paths.Add(request.RequestUri!); return response(request); });
            Factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));
            Fanart = new(Factory, _cache.Cache, NullLogger<AnimeClickFanartClient>.Instance);
            Artwork = new(new AnimeClickTmdbClient(Factory, _cache.Cache, NullLogger<AnimeClickTmdbClient>.Instance), Fanart);
        }
        public void Dispose() { _handler.Dispose(); _cache.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}
