using System.Net.Http;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class AnimeClickOrchestrationTests
{
    private const string ItalianOverview =
        "Questa è una storia che racconta come una ragazza viene coinvolta nella vita degli amici mentre tutto cambia.";

    private const string EnglishOverview =
        "This is a story about a girl who discovers that her friends have changed while they are living together.";

    private const string UnknownOverview =
        "Galaxy pilots confront ancient machines beyond distant planets during catastrophic battles beneath crimson moons.";

    [Fact]
    public void RefreshSchedulerDeduplicatesOnlyTheSamePhaseAndUsesSafeOptions()
    {
        var queued = new List<QueuedRefresh>();
        var intentRegistry = new AnimeClickMetadataRefreshIntentRegistry();
        var scheduler = CreateScheduler(
            TestDoubles.Proxy<ILibraryManager>(),
            queued,
            intentRegistry);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Test" };

        Assert.True(scheduler.TryQueue(item, MetadataField.Overview, "library-quality-repair"));
        Assert.False(scheduler.TryQueue(item, MetadataField.Overview, "library-quality-repair"));
        Assert.True(scheduler.TryQueue(item, MetadataField.Overview, "background-translation-completed"));

        Assert.Equal(2, queued.Count);
        var options = queued[0].Options;
        Assert.Equal(MetadataRefreshMode.ValidationOnly, options.MetadataRefreshMode);
        Assert.Equal(MetadataRefreshMode.None, options.ImageRefreshMode);
        Assert.False(options.ReplaceAllMetadata);
        Assert.False(options.ReplaceAllImages);
        Assert.True(options.IsAutomated);
        Assert.True(intentRegistry.HasIntent(options.DirectoryService, item, MetadataField.Overview));
    }

    [Fact]
    public void TranslationRefreshUsesPreTranslationStateAndWorkKeyDeduplication()
    {
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/library/movie.mkv",
            Overview = EnglishOverview
        };
        var libraryManager = TestDoubles.Proxy<ILibraryManager>((method, _) =>
            method.Name == nameof(ILibraryManager.FindByPath)
                ? item
                : TestDoubles.DefaultReturn(method));
        var queued = new List<QueuedRefresh>();
        var scheduler = CreateScheduler(libraryManager, queued);

        Assert.True(scheduler.TryCaptureOverviewByPath(item.Path, out var expectedOverview));
        item.Overview = "A newer English synopsis that must not be replaced by the older queued source.";
        Assert.False(scheduler.TryQueueByPathIfUnchanged(
            item.Path,
            MetadataField.Overview,
            "background-translation-completed",
            expectedOverview,
            "work-key-a"));

        item.Overview = expectedOverview;
        Assert.True(scheduler.TryQueueByPathIfUnchanged(
            item.Path,
            MetadataField.Overview,
            "background-translation-completed",
            expectedOverview,
            "work-key-a"));
        Assert.True(scheduler.TryQueueByPathIfUnchanged(
            item.Path,
            MetadataField.Overview,
            "background-translation-completed",
            expectedOverview,
            "work-key-b"));
        Assert.False(scheduler.TryQueueByPathIfUnchanged(
            item.Path,
            MetadataField.Overview,
            "background-translation-completed",
            expectedOverview,
            "work-key-b"));
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public void RefreshSchedulerHonoursLocksAndReleasesAFailedClaim()
    {
        var attempts = 0;
        var providerManager = TestDoubles.Proxy<IProviderManager>((method, _) =>
        {
            if (method.Name == nameof(IProviderManager.QueueRefresh))
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("simulated queue failure");
                }

                return null;
            }

            return TestDoubles.DefaultReturn(method);
        });
        var scheduler = CreateScheduler(
            TestDoubles.Proxy<ILibraryManager>(),
            providerManager);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Retry" };

        Assert.False(scheduler.TryQueue(item, MetadataField.Overview, "retryable"));
        Assert.True(scheduler.TryQueue(item, MetadataField.Overview, "retryable"));
        Assert.Equal(2, attempts);

        var lockedItem = new Movie { Id = Guid.NewGuid(), IsLocked = true };
        var lockedField = new Movie
        {
            Id = Guid.NewGuid(),
            LockedFields = [MetadataField.Overview]
        };
        Assert.False(scheduler.TryQueue(lockedItem, MetadataField.Overview, "locked-item"));
        Assert.False(scheduler.TryQueue(lockedField, MetadataField.Overview, "locked-field"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SeasonResolverCacheOnlyPathUsesTheYearKeyAndNeverCreatesAClient()
    {
        using var temporary = new TemporaryAnimeClickCache();
        await temporary.Cache.SetAsync(
            "seasonMap:v6::123/root::2::2024",
            "456/sequel",
            CancellationToken.None);

        var clientCreations = 0;
        var httpClientFactory = TestDoubles.Proxy<IHttpClientFactory>((method, _) =>
        {
            if (method.Name == nameof(IHttpClientFactory.CreateClient))
            {
                clientCreations++;
                return new HttpClient();
            }

            return TestDoubles.DefaultReturn(method);
        });
        var resolver = new AnimeClickSeasonResolver(
            new AnimeClickClient(httpClientFactory, NullLogger<AnimeClickClient>.Instance),
            temporary.Cache,
            new AnimeClickHtmlParser(),
            NullLogger<AnimeClickSeasonResolver>.Instance);
        var configuration = new PluginConfiguration { CacheHours = 48 };

        var hit = await resolver.ResolveCachedAsync(
            "123/root",
            2,
            configuration,
            CancellationToken.None,
            new Dictionary<int, int> { [2] = 2024 });
        var differentYearMiss = await resolver.ResolveCachedAsync(
            "123/root",
            2,
            configuration,
            CancellationToken.None,
            new Dictionary<int, int> { [2] = 2025 });
        var firstSeasonMiss = await resolver.ResolveCachedAsync(
            "123/root",
            1,
            configuration,
            CancellationToken.None);

        Assert.Equal("456/sequel", hit);
        Assert.Null(differentYearMiss);
        Assert.Null(firstSeasonMiss);
        Assert.Equal(0, clientCreations);
    }

    [Fact]
    public async Task StableNumericPrefixesClearHashedCurrentAndLegacyKeys()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var longId = "123/" + new string('à', 180);
        var entries = new[]
        {
            (Key: $"episodes:raw:v6::{longId}::24:2", Prefix: "episodes:raw:v6::123/"),
            (Key: $"episodes:raw:v5::{longId}::24:2", Prefix: "episodes:raw:v5::123/"),
            (Key: $"seasonMap:v6::{longId}::2::2024", Prefix: "seasonMap:v6::123/"),
            (Key: $"seasonMap:v5::{longId}::2::2024", Prefix: "seasonMap:v5::123/"),
            (Key: $"translation:v4::{longId}::episode.overview::en-it::hash", Prefix: "translation:v4::123/"),
            (Key: $"translation:v3::{longId}::episode.overview::en-it::hash", Prefix: "translation:v3::123/")
        };

        foreach (var entry in entries)
        {
            await temporary.Cache.SetAsync(entry.Key, "value", CancellationToken.None);
            Assert.Equal(
                "value",
                await temporary.Cache.GetAsync<string>(entry.Key, CancellationToken.None));
            Assert.Equal(1, temporary.Cache.ClearByPrefix(entry.Prefix));
            Assert.Null(await temporary.Cache.GetAsync<string>(entry.Key, CancellationToken.None));
        }
    }

    [Fact]
    public void LibraryQualityAuditCountsStatusesAndRepairsOnlyEnglishOrMissing()
    {
        var items = new List<BaseItem>
        {
            CreateMovie(1, "Italiano", ItalianOverview),
            CreateMovie(2, "English", EnglishOverview),
            CreateMovie(3, "Missing", string.Empty),
            CreateMovie(4, "Unknown", UnknownOverview),
            CreateMovie(5, "Locked English", EnglishOverview, locked: true)
        };
        var libraryManager = CreateLibraryManager(items);
        var queued = new List<QueuedRefresh>();
        var scheduler = CreateScheduler(libraryManager, queued);
        var service = new AnimeClickLibraryQualityService(
            libraryManager,
            scheduler,
            NullLogger<AnimeClickLibraryQualityService>.Instance);

        var report = service.Audit();

        Assert.Equal(5, report.GroupCount);
        Assert.Equal(5, report.ItemCount);
        Assert.Equal(1, report.ItalianCount);
        Assert.Equal(2, report.EnglishCount);
        Assert.Equal(1, report.MissingCount);
        Assert.Equal(1, report.UnknownCount);
        Assert.Equal(1, report.LockedCount);
        Assert.Equal(2, report.RepairableCount);

        var repair = service.QueueRepair(items.Select(item => item.Id.ToString("N")));
        Assert.Equal(5, repair.RequestedCount);
        Assert.Equal(5, repair.ConsideredCount);
        Assert.Equal(2, repair.QueuedCount);
        Assert.Equal(3, repair.SkippedCount);
        Assert.False(repair.Truncated);
        Assert.Equal(2, queued.Count);
        Assert.All(queued, call => Assert.Equal(MetadataField.Overview, call.Field));
    }

    [Fact]
    public void LibraryQualityRepairCapsARequestAtOneHundredItems()
    {
        var items = Enumerable.Range(1, 101)
            .Select(index => (BaseItem)CreateMovie(index, $"Missing {index}", string.Empty))
            .ToList();
        var libraryManager = CreateLibraryManager(items);
        var queued = new List<QueuedRefresh>();
        var service = new AnimeClickLibraryQualityService(
            libraryManager,
            CreateScheduler(libraryManager, queued),
            NullLogger<AnimeClickLibraryQualityService>.Instance);

        var result = service.QueueRepair(items.Select(item => item.Id.ToString("N")));

        Assert.Equal(101, result.RequestedCount);
        Assert.Equal(100, result.ConsideredCount);
        Assert.Equal(100, result.QueuedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.True(result.Truncated);
        Assert.Equal(100, queued.Count);
    }

    [Fact]
    public async Task OverviewOnlyAuthorityPreservesEveryOtherMetadataField()
    {
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Nome invariato",
            OriginalTitle = "Original title",
            Overview = EnglishOverview,
            Genres = ["Drammatico"],
            Studios = ["Studio invariato"],
            ProductionYear = 2024
        };
        item.SetProviderId("AnimeClick", "123/scheda");
        item.SetProviderId("Tmdb", "456");

        var directoryService = new DirectoryService(TestDoubles.Proxy<IFileSystem>());
        var intentRegistry = new AnimeClickMetadataRefreshIntentRegistry();
        intentRegistry.Register(
            directoryService,
            item,
            MetadataField.Overview,
            "library-quality-repair",
            overview: null);
        var resolver = new StubOverviewResolver(ItalianOverview);
        var provider = new AnimeClickMovieAuthorityProvider(
            intentRegistry,
            resolver,
            NullLogger<AnimeClickMovieAuthorityProvider>.Instance);
        var options = new MetadataRefreshOptions(directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly
        };

        Assert.True(provider.HasChanged(item, directoryService));
        var update = await provider.FetchAsync(item, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal(ItalianOverview, item.Overview);
        Assert.Equal("Nome invariato", item.Name);
        Assert.Equal("Original title", item.OriginalTitle);
        Assert.Equal(["Drammatico"], item.Genres);
        Assert.Equal(["Studio invariato"], item.Studios);
        Assert.Equal(2024, item.ProductionYear);
        Assert.Equal("123/scheda", item.GetProviderId("AnimeClick"));
        Assert.Equal("456", item.GetProviderId("Tmdb"));
        Assert.Equal(1, resolver.Calls);
        Assert.False(provider.HasChanged(item, directoryService));
    }

    [Fact]
    public async Task OverviewOnlyAuthorityUsesPublishedTranslationWithoutResolvingAgain()
    {
        var item = new Movie { Id = Guid.NewGuid(), Overview = EnglishOverview };
        var directoryService = new DirectoryService(TestDoubles.Proxy<IFileSystem>());
        var intentRegistry = new AnimeClickMetadataRefreshIntentRegistry();
        intentRegistry.Register(
            directoryService,
            item,
            MetadataField.Overview,
            "background-translation-completed",
            ItalianOverview);
        var resolver = new StubOverviewResolver("must not be used");
        var provider = new AnimeClickMovieAuthorityProvider(
            intentRegistry,
            resolver,
            NullLogger<AnimeClickMovieAuthorityProvider>.Instance);

        var update = await provider.FetchAsync(
            item,
            new MetadataRefreshOptions(directoryService),
            CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal(ItalianOverview, item.Overview);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task OverviewOnlyAuthorityDoesNotOverwriteANewerManualValue()
    {
        var item = new Movie { Id = Guid.NewGuid(), Overview = EnglishOverview };
        var directoryService = new DirectoryService(TestDoubles.Proxy<IFileSystem>());
        var intentRegistry = new AnimeClickMetadataRefreshIntentRegistry();
        intentRegistry.Register(
            directoryService,
            item,
            MetadataField.Overview,
            "background-translation-completed",
            ItalianOverview);
        var resolver = new StubOverviewResolver("must not be used");
        var provider = new AnimeClickMovieAuthorityProvider(
            intentRegistry,
            resolver,
            NullLogger<AnimeClickMovieAuthorityProvider>.Instance);

        const string manualOverview =
            "Questa sinossi italiana è stata corretta manualmente dopo che il lavoro era stato accodato.";
        item.Overview = manualOverview;
        var update = await provider.FetchAsync(
            item,
            new MetadataRefreshOptions(directoryService),
            CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal(manualOverview, item.Overview);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void TranslationPendingFansOutExpectedStatesAndRejectsLateOrInvalidatedJoins()
    {
        var first = new AnimeClickPendingRefreshTarget("/library/a.mkv", EnglishOverview);
        var pending = new AnimeClickPendingTranslation(8, first);

        Assert.True(pending.TryJoin(
            8,
            new AnimeClickPendingRefreshTarget("/library/b.mkv", string.Empty)));
        Assert.True(pending.TryJoin(
            8,
            new AnimeClickPendingRefreshTarget("/library/a.mkv", "newer value must not authorize")));
        Assert.True(pending.TryJoin(
            10,
            new AnimeClickPendingRefreshTarget("/library/c.mkv", null)));

        var targets = pending.Seal().OrderBy(target => target.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["/library/a.mkv", "/library/b.mkv", "/library/c.mkv"],
            targets.Select(target => target.Path));
        Assert.Equal(EnglishOverview, targets[0].ExpectedOverview);
        Assert.False(pending.TryJoin(
            10,
            new AnimeClickPendingRefreshTarget("/library/d.mkv", null)));

        var invalidated = new AnimeClickPendingTranslation(10, first);
        invalidated.Invalidate();
        Assert.True(invalidated.IsInvalidated);
        Assert.False(invalidated.TryJoin(
            12,
            new AnimeClickPendingRefreshTarget("/library/b.mkv", null)));
    }

    [Fact]
    public async Task EnqueueWaitsForTargetedInvalidationInsteadOfLosingUnrelatedWork()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/library/unrelated.mkv",
            Overview = EnglishOverview
        };
        var libraryManager = TestDoubles.Proxy<ILibraryManager>((method, _) =>
            method.Name == nameof(ILibraryManager.FindByPath)
                ? item
                : TestDoubles.DefaultReturn(method));
        var translator = new AnimeClickAiTranslator(
            TestDoubles.Proxy<IHttpClientFactory>(),
            temporary.Cache,
            NullLogger<AnimeClickAiTranslator>.Instance);
        using var queue = new AnimeClickTranslationQueue(
            translator,
            temporary.Cache,
            CreateScheduler(libraryManager, new List<QueuedRefresh>()),
            NullLogger<AnimeClickTranslationQueue>.Instance);
        var configuration = new PluginConfiguration
        {
            EnableEpisodeSynopsisTranslation = true,
            AiProvider = "ollama-cloud",
            AiEndpoint = "https://ollama.com/api/chat",
            AiModel = "test-model",
            AiApiKey = "test-key"
        };

        var invalidation = queue.BeginInvalidation(workKey =>
            workKey.StartsWith("translation:v4::other-series::", StringComparison.Ordinal));
        var enqueue = queue.EnqueueAsync(
            EnglishOverview,
            "current-series",
            "tmdb:series:1:s1:e1",
            "episode.overview",
            "en",
            "it",
            configuration,
            CancellationToken.None,
            item.Path);
        try
        {
            await Task.Delay(25);
            Assert.False(enqueue.IsCompleted);
        }
        finally
        {
            invalidation.Dispose();
        }

        var state = await enqueue;
        Assert.Equal(AnimeClickTranslationQueueState.Queued, state);
    }

    private static Movie CreateMovie(int number, string name, string? overview, bool locked = false)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Overview = overview,
            IsLocked = locked
        };
        movie.SetProviderId("AnimeClick", $"{10_000 + number}/movie-{number}");
        return movie;
    }

    private static ILibraryManager CreateLibraryManager(IReadOnlyCollection<BaseItem> items)
    {
        var byId = items.ToDictionary(item => item.Id);
        var list = items.ToList();
        return TestDoubles.Proxy<ILibraryManager>((method, args) => method.Name switch
        {
            "GetItemList" => list,
            "GetItemById" when args is { Length: > 0 } && args[0] is Guid id =>
                byId.GetValueOrDefault(id),
            _ => TestDoubles.DefaultReturn(method)
        });
    }

    private static AnimeClickMetadataRefreshScheduler CreateScheduler(
        ILibraryManager libraryManager,
        List<QueuedRefresh> queued,
        AnimeClickMetadataRefreshIntentRegistry? intentRegistry = null)
        => CreateScheduler(
            libraryManager,
            TestDoubles.Proxy<IProviderManager>((method, args) =>
            {
                if (method.Name == nameof(IProviderManager.QueueRefresh)
                    && args is { Length: 3 }
                    && args[0] is Guid id
                    && args[1] is MetadataRefreshOptions options
                    && args[2] is RefreshPriority priority)
                {
                    queued.Add(new QueuedRefresh(id, options, priority, MetadataField.Overview));
                    return null;
                }

                return TestDoubles.DefaultReturn(method);
            }),
            intentRegistry);

    private static AnimeClickMetadataRefreshScheduler CreateScheduler(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        AnimeClickMetadataRefreshIntentRegistry? intentRegistry = null)
        => new(
            libraryManager,
            providerManager,
            TestDoubles.Proxy<IFileSystem>(),
            intentRegistry ?? new AnimeClickMetadataRefreshIntentRegistry(),
            NullLogger<AnimeClickMetadataRefreshScheduler>.Instance);

    private sealed class StubOverviewResolver : IAnimeClickOverviewResolver
    {
        private readonly string? _overview;

        public StubOverviewResolver(string? overview)
        {
            _overview = overview;
        }

        public int Calls { get; private set; }

        public Task<string?> ResolveAsync(BaseItem item, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_overview);
        }
    }

    private sealed record QueuedRefresh(
        Guid Id,
        MetadataRefreshOptions Options,
        RefreshPriority Priority,
        MetadataField Field);
}
