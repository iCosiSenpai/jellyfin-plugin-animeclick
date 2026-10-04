using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickAniListTests
{
    private const string Cover = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx101921-ufrjLzhSz7L1.jpg";
    private const string Banner = "https://s4.anilist.co/file/anilistcdn/media/anime/banner/101921-qDWqfMTRCaUS.jpg";
    private const string Aoi = "https://s4.anilist.co/file/anilistcdn/staff/large/n101686-zJ3U3hYvzAfJ.png";

    private const string KaguyaJson = """
        {"data":{"Media":{"id":101921,"format":"TV","episodes":12,"status":"FINISHED","seasonYear":2019,"averageScore":83,
          "bannerImage":"https://s4.anilist.co/file/anilistcdn/media/anime/banner/101921-qDWqfMTRCaUS.jpg",
          "startDate":{"year":2019,"month":1,"day":12},"endDate":{"year":2019,"month":3,"day":30},
          "title":{"romaji":"Kaguya-sama wa Kokurasetai: Tensaitachi no Renai Zunousen"},
          "coverImage":{"extraLarge":"https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx101921-ufrjLzhSz7L1.jpg"},
          "trailer":{"site":"youtube","id":"IwpJJiQkZzI"},
          "studios":{"nodes":[{"name":"A-1 Pictures"}]},
          "characters":{"edges":[{"node":{"name":{"full":"Kaguya Shinomiya"}},"voiceActors":[{"name":{"full":"Aoi Koga"},"image":{"large":"https://s4.anilist.co/file/anilistcdn/staff/large/n101686-zJ3U3hYvzAfJ.png"}}]},
                                  {"node":{"name":{"full":"Miyuki Shirogane"}},"voiceActors":[{"name":{"full":"Makoto Furukawa"},"image":{"large":"https://evil.invalid/x.png"}}]}]},
          "staff":{"edges":[{"role":"Director","node":{"name":{"full":"Mamoru Hatakeyama"},"image":{"large":null}}},
                             {"role":"Original Creator","node":{"name":{"full":"Aka Akasaka"},"image":{"large":null}}},
                             {"role":"Key Animation (ep 3)","node":{"name":{"full":"Someone"},"image":{"large":null}}}]},
          "relations":{"edges":[{"relationType":"ADAPTATION","node":{"id":1,"type":"MANGA","format":"MANGA","seasonYear":null,"episodes":null}},
                                 {"relationType":"SEQUEL","node":{"id":112641,"type":"ANIME","format":"TV","seasonYear":2020,"episodes":12}}]}}}}
        """;

    private static AnimeClickAniListMedia Kaguya() => AnimeClickAniListMedia.Parse(JsonDocument.Parse(KaguyaJson).RootElement.GetProperty("data").GetProperty("Media"))!;

    private static AnimeClickAniListMedia Media(int id, string format, int? year, int? episodes, params (string Type, int Id, string Format)[] relations) => new()
    {
        Id = id, Format = format, SeasonYear = year, Episodes = episodes,
        Relations = relations.Select(r => new AnimeClickAniListRelation { Type = r.Type, Id = r.Id, Format = r.Format }).ToList()
    };

    private static PluginConfiguration Config() => new() { EnableAniListMetadata = true };

    [Fact]
    public void TheResponseIsReadIntoTheFieldsThePluginUses()
    {
        var media = Kaguya();
        Assert.Equal((101921, "TV", 12, 2019, 83), (media.Id, media.Format, media.Episodes, media.Year, media.AverageScore));
        Assert.Equal(new DateTime(2019, 1, 12, 0, 0, 0, DateTimeKind.Utc), media.StartDate);
        Assert.Equal(["A-1 Pictures"], media.Studios);
        Assert.Equal("IwpJJiQkZzI", media.TrailerYouTubeId);
        Assert.Equal(("Aoi Koga", "Kaguya Shinomiya"), (media.Cast[0].Name, media.Cast[0].Role));
        var sequel = Assert.Single(media.Relations, relation => relation.Type == "SEQUEL");
        Assert.Equal((112641, "TV"), (sequel.Id, sequel.Format));
        Assert.DoesNotContain(media.Relations, relation => relation.Format == "MANGA");
    }

    [Theory]
    [InlineData("MOVIE", 2016, true, 2016, true)]
    [InlineData("SPECIAL", 2016, true, 2016, false)]  // Your Name. pointed at a Suntory commercial
    [InlineData("TV", 2016, true, 2016, false)]       // KonoSuba's film pointed at the TV series
    [InlineData("TV", 2018, false, 2008, false)]      // Index pointed at its third season
    [InlineData("TV", 2019, false, 2018, true)]
    [InlineData("ONA", 2025, false, null, true)]
    [InlineData("MOVIE", 2019, false, 2019, false)]
    public void AnIdIsTrustedOnlyWhenTypeAndYearAgree(string format, int year, bool movie, int? knownYear, bool expected)
        => Assert.Equal(expected, AnimeClickAniListMetadata.Agrees(Media(1, format, year, 12), movie, knownYear));

    [Fact]
    public void OnlyEmptyFieldsAreFilledAndAnimeClickKeepsItsValues()
    {
        var series = new Series { Name = "Kaguya-sama", ProductionYear = 2019, Studios = ["Studio AnimeClick"] };
        var result = new MetadataResult<Series>
        {
            Item = series,
            People = [new PersonInfo { Name = "Regista AnimeClick", Type = PersonKind.Director }]
        };
        AnimeClickAniListMetadata.Apply(result, Kaguya(), Config());

        Assert.Equal(["Studio AnimeClick"], series.Studios);
        Assert.Equal(2019, series.ProductionYear);
        Assert.Equal(8.3f, series.CommunityRating);
        Assert.Equal(new DateTime(2019, 1, 12, 0, 0, 0, DateTimeKind.Utc), series.PremiereDate);
        Assert.Equal(SeriesStatus.Ended, series.Status);
        Assert.Equal(new DateTime(2019, 3, 30, 0, 0, 0, DateTimeKind.Utc), series.EndDate);
        Assert.Equal("Kaguya-sama wa Kokurasetai: Tensaitachi no Renai Zunousen", series.OriginalTitle);
        Assert.Equal("https://www.youtube.com/watch?v=IwpJJiQkZzI", Assert.Single(series.RemoteTrailers).Url);

        // The director came from AnimeClick, so AniList adds actors and writers but no second director.
        Assert.Single(result.People, person => person.Type == PersonKind.Director);
        var kaguya = Assert.Single(result.People, person => person.Role == "Kaguya Shinomiya");
        Assert.Equal(("Aoi Koga", PersonKind.Actor, Aoi), (kaguya.Name, kaguya.Type, kaguya.ImageUrl));
        Assert.Null(Assert.Single(result.People, person => person.Name == "Makoto Furukawa").ImageUrl);
        Assert.Contains(result.People, person => person.Name == "Aka Akasaka" && person.Type == PersonKind.Writer);
        Assert.DoesNotContain(result.People, person => person.Name == "Someone");
    }

    [Fact]
    public void SwitchedOffPreferencesAreRespected()
    {
        var config = Config();
        config.EnableCast = config.EnableStudios = config.EnableCommunityRating = config.EnableTrailers = false;
        var result = new MetadataResult<Movie> { Item = new Movie() };
        AnimeClickAniListMetadata.Apply(result, Kaguya(), config);
        Assert.Empty(result.Item.Studios);
        Assert.Null(result.Item.CommunityRating);
        Assert.Empty(result.Item.RemoteTrailers);
        Assert.Null(result.People);
    }

    [Fact]
    public async Task TheSequelChainGivesTheYearOnlyWhenTheEpisodesAgree()
    {
        var entries = new Dictionary<int, AnimeClickAniListMedia>
        {
            [21804] = Media(21804, "TV", 2016, 120, ("SEQUEL", 98034, "TV"), ("ALTERNATIVE", 1, "ONA")),
            [98034] = Media(98034, "TV", 2018, 24, ("PREQUEL", 21804, "TV"), ("SEQUEL", 3, "SPECIAL"), ("SEQUEL", 4, "ONA")),
            [4] = Media(4, "ONA", 2019, 6)
        };
        Task<AnimeClickAniListMedia?> Load(int id) => Task.FromResult(entries.GetValueOrDefault(id));

        Assert.Equal(2018, await AnimeClickAniListMetadata.SeasonYearAsync(21804, 2, 24, Load));
        Assert.Null(await AnimeClickAniListMetadata.SeasonYearAsync(21804, 2, 23, Load));      // a different cut
        Assert.Null(await AnimeClickAniListMetadata.SeasonYearAsync(21804, 3, 2, Load));       // Saiki S3: the special is skipped, the ONA has 6
        Assert.Null(await AnimeClickAniListMetadata.SeasonYearAsync(98034, 2, 6, Load));       // the ID names the second season, not the first

        entries[21804] = Media(21804, "TV", 2016, 120, ("SEQUEL", 98034, "TV"), ("SEQUEL", 7, "TV"));
        Assert.Null(await AnimeClickAniListMetadata.SeasonYearAsync(21804, 2, 24, Load));      // two TV sequels: ambiguous
    }

    [Fact]
    public async Task AKnownYearOrASwitchedOffSourceIsLeftAlone()
    {
        using var rig = new Rig(_ => throw new InvalidOperationException("no request expected"));
        var known = new Dictionary<int, int> { [2] = 2020 };
        var ids = new Dictionary<string, string> { ["AniList"] = "21804" };
        Assert.Same(known, await rig.Metadata.WithSeasonYearAsync(known, ids, 2, 24, Config(), CancellationToken.None));
        Assert.Null(await rig.Metadata.WithSeasonYearAsync(null, ids, 2, 24, new PluginConfiguration(), CancellationToken.None));
        Assert.Null(await rig.Metadata.WithSeasonYearAsync(null, ids, 2, null, Config(), CancellationToken.None));
    }

    [Theory]
    [InlineData(Cover, true)]
    [InlineData(Banner, true)]
    [InlineData(Aoi, true)]
    [InlineData("http://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx1-a.jpg", false)]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx1-a.jpg?x=1", false)]
    [InlineData("https://s4.anilist.co/file/anilistcdn/user/avatar/large/b1-a.png", false)]
    [InlineData("https://s4.anilist.co.evil.invalid/file/anilistcdn/media/anime/banner/1-a.jpg", false)]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/banner/../../x.jpg", false)]
    public void OnlyAniListMediaAndStaffImagesAreTrusted(string url, bool trusted)
    {
        Assert.Equal(trusted, AnimeClickArtwork.IsAniListImage(url));
        Assert.Equal(trusted, AnimeClickArtwork.IsTrustedUrl(url));
    }

    [Fact]
    public async Task CoverAndBannerAreOfferedForAMatchingWork()
    {
        using var rig = new Rig(_ => Json(KaguyaJson));
        var series = new Series { ProductionYear = 2019 };
        series.SetProviderId("AniList", "101921");
        var images = await rig.Metadata.GetImagesAsync(series, Config(), CancellationToken.None);
        Assert.Equal([(ImageType.Primary, Cover), (ImageType.Banner, Banner)], images.Select(image => (image.Type, image.Url)));

        var film = new Movie();
        film.SetProviderId("AniList", "101921");
        Assert.Empty(await rig.Metadata.GetImagesAsync(film, Config(), CancellationToken.None));
    }

    [Fact]
    public async Task TheRequestNamesThePluginIsCachedAndAMissingEntryIsRemembered()
    {
        var requests = new List<HttpRequestMessage>();
        using var rig = new Rig(request =>
        {
            requests.Add(request);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("\"id\":404", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"data\":{\"Media\":null}}") }
                : Json(KaguyaJson);
        });
        var config = Config();
        Assert.Equal(101921, (await rig.Resolver.GetMediaAsync(101921, config, CancellationToken.None))!.Id);
        Assert.Equal(101921, (await rig.Resolver.GetMediaAsync(101921, config, CancellationToken.None))!.Id);
        Assert.Null(await rig.Resolver.GetMediaAsync(404, config, CancellationToken.None));
        Assert.Null(await rig.Resolver.GetMediaAsync(404, config, CancellationToken.None));
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("AnimeClick-Jellyfin-Plugin/", requests[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Equal("https://graphql.anilist.co/", requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task CompletingAWorkUsesOnlyAnIdThatAgrees()
    {
        using var rig = new Rig(_ => Json(KaguyaJson));
        var ids = new Dictionary<string, string> { ["AniList"] = "101921" };
        var series = new MetadataResult<Series> { Item = new Series { ProductionYear = 2019 } };
        Assert.True(await rig.Metadata.CompleteAsync(series, ids, movie: false, Config(), CancellationToken.None));
        Assert.Equal(["A-1 Pictures"], series.Item.Studios);

        var wrongYear = new MetadataResult<Series> { Item = new Series { ProductionYear = 2008 } };
        Assert.False(await rig.Metadata.CompleteAsync(wrongYear, ids, movie: false, Config(), CancellationToken.None));
        Assert.Empty(wrongYear.Item.Studios);

        Assert.False(await rig.Metadata.CompleteAsync(series, ids, movie: false, new PluginConfiguration(), CancellationToken.None));
        Assert.False(new PluginConfiguration().EnableAniListMetadata);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryAnimeClickCache _cache = new();
        private readonly Handler _handler;

        public Rig(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _handler = new Handler(respond);
            var factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));
            Resolver = new AnimeClickAniListResolver(factory, _cache.Cache, NullLogger<AnimeClickAniListResolver>.Instance);
            Metadata = new AnimeClickAniListMetadata(Resolver);
        }

        public AnimeClickAniListResolver Resolver { get; }
        public AnimeClickAniListMetadata Metadata { get; }

        public void Dispose()
        {
            _handler.Dispose();
            _cache.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
