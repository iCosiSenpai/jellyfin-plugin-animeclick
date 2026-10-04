using System.Net;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

public sealed class AnimeClickLibraryWorkspaceTests
{
    private static Episode Episode(string name = "Episodio 3") => new()
    {
        Id = Guid.NewGuid(), Name = name, IndexNumber = 3, ParentIndexNumber = 1,
        Overview = "Questa è una trama già corretta e deve rimanere al suo posto.",
        Path = "/private/anime/s01e03.mkv", RunTimeTicks = 12345
    };

    [Fact]
    public async Task TitleRepairChangesOnlyThePlaceholderName()
    {
        var item = Episode(); item.SetProviderId("AnimeClick", "90003"); item.SetProviderId("Tvdb", "555");
        var saves = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById") return item;
            if (method.Name == "UpdateItemAsync") { saves++; return Task.CompletedTask; }
            return TestDoubles.DefaultReturn(method);
        });
        var repair = new AnimeClickTitleRepairService(library, new TitleResolver(() => "Una nuova giornata"));
        Assert.True(await repair.RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(1, saves); Assert.Equal("Una nuova giornata", item.Name);
        Assert.Equal(3, item.IndexNumber); Assert.Equal(1, item.ParentIndexNumber);
        Assert.Equal(12345, item.RunTimeTicks); Assert.Equal("90003", item.GetProviderId("AnimeClick"));
        Assert.Equal("555", item.GetProviderId("Tvdb")); Assert.Equal("/private/anime/s01e03.mkv", item.Path);
        Assert.Equal("Questa è una trama già corretta e deve rimanere al suo posto.", item.Overview);
    }

    [Theory]
    [InlineData("A new beginning", true)]
    [InlineData("The End", true)]
    [InlineData("My Brother", true)]
    [InlineData("I want to see you again", true)]
    [InlineData("Me Target My Brother", true)]
    [InlineData("The girl I like", true)]
    [InlineData("I'm Dead", true)]
    [InlineData("Happy Birthday", true)]
    [InlineData("I am Ichikawa", true)]
    [InlineData("Vivere in pace", false)]
    [InlineData("L'amore della mia vita", false)]
    [InlineData("Il ritorno di John Smith", false)]
    [InlineData("Una nuova giornata", false)]
    [InlineData("La fine del mondo", false)]
    [InlineData("Vigilia di Natale", false)]
    [InlineData("San Valentino", false)]
    [InlineData("Steins;Gate", false)]
    [InlineData("John Smith", false)]
    [InlineData("Love Live!", false)]
    [InlineData("One Piece", false)]
    [InlineData("I Little Busters", false)]
    [InlineData("Day", false)]
    public void EnglishTitleSelectionPreservesItalianAndUncertainNames(string name, bool english)
    {
        Assert.Equal(english, AnimeClickMetadataLanguageDetector.IsEnglishEpisodeTitle(name));
        Assert.Equal(english, AnimeClick.Plugin.Tasks.AnimeClickRefreshMissingTitlesTask.NeedsTitle(Episode(name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnglishTitleRepairOnlyWritesAnItalianReplacementAndRespectsNameLocks(bool locked)
    {
        var item = Episode("A new beginning"); item.LockedFields = locked ? [MetadataField.Name] : [];
        item.SetProviderId("Tmdb", "88"); var writes = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => item,
            "UpdateItemAsync" => Save(),
            _ => TestDoubles.DefaultReturn(method)
        });
        Task Save() { writes++; return Task.CompletedTask; }
        var repair = new AnimeClickTitleRepairService(library, new TitleResolver(() => "Un nuovo inizio"));
        Assert.Equal(!locked, await repair.RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(locked ? "A new beginning" : "Un nuovo inizio", item.Name);
        Assert.Equal(locked ? 0 : 1, writes);
        Assert.Equal("88", item.GetProviderId("Tmdb")); Assert.Equal(3, item.IndexNumber);
        Assert.Equal("Questa è una trama già corretta e deve rimanere al suo posto.", item.Overview);
    }

    [Theory]
    [InlineData("A new beginning")]
    [InlineData("The beginning of the end")]
    [InlineData("Episodio 3")]
    public async Task EnglishOrGenericLookupResultsNeverCountAsARepair(string replacement)
    {
        var item = Episode("A new beginning"); var writes = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById") return item;
            if (method.Name == "UpdateItemAsync") writes++;
            return TestDoubles.DefaultReturn(method);
        });
        Assert.False(await new AnimeClickTitleRepairService(library, new TitleResolver(() => replacement))
            .RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(0, writes); Assert.Equal("A new beginning", item.Name);
    }

    [Fact]
    public async Task AnotherEnglishManualCorrectionDuringLookupWins()
    {
        var item = Episode("A new beginning");
        var resolver = new TitleResolver(() => { item.Name = "My best friend"; return "Un nuovo inizio"; });
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name == "GetItemById" ? item : TestDoubles.DefaultReturn(method));
        Assert.False(await new AnimeClickTitleRepairService(library, resolver).RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal("My best friend", item.Name);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("lock")]
    [InlineData("number")]
    public async Task TitleRepairRechecksConcurrentChanges(string change)
    {
        var item = Episode(); var saves = 0;
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById") return item;
            if (method.Name == "UpdateItemAsync") saves++;
            return TestDoubles.DefaultReturn(method);
        });
        var resolver = new TitleResolver(() =>
        {
            if (change == "manual") item.Name = "Titolo scelto da me";
            if (change == "lock") item.LockedFields = [MetadataField.Name];
            if (change == "number") item.IndexNumber = 4;
            return "Titolo remoto";
        });
        Assert.False(await new AnimeClickTitleRepairService(library, resolver).RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(0, saves);
        Assert.NotEqual("Titolo remoto", item.Name);
    }

    [Theory]
    [InlineData("Titolo compilato", false)]
    [InlineData("Episodio 3", true)]
    public async Task TitleRepairPreservesMeaningfulAndLockedNames(string name, bool locked)
    {
        var item = Episode(name); item.IsLocked = locked;
        var resolver = new TitleResolver(() => throw new InvalidOperationException("Should never look up"));
        Assert.False(await new AnimeClickTitleRepairService(TestDoubles.Proxy<ILibraryManager>(), resolver).RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(name, item.Name);
    }

    [Fact]
    public async Task TitleRepairRestoresTheInMemoryNameIfSavingFails()
    {
        var item = Episode();
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => item,
            "UpdateItemAsync" => Task.FromException(new IOException("Disk failure")),
            _ => TestDoubles.DefaultReturn(method)
        });
        await Assert.ThrowsAsync<IOException>(() => new AnimeClickTitleRepairService(library, new TitleResolver(() => "Nuovo titolo"))
            .RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal("Episodio 3", item.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TitleRepairRespectsProviderDisabledBeforeOrDuringLookup(bool duringLookup)
    {
        var item = Episode(); var saves = 0;
        var options = new LibraryOptions { TypeOptions = [new() { Type = "Episode", MetadataFetchers = duringLookup ? ["AnimeClick"] : ["TheMovieDb"] }] };
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name switch
        {
            "GetLibraryOptions" => options,
            "GetItemById" => item,
            "UpdateItemAsync" => Save(),
            _ => TestDoubles.DefaultReturn(method)
        });
        Task Save() { saves++; return Task.CompletedTask; }
        var resolver = new TitleResolver(() => { options.TypeOptions[0].MetadataFetchers = []; return "Titolo remoto"; });
        Assert.False(await new AnimeClickTitleRepairService(library, resolver).RepairAsync(item, new(), CancellationToken.None));
        Assert.Equal(0, saves); Assert.Equal("Episodio 3", item.Name);
    }

    [Fact]
    public void ExistingTitlesAreNotReportedAsRepairableWhenTheyDifferFromAnimeClick()
    {
        var catalog = AnimeClickEpisodeCatalog.Create([new AnimeClickEpisode { ProviderId = "123", Number = 3, Title = "Altro titolo" }], 1, 1);
        Assert.Equal(AnimeClickAuditReason.Ok, AnimeClickLibraryAudit.ClassifyEpisode("123", "Titolo scelto da me", false, catalog));
    }

    [Fact]
    public void ActivitiesKeepRealCountersAndDoNotRestartOnDoubleClick()
    {
        var activities = new AnimeClickActivityService();
        Assert.True(activities.TryQueue("titles")); Assert.False(activities.TryQueue("titles"));
        Assert.True(activities.Begin("titles"));
        activities.Update("titles", 10, 4, 2, 1, 1, "Work");
        Assert.False(activities.Begin("titles"));
        Assert.Equal(40, activities.Get("titles").Progress);
        Assert.False(activities.TryQueue("titles"));
        activities.Cancel("titles"); Assert.Equal("Cancelling", activities.Get("titles").State);
        activities.Finish("titles", "Cancelled", "Stopped");
        Assert.Equal(2, activities.Get("titles").Applied); Assert.Equal(40, activities.Get("titles").Progress);
        Assert.True(activities.TryQueue("titles"));
    }

    private sealed class TitleResolver(Func<string?> resolve) : IAnimeClickTitleResolver
    {
        public Task<string?> ResolveTitleAsync(Episode episode, AnimeClickTitleRepairSession refreshedCatalogs, CancellationToken token)
            => Task.FromResult(resolve());
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
