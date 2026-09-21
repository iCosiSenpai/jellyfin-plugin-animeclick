using System.Net;
using AnimeClick.Plugin.Api;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnimeClick.Plugin.Tests;

public class AnimeClickMajorReleaseTests
{
    private static RemoteSearchResult Candidate(string name, string id, int? year = null)
        => new() { Name = name, ProductionYear = year, ProviderIds = new() { ["AnimeClick"] = id } };

    [Fact]
    public void AutomaticIdentificationRejectsUnrelatedResultsEvenWithMatchingYear()
        => Assert.Null(AnimeClickAutomaticIdentification.Select(
            [Candidate("Gintama Finale", "1/gintama", 2021)], "Saekano the Movie Finale", 2021));

    [Fact]
    public void AutomaticIdentificationRejectsAmbiguousRemakesWithoutYear()
        => Assert.Null(AnimeClickAutomaticIdentification.Select(
            [Candidate("Dororo", "1/dororo", 1969), Candidate("Dororo", "2/dororo", 2019)], "Dororo", null));

    [Fact]
    public void AutomaticIdentificationUsesYearToDisambiguate()
    {
        var remake = Candidate("Dororo", "2/dororo", 2019);
        Assert.Same(remake, AnimeClickAutomaticIdentification.Select(
            [Candidate("Dororo", "1/dororo", 1969), remake], "Dororo", 2019));
    }

    [Theory]
    [InlineData("86", "1986")]
    [InlineData("Naruto Shippuden", "Naruto")]
    [InlineData("Steins;Gate 0", "Steins;Gate")]
    [InlineData("Mushoku Tensei Season 3", "Mushoku Tensei")]
    [InlineData("Mushoku Tensei", "Mushoku Tensei III")]
    [InlineData("The Ancient Story of a Lonely Traveller", "The Ancient Story of a Lonely Traveller 2")]
    [InlineData("The Ancient Story of a Lonely Traveller 2", "The Ancient Story of a Lonely Traveller")]
    public void AutomaticIdentificationDoesNotConfuseRelatedNames(string query, string result)
        => Assert.Null(AnimeClickAutomaticIdentification.Select([Candidate(result, "1/x")], query, null));

    [Theory]
    [InlineData("86", "86")]
    [InlineData("Caffè", "Caffe")]
    [InlineData("Mushoku Tensei Season 3", "Mushoku Tensei III")]
    [InlineData("Paprika.2006.4K.HDR.DV.2160p.BDRip", "Paprika")]
    public void AutomaticIdentificationAcceptsSupportedTitleForms(string query, string result)
        => Assert.NotNull(AnimeClickAutomaticIdentification.Select([Candidate(result, "1/x")], query, null));

    [Fact]
    public void AutomaticIdentificationCanUseThePublishedSlugAsAlias()
        => Assert.NotNull(AnimeClickAutomaticIdentification.Select(
            [Candidate("Saenai Heroine no Sodatekata Fine", "22906/saekano-movie")], "Saekano Movie", null));

    [Theory]
    [InlineData("ONA")]
    [InlineData("OVA")]
    [InlineData("OAV")]
    [InlineData("Serie TV")]
    public void EpisodeBasedReleasesCanBeSeries(string format)
        => Assert.True(AnimeClickSearchScorer.IsFormatCompatible(new() { Format = format }, true));

    [Theory]
    [InlineData("https://user:password@www.animeclick.it")]
    [InlineData("https://www.animeclick.it?token=secret")]
    [InlineData("https://www.animeclick.it#fragment")]
    public void BaseUrlDoesNotCarryCredentialsOrQuery(string value)
        => Assert.Equal(ConfigurationLimits.DefaultBaseUrl, ConfigurationLimits.NormalizeBaseUrl(value));

