using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AnimeClick.Plugin.Api;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The page's maintenance tools: unlocking, the card preview before a binding, the cache numbers and
/// the diagnostic report.
/// </summary>
public class AnimeClickMaintenanceTests
{
    private const string ItalianOverview =
        "Questa è una storia che racconta come una ragazza viene coinvolta nella vita degli amici mentre tutto cambia.";

    private const string EnglishOverview =
        "This is a story about a girl who discovers that her friends have changed while they are living together.";

    private const string CardHtml = """
        <html>
          <head><meta property="og:title" content="Dandadan" /></head>
          <body>
            <h1 itemprop="name">Dandadan</h1>
            <div id="trama-div" itemprop="description">Trama: Momo Ayase crede ai fantasmi, il suo compagno Okarun agli alieni: una scommessa li trascina in un mondo dove esistono entrambi.</div>
          </body>
        </html>
        """;

    [Fact]
    public async Task UnlockingAnUnknownFieldIsRefusedInsteadOfRemovingEveryLock()
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Serie con correzioni manuali",
            LockedFields = [MetadataField.Name, MetadataField.Overview]
        };
        var updates = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id, ..])
            {
                return id == series.Id ? series : null;
            }

            if (method.Name == nameof(ILibraryManager.UpdateItemAsync))
            {
                updates++;
                return Task.CompletedTask;
            }

            return TestDoubles.DefaultReturn(method);
        });
        using var temporary = new TemporaryAnimeClickCache();
        var controller = CreateIdentifyController(library, temporary, new CountingHandler(CardHtml));

        var refused = await controller.UnlockItem(
            new UnlockItemRequest { ItemId = series.Id.ToString("N"), IncludeChildren = false, Field = "Nmae" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Equal(0, updates);
        Assert.Equal([MetadataField.Name, MetadataField.Overview], series.LockedFields);

        var accepted = await controller.UnlockItem(
            new UnlockItemRequest { ItemId = series.Id.ToString("N"), IncludeChildren = false, Field = "name" },
            CancellationToken.None);

        var response = Assert.IsType<UnlockItemResponse>(Assert.IsType<OkObjectResult>(accepted.Result).Value);
        Assert.Equal(1, response.UnlockedCount);
        Assert.Equal([MetadataField.Overview], series.LockedFields);
    }

    [Fact]
    public async Task SynopsisUnlockKeepsTheLockOfItemsTheRepairCannotImprove()
    {
        var english = CreateMovie(1, "Trama inglese bloccata", EnglishOverview);
        var italian = CreateMovie(2, "Trama italiana corretta a mano", ItalianOverview);
        var items = new BaseItem[] { english, italian };
        foreach (var item in items)
        {
            item.LockedFields = [MetadataField.Overview];
        }

        var library = TestDoubles.Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id, ..])
            {
                return items.FirstOrDefault(item => item.Id == id);
            }

            return method.Name == nameof(ILibraryManager.UpdateItemAsync)
                ? Task.CompletedTask
                : TestDoubles.DefaultReturn(method);
        });
        var queued = new List<Guid>();
        var providerManager = TestDoubles.Proxy<IProviderManager>((method, args) =>
        {
            if (method.Name == nameof(IProviderManager.QueueRefresh) && args is [Guid id, ..])
            {
                queued.Add(id);
            }

            return TestDoubles.DefaultReturn(method);
        });
        using var temporary = new TemporaryAnimeClickCache();
        var service = new AnimeClickLibraryQualityService(
            library,
            new AnimeClickMetadataRefreshScheduler(
                library,
                providerManager,
                TestDoubles.Proxy<IFileSystem>(),
                new AnimeClickMetadataRefreshIntentRegistry(),
                NullLogger<AnimeClickMetadataRefreshScheduler>.Instance),
            new AnimeClickRepairLedger(temporary.Cache, NullLogger<AnimeClickRepairLedger>.Instance),
            NullLogger<AnimeClickLibraryQualityService>.Instance);

        var result = await service.QueueRepairAsync(
            items.Select(item => item.Id.ToString("N")),
            force: true,
            CancellationToken.None,
            unlock: true);

        Assert.Equal(1, result.UnlockedCount);
        Assert.Equal(1, result.QueuedCount);
        Assert.Empty(english.LockedFields);
        Assert.Equal([MetadataField.Overview], italian.LockedFields);
        Assert.Equal([english.Id], queued);
    }

    [Fact]
    public async Task CardPreviewShowsTheCardAndTheTitlesAlreadyBoundToItWithoutWriting()
    {
        var selected = new Series { Id = Guid.NewGuid(), Name = "Dan Da Dan" };
        var other = new Series { Id = Guid.NewGuid(), Name = "Dandadan (doppione)" };
        other.SetProviderId("AnimeClick", "46016/dandadan");
        var unrelated = new Movie { Id = Guid.NewGuid(), Name = "Altro film" };
        unrelated.SetProviderId("AnimeClick", "460160/altro");
        selected.SetProviderId("AnimeClick", "46016");

        // The provider writes the series card on a season without a card of its own: same work.
        var ownSeason = new Season { Id = Guid.NewGuid(), Name = "Stagione 1", SeriesId = selected.Id };
        ownSeason.SetProviderId("AnimeClick", "46016/dandadan");
        var all = new List<BaseItem> { selected, ownSeason, other, unrelated };
        var updates = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemList))
            {
                return all;
            }

            if (method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id, ..])
            {
                return all.FirstOrDefault(item => item.Id == id);
            }

            if (method.Name == nameof(ILibraryManager.UpdateItemAsync))
            {
                updates++;
                return Task.CompletedTask;
            }

            return TestDoubles.DefaultReturn(method);
        });
        using var temporary = new TemporaryAnimeClickCache();
        var handler = new CountingHandler(CardHtml);
        var controller = CreateIdentifyController(library, temporary, handler);

        var first = await controller.PreviewCard(
            "https://www.animeclick.it/anime/46016/dandadan",
            selected.Id.ToString("N"),
            CancellationToken.None);

        var preview = Assert.IsType<AnimeClickCardPreview>(Assert.IsType<OkObjectResult>(first.Result).Value);
        Assert.Equal("46016/dandadan", preview.AnimeClickId);
        Assert.Equal("Dandadan", preview.Title);
        Assert.StartsWith("Momo Ayase", preview.Overview);
        var use = Assert.Single(preview.UsedBy);
        Assert.Equal(other.Id.ToString("N"), use.ItemId);
        Assert.Equal("Series", use.Type);
        Assert.Equal(0, updates);
        Assert.Equal(1, handler.Requests);

        // Correcting the season instead: its own series is still not "another" title.
        var fromSeason = await controller.PreviewCard("46016/dandadan", ownSeason.Id.ToString("N"), CancellationToken.None);
        var seasonPreview = Assert.IsType<AnimeClickCardPreview>(Assert.IsType<OkObjectResult>(fromSeason.Result).Value);
        Assert.Equal([other.Id.ToString("N")], seasonPreview.UsedBy.Select(item => item.ItemId));

        // The card is cached: looking at it again costs AnimeClick nothing.
        var second = await controller.PreviewCard("46016/dandadan", null, CancellationToken.None);
        var again = Assert.IsType<AnimeClickCardPreview>(Assert.IsType<OkObjectResult>(second.Result).Value);
        Assert.Equal(3, again.UsedBy.Count);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task CardPreviewRefusesInputThatIsNotAnAnimeClickCard()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var handler = new CountingHandler(CardHtml);
        var controller = CreateIdentifyController(TestDoubles.Proxy<ILibraryManager>(), temporary, handler);

        foreach (var input in new[] { "", "https://example.org/anime/1/x", "../../etc/passwd", "javascript:alert(1)" })
        {
            var result = await controller.PreviewCard(input, null, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CardPreviewReportsAPageThatIsNotACard()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var controller = CreateIdentifyController(
            TestDoubles.Proxy<ILibraryManager>(),
            temporary,
            new CountingHandler("<html><body><p>Pubblicità</p></body></html>"));

        var result = await controller.PreviewCard("999/pagina-sbagliata", null, CancellationToken.None);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, failure.StatusCode);
        Assert.Null(await temporary.Cache.GetAsync<AnimeClickAnime>(
            "anime::https://www.animeclick.it/anime/999/pagina-sbagliata",
            CancellationToken.None));
    }

    [Fact]
    public async Task CacheStatisticsGroupEntriesBySource()
    {
        using var temporary = new TemporaryAnimeClickCache();
        Assert.Equal(0, temporary.Cache.GetStatistics().Entries);

        await temporary.Cache.SetAsync("anime::https://www.animeclick.it/anime/1/a", new AnimeClickAnime { Title = "A" }, CancellationToken.None);
        await temporary.Cache.SetAsync("anime::https://www.animeclick.it/anime/2/b", new AnimeClickAnime { Title = "B" }, CancellationToken.None);
        await temporary.Cache.SetAsync("seasonMap:v7::1::2", new List<string> { "2" }, CancellationToken.None);
        await temporary.Cache.SetAsync("translation:v4::1::abc", "Una trama tradotta", CancellationToken.None);
        await temporary.Cache.SetAsync("tmdbTvId:v3::1", "123", CancellationToken.None);

        var statistics = temporary.Cache.GetStatistics();

        Assert.Equal(5, statistics.Entries);
        Assert.True(statistics.Bytes > 0);
        Assert.NotNull(statistics.OldestEntryUtc);
        Assert.True(statistics.NewestEntryUtc >= statistics.OldestEntryUtc);
        Assert.Equal(3, statistics.Groups.Single(group => group.Id == "animeclick").Entries);
        Assert.Equal(1, statistics.Groups.Single(group => group.Id == "translation").Entries);
        Assert.Equal(1, statistics.Groups.Single(group => group.Id == "tmdb").Entries);
        Assert.Equal(statistics.Bytes, statistics.Groups.Sum(group => group.Bytes));

        // The group is read from the letters a key starts with, also where ':' cannot be in a file name.
        Assert.Equal("tvdb", AnimeClickCacheService.GroupOf("tvdbSeriesId"));
        Assert.Equal("ledger", AnimeClickCacheService.GroupOf("repairLedger"));
        Assert.Equal("animeclick", AnimeClickCacheService.GroupOf("unaFamigliaNuova"));
    }

    [Fact]
    public void DiagnosticReportNeverCarriesSecretsOrAddresses()
    {
        const string secret = "sk-segreto-9f3b7a";
        const string privateHost = "nas-di-casa.example";
        var configuration = new PluginConfiguration
        {
            TmdbApiKey = secret + "-tmdb",
            TvdbApiKey = secret + "-tvdb",
            FanartPersonalApiKey = secret + "-fanart",
            AiApiKey = secret + "-ai",
            OllamaCloudApiKey = secret + "-legacy",
            CommunityGitHubToken = secret + "-github",
            AiProvider = "custom",
            AiModel = "modello-di-prova",
            AiEndpoint = "https://" + privateHost + "/v1/chat/completions",
            BaseUrl = "https://" + privateHost,
            UserAgent = "Agente-personale/" + privateHost,
            EpisodeLayoutOverrides = "# commento\n72=flat\n\n456=13,24"
        };

        var report = AnimeClickDiagnosticReport.Create(
            configuration,
            "12.0.0",
            [new AnimeClickLibraryStatus { Id = "abc", Name = "Anime di " + privateHost, CollectionType = "tvshows", State = "active" }],
            (Series: 3, Movies: 1),
            [new AnimeClickActivity { Key = "titles", State = "Running", Processed = 4, Message = "Titolo privato · S1 E5: lettura del titolo…" }],
            new AnimeClickCacheStatistics { Entries = 2, Bytes = 10 },
            repairLedgerEntries: 4);

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateHost, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Titolo privato", json, StringComparison.Ordinal);

        Assert.Equal(AnimeClickDiagnosticReport.Set, report.Configuration[nameof(PluginConfiguration.TmdbApiKey)]);
        Assert.Equal(AnimeClickDiagnosticReport.NotSet, report.Configuration[nameof(PluginConfiguration.FanartProjectApiKey)]);
        Assert.Equal("modello-di-prova", report.Configuration[nameof(PluginConfiguration.AiModel)]);
        Assert.Equal("HTTPS, servizio online", report.Configuration[nameof(PluginConfiguration.AiEndpoint)]);
        Assert.Equal("personalizzato", report.Configuration[nameof(PluginConfiguration.BaseUrl)]);

        // The report describes the User-Agent actually sent. One without the plugin's own token is
        // ignored by the client, and a default saved by an earlier version differs only in its version.
        Assert.Equal("predefinito", report.Configuration[nameof(PluginConfiguration.UserAgent)]);
        string Describe(string userAgent)
            => (string)AnimeClickDiagnosticReport.Redact(new PluginConfiguration { UserAgent = userAgent })[nameof(PluginConfiguration.UserAgent)]!;
        Assert.Equal("predefinito", Describe("AnimeClick-Jellyfin-Plugin/1.2.0.0 (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)"));
        Assert.Equal("personalizzato", Describe("AnimeClick-Jellyfin-Plugin/1.2.0.0 (casa-mia)"));
        Assert.Equal(2, report.Configuration[nameof(PluginConfiguration.EpisodeLayoutOverrides)]);
        Assert.Equal(true, report.Configuration[nameof(PluginConfiguration.EnablePlot)]);
        Assert.Equal(3, report.IdentifiedSeries);
        var activity = Assert.Single(report.Activities);
        Assert.Equal(("titles", "Running", 4), (activity.Key, activity.State, activity.Processed));
        Assert.Equal("tvshows", Assert.Single(report.Libraries).CollectionType);
    }

    [Fact]
    public void EveryStringSettingIsRedactedUnlessExplicitlyPlain()
    {
        // A string added to the configuration later must not reach a public report by default.
        const string marker = "valore-da-non-pubblicare";
        var configuration = new PluginConfiguration();
        var plain = new[]
        {
            nameof(PluginConfiguration.AiProvider),
            nameof(PluginConfiguration.AiModel),
            nameof(PluginConfiguration.CommunitySharingMode)
        };
        foreach (var property in typeof(PluginConfiguration).GetProperties()
                     .Where(property => property.PropertyType == typeof(string) && property.CanWrite))
        {
            property.SetValue(configuration, marker);
        }

        var redacted = AnimeClickDiagnosticReport.Redact(configuration);

        foreach (var pair in redacted.Where(pair => pair.Value is string))
        {
            Assert.True(
                plain.Contains(pair.Key) || !string.Equals((string)pair.Value!, marker, StringComparison.Ordinal),
                pair.Key + " would be published as typed");
        }
    }

    [Theory]
    [InlineData("https://api.example.com/v1/chat", "https://API.example.com/v1/chat", true)]
    [InlineData("https://api.example.com/v1/chat", "https://api.example.com:443/v1/chat", true)]
    [InlineData("https://api.example.com/v1/chat", "https://api.example.com/v2/chat", false)]
    [InlineData("https://api.example.com/v1/chat", "https://api.example.org/v1/chat", false)]
    [InlineData("https://api.example.com/v1/chat", "https://api.example.com:8443/v1/chat", false)]
    public void TheSameAiDestinationIsSchemeHostPortAndPath(string left, string right, bool same)
    {
        Assert.True(AnimeClickAiTranslator.TryNormalizeEndpoint(left, out var leftUri));
        Assert.True(AnimeClickAiTranslator.TryNormalizeEndpoint(right, out var rightUri));
        Assert.Equal(same, AnimeClickAiTranslator.IsSameDestination(leftUri, rightUri));
    }

    [Fact]
    public void ExcerptCutsAtAWordAndLeavesShortTextAlone()
    {
        Assert.Null(AnimeClickMetadataText.Excerpt("   ", 40));
        Assert.Equal("Breve.", AnimeClickMetadataText.Excerpt(" Breve. ", 40));

        var excerpt = AnimeClickMetadataText.Excerpt("Una trama piuttosto lunga, che continua ancora per molte parole senza fermarsi.", 40);

        Assert.NotNull(excerpt);
        Assert.True(excerpt!.Length <= 40);
        Assert.EndsWith("…", excerpt);
        Assert.DoesNotContain(" …", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesTitleRepairUnlocksOnlyTheEpisodesItRewritesAndStopsAtItsBudget()
    {
        var placeholderLocked = CreateEpisode(1, "Episodio 1", MetadataField.Name);
        var correctedByHand = CreateEpisode(2, "Il titolo che ho scritto io", MetadataField.Name);
        var placeholderFree = CreateEpisode(3, "Episodio 3");
        var episodes = new List<Episode> { placeholderLocked, correctedByHand, placeholderFree };
        var library = TestDoubles.Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id, ..])
            {
                return episodes.FirstOrDefault(episode => episode.Id == id);
            }

            return method.Name == nameof(ILibraryManager.UpdateItemAsync)
                ? Task.CompletedTask
                : TestDoubles.DefaultReturn(method);
        });
        var service = new AnimeClickTitleRepairService(library, new NumberedTitleResolver());

        // Without the unlock, a locked episode is left alone.
        var locked = await service.RepairSeriesAsync(episodes, unlock: false, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(new AnimeClickSeriesTitleRepair(Checked: 1, Applied: 1, Unlocked: 0, Remaining: 0), locked);
        Assert.Equal("Episodio 1", placeholderLocked.Name);
        Assert.Equal("Il vero titolo 3", placeholderFree.Name);

        // With it, only the episode that needs a title loses its lock.
        var unlocked = await service.RepairSeriesAsync(episodes, unlock: true, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(new AnimeClickSeriesTitleRepair(Checked: 1, Applied: 1, Unlocked: 1, Remaining: 0), unlocked);
        Assert.Equal("Il vero titolo 1", placeholderLocked.Name);
        Assert.Empty(placeholderLocked.LockedFields);
        Assert.Equal("Il titolo che ho scritto io", correctedByHand.Name);
        Assert.Equal([MetadataField.Name], correctedByHand.LockedFields);

        // An episode no source has a title for keeps its lock, even with the unlock.
        var withoutTitle = CreateEpisode(1, "Episodio 1", MetadataField.Name);
        var noTitleLibrary = TestDoubles.Proxy<ILibraryManager>((method, args) =>
            method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid wanted, ..] && wanted == withoutTitle.Id
                ? withoutTitle
                : method.Name == nameof(ILibraryManager.UpdateItemAsync) ? Task.CompletedTask : TestDoubles.DefaultReturn(method));
        var untitled = await new AnimeClickTitleRepairService(noTitleLibrary, new NumberedTitleResolver(onlyFrom: 99))
            .RepairSeriesAsync([withoutTitle], unlock: true, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(new AnimeClickSeriesTitleRepair(Checked: 1, Applied: 0, Unlocked: 0, Remaining: 0), untitled);
        Assert.Equal([MetadataField.Name], withoutTitle.LockedFields);

        // An exhausted budget still makes progress, one episode per call, and says what is left.
        // The episodes it did not reach keep their lock.
        var many = Enumerable.Range(10, 4).Select(number => CreateEpisode(number, "Episodio " + number, MetadataField.Name)).ToList();
        episodes.AddRange(many);
        var sliced = await service.RepairSeriesAsync(many, unlock: true, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(new AnimeClickSeriesTitleRepair(Checked: 1, Applied: 1, Unlocked: 1, Remaining: 3, ContinueAfter: many[0].Id), sliced);
        Assert.All(many.Skip(1), episode => Assert.Equal([MetadataField.Name], episode.LockedFields));
    }

    [Fact]
    public async Task SeriesTitleRepairKeepsTheLocksOfALibraryThatDoesNotUseAnimeClickForEpisodes()
    {
        // The repair could not write these titles anyway: removing the lock would only expose them.
        var episode = CreateEpisode(1, "Episodio 1", MetadataField.Name);
        var updates = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == nameof(ILibraryManager.GetLibraryOptions))
            {
                return new MediaBrowser.Model.Configuration.LibraryOptions
                {
                    TypeOptions = [new MediaBrowser.Model.Configuration.TypeOptions { Type = "Episode", MetadataFetchers = ["TheMovieDb"] }]
                };
            }

            if (method.Name == nameof(ILibraryManager.UpdateItemAsync))
            {
                updates++;
                return Task.CompletedTask;
            }

            return TestDoubles.DefaultReturn(method);
        });
        var service = new AnimeClickTitleRepairService(library, new NumberedTitleResolver());

        var outcome = await service.RepairSeriesAsync([episode], unlock: true, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(new AnimeClickSeriesTitleRepair(Checked: 0, Applied: 0, Unlocked: 0, Remaining: 0), outcome);
        Assert.Equal([MetadataField.Name], episode.LockedFields);
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task SeriesTitleRepairResumesAfterTheEpisodesThatHaveNoTitleAnywhere()
    {
        // Three episodes no source has a title for, then one that has it: without a cursor every
        // call would spend its budget on the first three again.
        var episodes = Enumerable.Range(1, 4).Select(number => CreateEpisode(number, "Episodio " + number)).ToList();
        var library = TestDoubles.Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id, ..])
            {
                return episodes.FirstOrDefault(episode => episode.Id == id);
            }

            return method.Name == nameof(ILibraryManager.UpdateItemAsync)
                ? Task.CompletedTask
                : TestDoubles.DefaultReturn(method);
        });
        var asked = new List<int?>();
        var service = new AnimeClickTitleRepairService(library, new NumberedTitleResolver(asked, onlyFrom: 4));

        Guid? cursor = null;
        AnimeClickSeriesTitleRepair outcome;
        var calls = 0;
        do
        {
            outcome = await service.RepairSeriesAsync(episodes, unlock: false, TimeSpan.Zero, CancellationToken.None, cursor);
            cursor = outcome.ContinueAfter;
            calls++;
        }
        while (outcome.Remaining > 0 && calls < 10);

        Assert.Equal(4, calls);
        Assert.Equal([1, 2, 3, 4], asked);
        Assert.Equal("Il vero titolo 4", episodes[3].Name);
        Assert.Null(outcome.ContinueAfter);
    }

    private static Episode CreateEpisode(int number, string name, params MetadataField[] lockedFields)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IndexNumber = number,
            ParentIndexNumber = 1,
            LockedFields = lockedFields
        };

    private sealed class NumberedTitleResolver(List<int?>? asked = null, int onlyFrom = 0) : IAnimeClickTitleResolver
    {
        public Task<string?> ResolveTitleAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken cancellationToken)
        {
            asked?.Add(episode.IndexNumber);
            refreshedCatalogs.LastSource = "AnimeClick";
            return Task.FromResult(episode.IndexNumber >= onlyFrom ? "Il vero titolo " + episode.IndexNumber : null);
        }
    }

    private static AnimeClickIdentifyController CreateIdentifyController(
        ILibraryManager library,
        TemporaryAnimeClickCache temporary,
        CountingHandler handler)
    {
        var httpClientFactory = TestDoubles.Proxy<IHttpClientFactory>((method, _) =>
            method.Name == nameof(IHttpClientFactory.CreateClient)
                ? new HttpClient(handler, disposeHandler: false)
                : TestDoubles.DefaultReturn(method));
        return new AnimeClickIdentifyController(
            library,
            TestDoubles.Proxy<IProviderManager>(),
            new AnimeClickClient(httpClientFactory, NullLogger<AnimeClickClient>.Instance),
            new AnimeClickHtmlParser(),
            TestDoubles.Proxy<IFileSystem>(),
            NullLogger<AnimeClickIdentifyController>.Instance,
            community: null,
            cache: temporary.Cache,
            repairLedger: new AnimeClickRepairLedger(temporary.Cache, NullLogger<AnimeClickRepairLedger>.Instance));
    }

    private static Movie CreateMovie(int number, string name, string overview)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, Overview = overview };
        movie.SetProviderId("AnimeClick", $"{20_000 + number}/film-{number}");
        return movie;
    }

    private sealed class CountingHandler(string html) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
        }
    }
}

