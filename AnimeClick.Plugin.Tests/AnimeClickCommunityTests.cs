using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickCommunityTests
{
    private const string Relay = "https://animeclick-community.example.workers.dev/v1/proposals";

    private static readonly JsonDocument Fixtures = JsonDocument.Parse(File.ReadAllText(FindFixture()));

    private static string FindFixture()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "community", "fixtures", "proposals.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("community/fixtures/proposals.json not found above the test output folder.");
    }

    private static IEnumerable<JsonElement> Cases(string kind) => Fixtures.RootElement.GetProperty(kind).EnumerateArray();

    private static AnimeClickCommunityDataset Dataset(string mappingsJson, string? relay = Relay)
        => AnimeClickCommunityData.ParseDataset("{\"schemaVersion\":2," + (relay is null ? "" : "\"relay\":\"" + relay + "\",")
            + "\"mappings\":[" + mappingsJson + "]}");

    private const string SaikiSeason = """{"kind":"Season","animeClickId":"26035","series":{"Tmdb":"67676","Tvdb":"313435"},"seasonNumber":3,"episodeCount":2}""";

    /* ===== Shared contract ===== */

    [Fact]
    public void AcceptsEveryValidFixtureAndHashesItLikeTheOtherImplementations()
    {
        foreach (var example in Cases("valid"))
        {
            var dataset = Dataset(example.GetProperty("mapping").GetRawText());
            var mapping = Assert.Single(dataset.Mappings);
            Assert.Equal(example.GetProperty("canonical").GetString(), AnimeClickCommunityData.Canonical(mapping));
            Assert.Equal(example.GetProperty("fingerprint").GetString(), AnimeClickCommunityData.Fingerprint(mapping));
        }
    }

    [Fact]
    public void RejectsEveryInvalidFixture()
    {
        foreach (var example in Cases("invalid"))
        {
            var name = example.GetProperty("name").GetString();
            var error = Record.Exception(() => Dataset(example.GetProperty("mapping").GetRawText()));
            Assert.True(error is JsonException, $"{name} must be rejected, got {error?.GetType().Name ?? "no error"}.");
        }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"mappings\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"mappings\":[],\"comment\":\"x\"}")]
    [InlineData("{\"schemaVersion\":2,\"relay\":\"http://example.org/v1\",\"mappings\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"relay\":\"https://user:pw@example.org/v1\",\"mappings\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"relay\":\"https://example.org/v1?x=1\",\"mappings\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"relay\":\"https://example.org:8443/v1\",\"mappings\":[]}")]
    public void RejectsOtherSchemasUnknownFieldsAndUnsafeRelays(string json)
        => Assert.Throws<JsonException>(() => AnimeClickCommunityData.ParseDataset(json));

    /* ===== Matching ===== */

    [Fact]
    public void ApprovedWorksRequireStableIdsAndRefuseConflictsAndWrongTypes()
    {
        var dataset = Dataset("""{"kind":"Series","animeClickId":"72","providerIds":{"Tmdb":"1","Tvdb":"2"}}""");
        Assert.Equal("72", AnimeClickCommunityData.Match(dataset, "Series", new Dictionary<string, string> { ["Tmdb"] = "1" }));
        Assert.Null(AnimeClickCommunityData.Match(dataset, "Movie", new Dictionary<string, string> { ["Tmdb"] = "1" }));
        Assert.Null(AnimeClickCommunityData.Match(dataset, "Series", new Dictionary<string, string> { ["Tmdb"] = "1", ["Tvdb"] = "3" }));
        dataset.Mappings.Add(new AnimeClickCommunityMapping { Kind = "Series", AnimeClickId = "99", ProviderIds = new() { ["Tmdb"] = "1" } });
        Assert.Null(AnimeClickCommunityData.Match(dataset, "Series", new Dictionary<string, string> { ["Tmdb"] = "1" }));
    }

    [Fact]
    public void ASeasonMatchesOnlyTheSameNumberAndTheSameNumberOfEpisodes()
    {
        var dataset = Dataset(SaikiSeason);
        var saiki = new Dictionary<string, string> { ["Tmdb"] = "67676", ["Tvdb"] = "313435", ["AniList"] = "21804" };
        Assert.Equal("26035", AnimeClickCommunityData.MatchSeason(dataset, saiki, 3, 2));
        Assert.Equal("26035", AnimeClickCommunityData.MatchSeason(dataset, new Dictionary<string, string> { ["tvdb"] = "313435" }, 3, 2));

        // A library that cut the series differently gets nothing rather than a card for other episodes.
        Assert.Null(AnimeClickCommunityData.MatchSeason(dataset, saiki, 3, 3));
        Assert.Null(AnimeClickCommunityData.MatchSeason(dataset, saiki, 2, 2));

        // A contradicting identity, or AniList alone, is not evidence for a season.
        Assert.Null(AnimeClickCommunityData.MatchSeason(dataset, new Dictionary<string, string> { ["Tmdb"] = "67676", ["Tvdb"] = "1" }, 3, 2));
        Assert.Null(AnimeClickCommunityData.MatchSeason(dataset, new Dictionary<string, string> { ["AniList"] = "21804" }, 3, 2));

        // Two approved cards for the same layout cancel each other out.
        dataset.Mappings.Add(new AnimeClickCommunityMapping
        {
            Kind = "Season", AnimeClickId = "1", Series = new() { ["Tvdb"] = "313435" }, SeasonNumber = 3, EpisodeCount = 2
        });
        Assert.Null(AnimeClickCommunityData.MatchSeason(dataset, saiki, 3, 2));
    }

    /* ===== Building proposals ===== */

    [Fact]
    public void DefaultsAskAndPayloadsExcludePrivateData()
    {
        var config = new PluginConfiguration();
        Assert.Equal(AnimeClickCommunitySharing.Ask, config.CommunitySharingMode);
        Assert.False(config.EnableCommunityMappings);
        Assert.Empty(config.CommunityGitHubToken);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Private title", Path = "/private/name.mkv", Overview = "Private overview" };
        movie.SetProviderId("AnimeClick", "72/naruto"); movie.SetProviderId("Tmdb", "00123");
        movie.SetProviderId("Imdb", "tt987"); movie.SetProviderId("Tvdb", "https://private.invalid");
        var mapping = AnimeClickCommunityService.BuildMapping(movie)!;
        Assert.Equal("72", mapping.AnimeClickId); Assert.Single(mapping.ProviderIds!); Assert.Equal("123", mapping.ProviderIds!["Tmdb"]);
        var json = AnimeClickCommunityData.Serialize(mapping);
        Assert.DoesNotContain("rivate", json); Assert.DoesNotContain(movie.Id.ToString(), json);
        Assert.DoesNotContain("series", json); Assert.DoesNotContain("seasonNumber", json);
        Assert.Null(AnimeClickCommunityService.BuildMapping(new Season()));
        Assert.Null(AnimeClickCommunityService.BuildMapping(new Movie()));
    }

    [Fact]
    public void ASeasonIsSharedWithTheSeriesIdsItsNumberAndItsEpisodeCount()
    {
        var season = SaikiSeasonItem();
        var seriesIds = new Dictionary<string, string> { ["Tmdb"] = "67676", ["Tvdb"] = "313435", ["AniList"] = "21804", ["Imdb"] = "tt1" };
        var mapping = AnimeClickCommunityService.BuildSeasonMapping(season, seriesIds, 2)!;
        Assert.Equal(SaikiSeason.Replace(" ", "", StringComparison.Ordinal), AnimeClickCommunityData.Serialize(mapping));
        Assert.Null(AnimeClickCommunityService.BuildSeasonMapping(season, new Dictionary<string, string> { ["AniList"] = "21804" }, 2));
        Assert.Null(AnimeClickCommunityService.BuildSeasonMapping(season, seriesIds, null));
        Assert.Null(AnimeClickCommunityService.BuildSeasonMapping(new Season { IndexNumber = 3 }, seriesIds, 2));
    }

    [Fact]
    public void EpisodesAreCountedByTheNumbersRealFilesHold()
    {
        Assert.Equal(4, AnimeClickCommunityService.CountEpisodes(
        [
            new Episode { IndexNumber = 1 },
            new Episode { IndexNumber = 2, IndexNumberEnd = 3 },
            new Episode { IndexNumber = 3 },
            new Episode { IndexNumber = 4 },
            new Episode { IndexNumber = 5, IsVirtualItem = true },
            new Episode { IndexNumber = null }
        ]));
        Assert.Null(AnimeClickCommunityService.CountEpisodes([new Episode { IndexNumber = null }]));
    }

    /* ===== Configuration ===== */

    [Theory]
    [InlineData(true, "Always")]
    [InlineData(false, "Ask")]
    public void SchemaThreeKeepsAutomaticSharingOnlyForWhoHadSwitchedItOn(bool sharing, string expected)
    {
        var config = new PluginConfiguration { ConfigurationVersion = 2, SetupCompletedVersion = 2, EnableCommunitySharing = sharing };
        Assert.True(config.ApplyMigrations());
        Assert.Equal(expected, config.CommunitySharingMode);
        Assert.Equal(3, config.ConfigurationVersion);
        Assert.Equal(2, config.SetupCompletedVersion);

        var current = new PluginConfiguration { ConfigurationVersion = 3, EnableCommunitySharing = true, CommunitySharingMode = "Never" };
        Assert.False(current.ApplyMigrations());
        Assert.Equal("Never", current.CommunitySharingMode);
    }

    [Theory]
    [InlineData("Never", "Never")]
    [InlineData("Always", "Always")]
    [InlineData("always", "Ask")]
    [InlineData("", "Ask")]
    [InlineData(null, "Ask")]
    public void AnUnknownSharingChoiceFallsBackToAsking(string? stored, string expected)
    {
        var config = new PluginConfiguration { CommunitySharingMode = stored! };
        config.Sanitize();
        Assert.Equal(expected, config.CommunitySharingMode);
    }

    /* ===== Offer and consent ===== */

    [Fact]
    public async Task AskingReturnsTheExactProposalAndSendsNothingUntilTheClick()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Ask" });
        var offer = await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        Assert.Equal("Ask", offer.Mode);
        Assert.False(offer.Queued);
        Assert.Equal("72", offer.Proposal!.AnimeClickId);
        Assert.Equal(AnimeClickCommunityData.Fingerprint(offer.Proposal), offer.Fingerprint);
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Empty(rig.Posts);

        Assert.Contains("Grazie", await rig.Service.ShareAsync(rig.Movie(), CancellationToken.None), StringComparison.Ordinal);
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Single(rig.Posts);
    }

    [Fact]
    public async Task NeverOffersNothingAndStopsWhatIsAlreadyQueued()
    {
        var config = new PluginConfiguration { CommunitySharingMode = "Always" };
        using var rig = new Rig(config);
        Assert.True((await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None)).Queued);
        config.CommunitySharingMode = "Never";
        var offer = await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        Assert.Null(offer.Proposal); Assert.False(offer.Queued);
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task TheRelaySendsOnlyPublicIdsAndRemembersTheIssue()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" });
        await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);

        var post = Assert.Single(rig.Posts);
        Assert.Equal(Relay, post.Url);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal(["schemaVersion", "installation", "pluginVersion", "mapping"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Matches("^[0-9a-f]{32}$", body.RootElement.GetProperty("installation").GetString());
        Assert.DoesNotContain("rivate", post.Body, StringComparison.Ordinal);
        Assert.Null(post.Authorization);

        var proposal = Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None));
        Assert.Equal("Sent", proposal.State);
        Assert.Equal(17, proposal.Issue);
        Assert.Equal("https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/17", proposal.Url);
        Assert.Equal("Abbinamento già condiviso.", await rig.Service.ShareAsync(rig.Movie(), CancellationToken.None));
    }

    [Fact]
    public async Task AQueuedProposalLearnsItsIssueFromTheRelayLater()
    {
        var published = false;
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" },
            relayResponse: () => new(HttpStatusCode.Accepted) { Content = new StringContent("{\"queued\":true,\"fingerprint\":\"x\"}") },
            relayState: fingerprint => published
                ? "{\"state\":\"published\",\"issue\":58,\"url\":\"https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/58\"}"
                : "{\"state\":\"pending\"}");
        await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);
        var queued = Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None));
        Assert.Equal(("Sent", (int?)null), (queued.State, queued.Issue));

        await rig.Service.ResolveQueuedIssuesAsync(CancellationToken.None);
        Assert.Null(Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).Issue);

        published = true;
        await rig.Service.ResolveQueuedIssuesAsync(CancellationToken.None);
        Assert.Null(Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).Issue); // once an hour at most
        await rig.Cache.Cache.SetAsync<DateTimeOffset?>("community::issues-checked:v1", DateTimeOffset.UtcNow.AddHours(-2), CancellationToken.None);
        await rig.Service.ResolveQueuedIssuesAsync(CancellationToken.None);
        var resolved = Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None));
        Assert.Equal((58, "https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/58"), (resolved.Issue, resolved.Url));
    }

    [Fact]
    public async Task ALinkOutsideTheRepositoryIsNeverKept()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" },
            relayResponse: () => new(HttpStatusCode.Created) { Content = new StringContent("{\"issue\":5,\"url\":\"https://evil.invalid/x\"}") });
        await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Equal("https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/5", Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).Url);
    }

    [Fact]
    public async Task WithoutARelayTheProposalWaitsAndThePageSaysWhy()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" }, relay: null);
        await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Empty(rig.Posts);
        Assert.Equal("Queued", Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).State);
        var status = JsonSerializer.SerializeToElement(await rig.Service.StatusAsync(CancellationToken.None));
        Assert.Equal("unavailable", status.GetProperty("Channel").GetString());
        Assert.Contains("non è ancora attivo", status.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedProposalIsDroppedAndABusyRelayIsRetriedLater()
    {
        using var rejected = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" },
            relayResponse: () => new(HttpStatusCode.BadRequest));
        await rejected.Service.OfferAsync(rejected.Movie(), CancellationToken.None);
        await rejected.Service.SendNextAsync(CancellationToken.None);
        await rejected.Service.SendNextAsync(CancellationToken.None);
        Assert.Single(rejected.Posts);
        Assert.Equal("Failed", Assert.Single(await rejected.Service.ProposalsAsync(CancellationToken.None)).State);

        using var busy = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" }, relayResponse: () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return response;
        });
        await busy.Service.OfferAsync(busy.Movie(), CancellationToken.None);
        await busy.Service.SendNextAsync(CancellationToken.None);
        await busy.Service.SendNextAsync(CancellationToken.None);
        Assert.Single(busy.Posts);
        Assert.Equal("Queued", Assert.Single(await busy.Service.ProposalsAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ATokenSendsDirectlyToGitHubWithTheSharedTitleAndBody()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always", CommunityGitHubToken = "fake-unit-test-token" });
        var mapping = AnimeClickCommunityService.BuildMapping(rig.Movie())!;
        Assert.True((await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None)).Queued);
        await rig.Service.SendNextAsync(CancellationToken.None);

        var post = Assert.Single(rig.Posts);
        Assert.Equal("https://api.github.com/repos/iCosiSenpai/jellyfin-plugin-animeclick/issues", post.Url);
        Assert.Equal("fake-unit-test-token", post.Authorization);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal(AnimeClickCommunityService.IssueTitle(mapping, AnimeClickCommunityData.Fingerprint(mapping)), body.RootElement.GetProperty("title").GetString());
        Assert.Contains(AnimeClickCommunityData.Canonical(mapping), body.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fake-unit-test-token", post.Body, StringComparison.Ordinal);
        Assert.Equal(23, Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).Issue);
    }

    [Fact]
    public async Task OffMeansNoNetworkAtAll()
    {
        using var rig = new Rig(new PluginConfiguration());
        Assert.Null(await rig.Service.ResolveAsync("Series", new Dictionary<string, string> { ["Tmdb"] = "1" }, new PluginConfiguration(), CancellationToken.None));
        Assert.Null(await rig.Service.ResolveLibrarySeasonAsync(new Dictionary<string, string> { ["Tmdb"] = "67676" }, 3, "/s3", new PluginConfiguration(), CancellationToken.None));
        await rig.Service.SendNextAsync(CancellationToken.None);
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task CorruptedLocalEntriesNeitherLeaveNorBlockAValidOne()
    {
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" });
        await rig.Cache.Cache.SetAsync("community::outbox:v1", new List<AnimeClickCommunitySubmission>
        {
            new() { Mapping = new() { Kind = "Movie", AnimeClickId = "invalid", ProviderIds = new() { ["Tmdb"] = "1" } } },
            new() { Mapping = new() { Kind = "Movie", AnimeClickId = "72", ProviderIds = new() { ["Tmdb"] = "1" } } }
        }, CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);
        var post = Assert.Single(rig.Posts);
        Assert.DoesNotContain("invalid", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsentProposalsSurviveARestartAndAnAdministrativeCacheClear()
    {
        using var cache = new TemporaryAnimeClickCache();
        var config = new PluginConfiguration { CommunitySharingMode = "Always" };
        var paths = TestDoubles.Proxy<IApplicationPaths>((method, _) => method.Name is "get_DataPath" or "get_CachePath" ? cache.RootPath : TestDoubles.DefaultReturn(method));
        using (var first = new Rig(config, cache: cache, paths: paths))
            await first.Service.OfferAsync(first.Movie(), CancellationToken.None);
        cache.Cache.ClearAll();
        using var restarted = new Rig(config, cache: cache, paths: paths);
        await restarted.Service.SendNextAsync(CancellationToken.None);
        Assert.Single(restarted.Posts);
    }

    /* ===== Review state and summary ===== */

    [Fact]
    public async Task ReviewStatesComeFromTheDatasetAndThePublicIssue()
    {
        var movie = AnimeClickCommunityData.Serialize(AnimeClickCommunityService.BuildMapping(Rig.MovieItem())!);
        using var rig = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" }, issueState: "{\"state\":\"closed\",\"state_reason\":\"not_planned\"}");
        await rig.Service.OfferAsync(rig.Movie(), CancellationToken.None);
        await rig.Service.SendNextAsync(CancellationToken.None);
        await rig.Service.RefreshProposalStatesAsync(CancellationToken.None);
        Assert.Equal("NotAccepted", Assert.Single(await rig.Service.ProposalsAsync(CancellationToken.None)).State);

        using var approved = new Rig(new PluginConfiguration { CommunitySharingMode = "Always" }, mappings: movie);
        await approved.Service.OfferAsync(approved.Movie(), CancellationToken.None);
        await approved.Service.SendNextAsync(CancellationToken.None);
        Assert.Equal("Approved", Assert.Single(await approved.Service.ProposalsAsync(CancellationToken.None)).State);
        var summary = await approved.Service.SummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary.Available); Assert.Equal(1, summary.Approved); Assert.True(summary.RelayAvailable);
    }

    /* ===== Seasons in the library ===== */

    [Fact]
    public async Task TheSeasonProviderUsesTheApprovedCardOnlyForTheSameLayout()
    {
        var config = new PluginConfiguration { EnableCommunityMappings = true };
        using var rig = new Rig(config, mappings: SaikiSeason, episodes: 2);
        var provider = new AnimeClickSeasonProvider(rig.SeasonResolver(), NullLogger<AnimeClickSeasonProvider>.Instance, rig.Factory, community: rig.Service);
        var result = await provider.GetMetadataAsync(SaikiSeasonInfo(), config, CancellationToken.None);
        Assert.Equal("26035", result.Item.GetProviderId("AnimeClick"));
        Assert.Equal(3, result.Item.IndexNumber);

        using var otherCut = new Rig(config, mappings: SaikiSeason, episodes: 3);
        var other = new AnimeClickSeasonProvider(otherCut.SeasonResolver(), NullLogger<AnimeClickSeasonProvider>.Instance, otherCut.Factory, community: otherCut.Service);
        Assert.NotEqual("26035", (await other.GetMetadataAsync(SaikiSeasonInfo(), config, CancellationToken.None)).Item.GetProviderId("AnimeClick"));

        var identified = SaikiSeasonInfo();
        identified.ProviderIds["AnimeClick"] = "500";
        Assert.NotEqual("26035", (await provider.GetMetadataAsync(identified, config, CancellationToken.None)).Item.GetProviderId("AnimeClick"));
    }

    [Fact]
    public async Task ASeasonCorrectionCanBeOfferedAndCountedInTheLibrary()
    {
        using var rig = new Rig(new PluginConfiguration { EnableCommunityMappings = true }, mappings: SaikiSeason, episodes: 2);
        var offer = await rig.Service.OfferAsync(rig.Season, CancellationToken.None);
        Assert.Equal(SaikiSeason.Replace(" ", "", StringComparison.Ordinal), AnimeClickCommunityData.Serialize(offer.Proposal));
        Assert.Equal(1, (await rig.Service.SummaryAsync(CancellationToken.None)).InLibrary);
    }

    private static Season SaikiSeasonItem()
    {
        var season = new Season { Id = Guid.NewGuid(), IndexNumber = 3, Name = "Stagione 3", Path = "/media/Saiki/Season 03" };
        season.SetProviderId("AnimeClick", "26035/saiki-kusuo-no-sainan-shinsaku");
        return season;
    }

    private static SeasonInfo SaikiSeasonInfo() => new()
    {
        Name = "Stagione 3", IndexNumber = 3, Path = "/media/Saiki/Season 03",
        SeriesProviderIds = new Dictionary<string, string> { ["AnimeClick"] = "16615/saiki-kusuo-no-psi-nan-tv", ["Tmdb"] = "67676", ["Tvdb"] = "313435" },
        ProviderIds = new Dictionary<string, string>()
    };

    private sealed record Post(string Url, string Body, string? Authorization);

    /// <summary>A community service against a fake GitHub, relay and library.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Handler _handler;
        private readonly bool _ownsCache;

        public Rig(PluginConfiguration config, string? relay = Relay, string mappings = "", Func<HttpResponseMessage>? relayResponse = null,
            string issueState = "{\"state\":\"open\"}", int episodes = 2, TemporaryAnimeClickCache? cache = null, IApplicationPaths? paths = null,
            Func<string, string>? relayState = null)
        {
            _ownsCache = cache is null;
            Cache = cache ?? new TemporaryAnimeClickCache();
            var dataset = "{\"schemaVersion\":2," + (relay is null ? "" : "\"relay\":\"" + relay + "\",") + "\"mappings\":[" + mappings + "]}";
            _handler = new Handler(request =>
            {
                Calls++;
                var url = request.RequestUri!.AbsoluteUri;
                if (request.Method == HttpMethod.Post)
                {
                    Posts.Add(new Post(url, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), request.Headers.Authorization?.Parameter));
                    if (url == Relay)
                        return relayResponse?.Invoke() ?? new(HttpStatusCode.Created)
                        {
                            Content = new StringContent("{\"issue\":17,\"url\":\"https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/17\",\"duplicate\":false}")
                        };
                    return new(HttpStatusCode.Created)
                    {
                        Content = new StringContent("{\"number\":23,\"html_url\":\"https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/23\"}")
                    };
                }

                if (url == AnimeClickCommunityService.DatasetUrl) return new(HttpStatusCode.OK) { Content = new StringContent(dataset) };
                if (url.StartsWith(Relay + "/", StringComparison.Ordinal))
                    return relayState is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent(relayState(url[(Relay.Length + 1)..])) };
                if (url.StartsWith("https://api.github.com/search/issues", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"total_count\":0,\"items\":[]}") };
                if (url.StartsWith("https://api.github.com/repos/", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent(issueState) };
                return new(HttpStatusCode.NotFound);
            });
            Factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(_handler, false));

            var series = new Series { Id = Guid.NewGuid(), Name = "Saiki" };
            series.SetProviderId("AnimeClick", "16615"); series.SetProviderId("Tmdb", "67676"); series.SetProviderId("Tvdb", "313435");
            Season = SaikiSeasonItem();
            Season.SeriesId = series.Id;
            var seasonEpisodes = Enumerable.Range(1, episodes).Select(number => (BaseItem)new Episode { Id = Guid.NewGuid(), IndexNumber = number }).ToList();
            var library = TestDoubles.Proxy<ILibraryManager>((method, args) => method.Name switch
            {
                nameof(ILibraryManager.FindByPath) => args![0] as string == Season.Path ? Season : null,
                nameof(ILibraryManager.GetItemById) => args![0] is Guid id && id == series.Id ? series : null,
                nameof(ILibraryManager.GetItemList) => ((InternalItemsQuery)args![0]!) switch
                {
                    { ParentId: var parent } when parent == Season.Id => seasonEpisodes,
                    { ParentId: var parent } when parent == series.Id => [Season],
                    _ => [series]
                },
                _ => TestDoubles.DefaultReturn(method)
            });
            Service = new AnimeClickCommunityService(Factory, Cache.Cache, () => config, paths, library);
        }

        public TemporaryAnimeClickCache Cache { get; }
        public IHttpClientFactory Factory { get; }
        public AnimeClickCommunityService Service { get; }
        public Season Season { get; }
        public List<Post> Posts { get; } = [];
        public int Calls { get; private set; }

        public static Movie MovieItem()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "My private title", Path = "/private/movie.mkv" };
            movie.SetProviderId("AnimeClick", "72/naruto"); movie.SetProviderId("Tmdb", "1");
            return movie;
        }

        public Movie Movie() => MovieItem();

        public AnimeClickSeasonResolver SeasonResolver()
            => new(new AnimeClickClient(Factory, NullLogger<AnimeClickClient>.Instance), Cache.Cache, new AnimeClickHtmlParser(), NullLogger<AnimeClickSeasonResolver>.Instance);

        public void Dispose()
        {
            Service.Dispose();
            _handler.Dispose();
            if (_ownsCache) Cache.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