    [Fact]
    public async Task RedirectToAnUntrustedImageHostIsRejectedBeforeSecondRequest()
    {
        var handler = new ResponseHandler(_ => new(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data/") } });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => AnimeClickHttp.GetAsync(client,
            "https://www.animeclick.it/image.png", new(), true, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OfficialRedirectRemainsUsable()
    {
        var handler = new ResponseHandler(uri => uri.AbsolutePath == "/old"
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/new", UriKind.Relative) } }
            : new(HttpStatusCode.OK) { Content = new StringContent("image") });
        using var client = new HttpClient(handler);
        using var response = await AnimeClickHttp.GetAsync(client,
            "https://www.animeclick.it/old", new(), true, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task RateLimitCanBeExtendedWhileAnotherCallerWaits()
    {
        var clock = new FakeTimeProvider();
        var throttle = new RequestThrottle("test", TimeSpan.FromSeconds(1), clock);
        await throttle.WaitAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = throttle.WaitAsync(cancel.Token);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
        // The previous implementation blocked synchronously here until the wait finished.
        var notice = Task.Run(() => throttle.NoticeRateLimit(response));
        try
        {
            Assert.Equal(TimeSpan.FromMinutes(5), await notice.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(waiting.IsCompleted);
        }
        finally { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task CacheDoesNotConfuseSanitizedOrLiteralHashKeys()
    {
        using var temporary = new TemporaryAnimeClickCache();
        await temporary.Cache.SetAsync("anime:123/title", "slash", CancellationToken.None);
        await temporary.Cache.SetAsync("anime:123_title", "underscore", CancellationToken.None);
        Assert.Equal("slash", await temporary.Cache.GetAsync<string>("anime:123/title", CancellationToken.None));
        Assert.Equal("underscore", await temporary.Cache.GetAsync<string>("anime:123_title", CancellationToken.None));
        var hashed = Directory.GetFiles(Path.Combine(temporary.RootPath, "AnimeClickMetadata"))
            .Select(Path.GetFileNameWithoutExtension).Single(name => name!.Contains('~'))!;
        await temporary.Cache.SetAsync(hashed, "literal", CancellationToken.None);
        Assert.Equal("slash", await temporary.Cache.GetAsync<string>("anime:123/title", CancellationToken.None));
        Assert.Equal("literal", await temporary.Cache.GetAsync<string>(hashed, CancellationToken.None));
        Assert.Equal(3, temporary.Cache.ClearByPrefix("anime:"));
    }

    [Fact]
    public async Task EnrichingCachedMetadataDoesNotRenewItsExpiration()
    {
        using var temporary = new TemporaryAnimeClickCache();
        await temporary.Cache.SetAsync("anime:test", "initial", CancellationToken.None);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(temporary.RootPath, "AnimeClickMetadata")));
        var timestamp = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(path, timestamp);
        await temporary.Cache.SetAsync("anime:test", "enriched", CancellationToken.None, preserveAge: true);
        Assert.Equal("enriched", await temporary.Cache.GetAsync<string>("anime:test", 48, CancellationToken.None));
        Assert.Null(await temporary.Cache.GetAsync<string>("anime:test", 1, CancellationToken.None));
    }

    [Fact]
    public void RepairVerdictIsReconsideredWhenSourcesChange()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var configuration = new PluginConfiguration();
        using var ledger = new AnimeClickRepairLedger(temporary.Cache,
            NullLogger<AnimeClickRepairLedger>.Instance, () => configuration);
        var id = Guid.NewGuid();
        ledger.Record(id, AnimeClickRepairOutcome.NoSource, "none");
        Assert.True(ledger.IsSuppressed(id, DateTimeOffset.UtcNow, out _));
        configuration.CacheHours++;
        Assert.True(ledger.IsSuppressed(id, DateTimeOffset.UtcNow, out _));
        configuration.TmdbApiKey = "new-test-key";
        Assert.False(ledger.IsSuppressed(id, DateTimeOffset.UtcNow, out var attempt));
        Assert.DoesNotContain(configuration.TmdbApiKey, attempt.SourceFingerprint);
        ledger.Record(id, AnimeClickRepairOutcome.NoSource, "none");
        Assert.True(ledger.IsSuppressed(id, DateTimeOffset.UtcNow, out _));
        configuration.EnableTvdbSynopsis = true;
        Assert.False(ledger.IsSuppressed(id, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void FreshConfigurationDoesNotInventAnAiProfileAndMigrationIsIdempotent()
    {
        var configuration = new PluginConfiguration();
        Assert.True(configuration.ApplyMigrations());
        Assert.Equal(string.Empty, configuration.AiEndpoint);
        Assert.Equal(string.Empty, configuration.AiModel);
        Assert.False(AnimeClickAiTranslator.IsConfigured(configuration, out _));
        Assert.False(configuration.ApplyMigrations());
    }

    [Fact]
    public void AiCanBeDisabledWithoutLosingTheSavedProfile()
    {
        var configuration = new PluginConfiguration
        { AiEndpoint = "http://localhost:11434/api/chat", AiModel = "example-model" };
        Assert.True(AnimeClickAiTranslator.IsConfigured(configuration, out _));
        configuration.EnableAiTranslation = false;
        Assert.False(AnimeClickAiTranslator.IsConfigured(configuration, out _));
        Assert.Equal("example-model", configuration.AiModel);
    }

    [Fact]
    public void TranslationQueueAndTranslatorUseTheSameTruncatedTextAndTrimmedProfile()
    {
        var text = new string('a', 7999) + "😀" + new string('b', 50);
        var plain = AnimeClickAiTranslator.NormalizeSourceText(text);
        Assert.Equal(7999, plain.Length);
        var configuration = new PluginConfiguration
        { AiEndpoint = "http://localhost:11434/api/chat", AiModel = " model ", AiApiKey = " key " };
        Assert.True(AnimeClickTranslationQueue.TryBuildWorkKey(text, "scope", "identity", "overview", "en", "it", configuration, out var actual));
        var expected = AnimeClickAiTranslator.BuildTranslationCacheKey("scope", "identity", "overview", "en", "it",
            "model", configuration.AiEndpoint, "key", plain);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("<html><h1>Access denied</h1></html>")]
    [InlineData("<html><h1>Pubblicità</h1><div id='trama-div'>Sponsor</div></html>")]
    public void NonAnimePagesAreNotAcceptedAsMetadata(string html)
        => Assert.Throws<InvalidDataException>(() => new AnimeClickHtmlParser().ParseAnimePage("https://www.animeclick.it/anime/72/naruto", html));

    [Theory]
    [InlineData("https://www.animeclick.it/anime/72/naruto?utm_source=test", true)]
    [InlineData("72/naruto", true)]
    [InlineData("72-naruto", true)]
    [InlineData("https://evil.invalid/anime/72/naruto", false)]
    [InlineData("https://www.animeclick.it/episodio/72/naruto", false)]
    [InlineData("https://name:secret@www.animeclick.it/anime/72/naruto", false)]
    public void ManualIdentificationAcceptsOnlyAnimeCardLinksOrIds(string input, bool expected)
        => Assert.Equal(expected, AnimeClickClient.TryNormalizeAnimeInput(input, out _));

    [Fact]
    public void ExternalEpisodeLinksPointToEpisodesNotAnimeCards()
    {
        var episode = new Episode();
        episode.SetProviderId("AnimeClick", "123/episodio");
        Assert.Equal("https://www.animeclick.it/episodio/123/episodio",
            Assert.Single(new AnimeClickExternalUrlProvider().GetExternalUrls(episode)));
    }

    [Fact]
    public void ExternalLinksValidateStoredIdsAndCanonicalizeLegacyForms()
    {
        var series = new Series();
        series.SetProviderId("AnimeClick", "72-naruto");
        Assert.Equal("https://www.animeclick.it/anime/72/naruto",
            Assert.Single(new AnimeClickExternalUrlProvider().GetExternalUrls(series)));
        series.SetProviderId("AnimeClick", "../../../unexpected");
        Assert.Empty(new AnimeClickExternalUrlProvider().GetExternalUrls(series));
    }

    [Theory]
    [InlineData("2/new-work", false)]
    [InlineData("1/canonical-slug", true)]
    public void AuthorityDoesNotUndoANewerIdentificationOnTheSamePath(string currentId, bool expectedWrite)
    {
        var path = "/test/" + Guid.NewGuid();
        var snapshot = new Series { Path = path, Name = "Old work" };
        snapshot.SetProviderId("AnimeClick", "1/old-slug");
        using (var lease = AnimeClickMetadataAuthorityStore.Begin<Series>(path, "1/old-slug"))
            lease.Capture(snapshot);
        var current = new Series { Path = path, Name = "Current title" };
        current.SetProviderId("AnimeClick", currentId);
        var result = AnimeClickMetadataAuthorityStore.Apply(current);
        Assert.Equal(expectedWrite, result != ItemUpdateType.None);
        Assert.Equal(expectedWrite ? "Old work" : "Current title", current.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualIdentificationRechecksConcurrentChangesBeforeSaving(bool lockDuringFetch)
    {
        var item = new Series { Id = Guid.NewGuid(), Name = "Existing" };
        item.SetProviderId("AnimeClick", "1/old");
        var writes = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById") return item;
            if (method.Name == "UpdateItemAsync") writes++;
            return TestDoubles.DefaultReturn(method);
        });
        var handler = new ResponseHandler(_ =>
        {
            if (lockDuringFetch) item.IsLocked = true;
            else item.SetProviderId("AnimeClick", "3/concurrent");
            return new(HttpStatusCode.OK) { Content = new StringContent("<h1 itemprop='name'>Valid anime</h1>") };
        });
        var factory = TestDoubles.Proxy<IHttpClientFactory>((_, _) => new HttpClient(handler, false));
        var controller = new AnimeClickIdentifyController(library, TestDoubles.Proxy<IProviderManager>(),
            new AnimeClickClient(factory, NullLogger<AnimeClickClient>.Instance), new AnimeClickHtmlParser(),
            TestDoubles.Proxy<MediaBrowser.Model.IO.IFileSystem>(), NullLogger<AnimeClickIdentifyController>.Instance);
        var result = await controller.IdentifyAndRefresh(new() { ItemId = item.Id.ToString(), AnimeClickId = "2/new" }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(0, writes);
        Assert.Equal(lockDuringFetch ? "1/old" : "3/concurrent", item.GetProviderId("AnimeClick"));
    }

    private sealed class ResponseHandler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response(request.RequestUri!));
        }
    }
}