public class AnimeClickLockAndOutcomeTests
{
    [Fact]
    public void UnCampoSbloccatoLasciaBloccatiGliAltri()
    {
        var series = new MediaBrowser.Controller.Entities.TV.Series { IsLocked = true };

        Assert.True(AnimeClickLocks.Release(series, MediaBrowser.Model.Entities.MetadataField.Name));

        Assert.False(series.IsLocked);
        Assert.DoesNotContain(MediaBrowser.Model.Entities.MetadataField.Name, series.LockedFields);
        Assert.Contains(MediaBrowser.Model.Entities.MetadataField.Overview, series.LockedFields);
        Assert.Contains(MediaBrowser.Model.Entities.MetadataField.Genres, series.LockedFields);
    }

    [Fact]
    public void SbloccareTuttoTogliTuttiIBlocchi()
    {
        var series = new MediaBrowser.Controller.Entities.TV.Series
        {
            IsLocked = true,
            LockedFields = [MediaBrowser.Model.Entities.MetadataField.Overview]
        };

        Assert.True(AnimeClickLocks.Release(series, field: null));
        Assert.False(series.IsLocked);
        Assert.Empty(series.LockedFields);
        Assert.False(AnimeClickLocks.Release(series, field: null));
    }

    [Fact]
    public void SenzaAnimeClickNonESenzaFonte()
    {
        var noSource = AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.NoSource, "no-external-source");

        var unreachable = AnimeClickOverviewResolver.NotNoSourceWhileUnreachable(noSource, animeClickUnreachable: true);
        Assert.Equal(AnimeClickRepairOutcome.Error, unreachable.Outcome);
        Assert.Null(AnimeClickRepairLedger.SuppressionWindow(unreachable.Outcome.ToString()));

        Assert.Same(noSource, AnimeClickOverviewResolver.NotNoSourceWhileUnreachable(noSource, animeClickUnreachable: false));
        Assert.Equal(AnimeClickRepairOutcome.Error,
            AnimeClickOverviewResolver.MapFallbackOutcome(AnimeClickOverviewResolver.AnimeClickUnreachableOutcome));
    }
}
