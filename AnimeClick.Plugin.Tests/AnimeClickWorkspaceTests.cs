using AnimeClick.Plugin.Api;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AnimeClickWorkspaceTests
{
    [Fact]
    public void SingleSeriesAuditKeepsTheSameBreakdownAsTheLibraryReport()
    {
        var item = new LibraryAuditSeriesItem();
        item.ApplyReasons(
        [
            AnimeClickAuditReason.PendingRefresh,
            AnimeClickAuditReason.CatalogNotCached,
            AnimeClickAuditReason.TitleNotPublished,
            AnimeClickAuditReason.CardHasNoTitles,
            AnimeClickAuditReason.CardNotResolved
        ]);

        Assert.Equal(5, item.MissingTitleCount);
        Assert.Equal(2, item.RecoverableTitleCount);
        Assert.Equal(1, item.WaitingTitleCount);
        Assert.Equal(2, item.UnavailableTitleCount);
        Assert.Equal(nameof(AnimeClickAuditReason.CardNotResolved), item.Reason);
        Assert.False(string.IsNullOrWhiteSpace(item.ReasonLabel));

        item.ApplyReasons([]);
        Assert.Equal(0, item.MissingTitleCount);
        Assert.Equal(0, item.UnavailableTitleCount);
        Assert.Equal(nameof(AnimeClickAuditReason.Ok), item.Reason);
    }

    [Fact]
    public void FreshInstallationStartsWithTheFullSetup()
    {
        var configuration = new PluginConfiguration();
        Assert.True(configuration.ApplyMigrations());
        Assert.Equal(0, configuration.SetupCompletedVersion);
        Assert.Equal(3, configuration.ConfigurationVersion);
        Assert.False(configuration.ApplyMigrations());
    }

    [Fact]
    public void ExistingInstallationSkipsTheFirstRunSetupAndKeepsItsChoices()
    {
        // A schema-1 install that deliberately chose 30 seconds: the schema-1 upgrade must not run again.
        var configuration = new PluginConfiguration { ConfigurationVersion = 1, EpisodeTranslationTimeoutSec = 30, AiModel = "chosen" };
        Assert.True(configuration.ApplyMigrations());
        Assert.Equal(1, configuration.SetupCompletedVersion);
        Assert.Equal(30, configuration.EpisodeTranslationTimeoutSec);
        Assert.Equal("chosen", configuration.AiModel);
        Assert.Equal(3, configuration.ConfigurationVersion);

        var alreadyDone = new PluginConfiguration { ConfigurationVersion = 1, SetupCompletedVersion = 5 };
        alreadyDone.ApplyMigrations();
        Assert.Equal(5, alreadyDone.SetupCompletedVersion);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, 1000)]
    public void SetupVersionIsKeptInRange(int stored, int expected)
    {
        var configuration = new PluginConfiguration { SetupCompletedVersion = stored };
        configuration.Sanitize();
        Assert.Equal(expected, configuration.SetupCompletedVersion);
    }

    [Fact]
    public void EnablingPutsAnimeClickFirstForMetadataAndKeepsEveryOtherProvider()
    {
        var options = new LibraryOptions
        {
            TypeOptions =
            [
                new TypeOptions { Type = "Series", MetadataFetchers = ["TheMovieDb", "AniList"], MetadataFetcherOrder = ["TheMovieDb", "AniList"], ImageFetchers = ["TheMovieDb"], ImageFetcherOrder = ["TheMovieDb"] },
                new TypeOptions { Type = "Episode", MetadataFetchers = ["TheMovieDb"], MetadataFetcherOrder = [] },
                new TypeOptions { Type = "MusicAlbum", MetadataFetchers = ["MusicBrainz"] }
            ]
        };

        Assert.True(AnimeClickLibraryWorkspace.ApplyAnimeClick(options, AnimeClickLibraryWorkspace.RelevantTypes("tvshows"), imagesFirst: false));

        var series = options.TypeOptions[0];
        Assert.Equal(["TheMovieDb", "AniList", "AnimeClick"], series.MetadataFetchers);
        Assert.Equal(["AnimeClick", "TheMovieDb", "AniList"], series.MetadataFetcherOrder);
        Assert.Equal(["TheMovieDb", "AnimeClick"], series.ImageFetchers);
        Assert.Equal(["TheMovieDb", "AnimeClick"], series.ImageFetcherOrder);
        Assert.Equal(["AnimeClick"], options.TypeOptions[1].MetadataFetcherOrder);
        Assert.Equal(["MusicBrainz"], options.TypeOptions[2].MetadataFetchers);

        // Applying again is a no-op, so the library options are not rewritten for nothing.
        Assert.False(AnimeClickLibraryWorkspace.ApplyAnimeClick(options, AnimeClickLibraryWorkspace.RelevantTypes("tvshows"), imagesFirst: false));

        Assert.True(AnimeClickLibraryWorkspace.ApplyAnimeClick(options, AnimeClickLibraryWorkspace.RelevantTypes("tvshows"), imagesFirst: true));
        Assert.Equal(["AnimeClick", "TheMovieDb"], series.ImageFetcherOrder);
    }

    [Fact]
    public void ImagesGoFirstOnlyWithIntegratedArtwork()
    {
        Assert.False(AnimeClickLibraryWorkspace.IntegratedArtworkConfigured(new PluginConfiguration()));
        Assert.True(AnimeClickLibraryWorkspace.IntegratedArtworkConfigured(new PluginConfiguration { TmdbApiKey = "k" }));
        Assert.False(AnimeClickLibraryWorkspace.IntegratedArtworkConfigured(new PluginConfiguration { TmdbApiKey = "k", EnableIntegratedImages = false }));
        Assert.True(AnimeClickLibraryWorkspace.IntegratedArtworkConfigured(new PluginConfiguration { FanartProjectApiKey = "f" }));
    }

    [Fact]
    public void LibraryStatusDescribesEachVideoLibrary()
    {
        var folders = new List<VirtualFolderInfo>
        {
            Folder("Anime", CollectionTypeOptions.tvshows, new TypeOptions { Type = "Series", MetadataFetchers = ["AnimeClick", "TheMovieDb"], MetadataFetcherOrder = ["AnimeClick"], ImageFetchers = ["AnimeClick"] },
                new TypeOptions { Type = "Season", MetadataFetchers = ["AnimeClick"] },
                new TypeOptions { Type = "Episode", MetadataFetchers = ["AnimeClick"] }),
            Folder("Film", CollectionTypeOptions.movies, new TypeOptions { Type = "Movie", MetadataFetchers = ["TheMovieDb", "AnimeClick"], MetadataFetcherOrder = ["TheMovieDb", "AnimeClick"] }),
            Folder("Serie TV", CollectionTypeOptions.tvshows, new TypeOptions { Type = "Series", MetadataFetchers = ["TheMovieDb"] }),
            Folder("Vuota", CollectionTypeOptions.tvshows),
            Folder("Musica", CollectionTypeOptions.music, new TypeOptions { Type = "MusicAlbum", MetadataFetchers = ["MusicBrainz"] })
        };
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name == "GetVirtualFolders" ? folders : TestDoubles.DefaultReturn(method));
        var statuses = new AnimeClickLibraryWorkspace(library, NullLogger<AnimeClickLibraryWorkspace>.Instance).GetLibraries();

        Assert.Equal(["Anime", "Film", "Serie TV", "Vuota"], statuses.Select(status => status.Name));
        Assert.Equal(AnimeClickLibraryState.Active, statuses[0].State);
        Assert.True(statuses[0].CanEnable); // Season and episode images are still missing.
        Assert.Equal(AnimeClickLibraryState.Partial, statuses[1].State);
        Assert.Equal(AnimeClickLibraryState.Inactive, statuses[2].State);
        Assert.True(statuses[2].CanEnable);
        Assert.Equal(AnimeClickLibraryState.Unmanaged, statuses[3].State);
        Assert.False(statuses[3].CanEnable);
    }

    [Fact]
    public void EnablingAnUnknownLibraryReportsAnErrorWithoutTouchingAnything()
    {
        var workspace = new AnimeClickLibraryWorkspace(TestDoubles.Proxy<ILibraryManager>(), NullLogger<AnimeClickLibraryWorkspace>.Instance);
        var result = workspace.Enable("not-a-guid", new PluginConfiguration());
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(workspace.Enable(Guid.NewGuid().ToString(), new PluginConfiguration()).Success);
    }

    [Fact]
    public void ShowcaseListsTheMostRecentlyRefreshedAnimeOnly()
    {
        var old = Series("Vecchia", "100", DateTime.UtcNow.AddDays(-3));
        var recent = Series("Recente", "200", DateTime.UtcNow.AddMinutes(-5));
        recent.Overview = new string('a', 500);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film", DateLastRefreshed = DateTime.UtcNow.AddHours(-1) };
        movie.SetProviderId("AnimeClick", "300/film");
        var foreign = Series("Senza ID", null, DateTime.UtcNow);
        var library = TestDoubles.Proxy<ILibraryManager>((method, _) => method.Name == "GetItemList"
            ? new List<BaseItem> { old, recent, movie, foreign }
            : TestDoubles.DefaultReturn(method));

        var showcase = new AnimeClickLibraryWorkspace(library, NullLogger<AnimeClickLibraryWorkspace>.Instance)
            .GetShowcase(2, new PluginConfiguration());

        Assert.Equal(2, showcase.SeriesCount);
        Assert.Equal(1, showcase.MovieCount);
        Assert.Equal(["Recente", "Film"], showcase.Items.Select(item => item.Name));
        Assert.Equal("Movie", showcase.Items[1].Type);
        Assert.Equal("https://www.animeclick.it/anime/300/film", showcase.Items[1].AnimeClickUrl);
        Assert.True(showcase.Items[0].Overview!.Length <= 321);
        Assert.EndsWith("…", showcase.Items[0].Overview);

        var catalog = new AnimeClickLibraryWorkspace(library, NullLogger<AnimeClickLibraryWorkspace>.Instance).GetCatalog();
        Assert.Equal(["Film", "Recente", "Vecchia"], catalog.Select(item => item.Name));
        Assert.Equal("300/film", catalog[0].AnimeClickId);
    }

    [Fact]
    public void EveryPageAssetIsEmbeddedAndEveryLoadedScriptIsRegistered()
    {
        var plugin = (AnimeClick.Plugin.Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AnimeClick.Plugin.Plugin));
        var pages = plugin.GetPages().ToList();
        var assembly = typeof(AnimeClick.Plugin.Plugin).Assembly;
        foreach (var page in pages)
        {
            using var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.True(stream is not null, page.Name + " → " + page.EmbeddedResourcePath);
        }

        using var html = new StreamReader(assembly.GetManifestResourceStream("AnimeClick.Plugin.Configuration.configPage.html")!);
        var markup = html.ReadToEnd();
        var scripts = System.Text.RegularExpressions.Regex.Match(markup, @"SCRIPTS = \[(?<list>[^\]]+)\]").Groups["list"].Value
            .Split(',').Select(name => name.Trim().Trim('\'')).ToList();
        Assert.NotEmpty(scripts);
        Assert.All(scripts, name => Assert.Contains(pages, page => page.Name == name));
        Assert.Contains(pages, page => page.Name == "AnimeClickCss");
    }

    [Fact]
    public void TheAuditCountsExactlyTheEpisodesTheTitleRepairWillInspect()
    {
        var config = new PluginConfiguration { TmdbApiKey = "k" };
        static Episode Episode(string name, bool locked = false) => new()
        {
            Name = name, Path = "/anime/serie/s01e03.mkv", LockedFields = locked ? [MetadataField.Name] : []
        };
        Func<bool> unused = () => throw new InvalidOperationException("library options are read only when needed");
        bool Candidate(Episode episode, string? series, string? season, string? tmdb, Func<bool> library)
            => AnimeClick.Plugin.Tasks.AnimeClickRefreshMissingTitlesTask.IsCandidate(episode, series, season, tmdb, null, library, config);

        Assert.True(Candidate(Episode("Episodio 3"), "72", null, null, unused));
        Assert.True(Candidate(Episode("Episodio 3"), null, "99", null, unused));
        Assert.False(Candidate(Episode("Episodio 3", locked: true), "72", null, null, unused));
        Assert.False(Candidate(Episode("Una giornata al mare"), "72", null, null, unused));
        Assert.True(Candidate(Episode("Episodio 3"), null, null, "555", () => true));
        Assert.False(Candidate(Episode("Episodio 3"), null, null, "555", () => false));
        Assert.False(Candidate(Episode("Episodio 3"), null, null, null, () => true));
        config.EnableEpisodeTitleFallback = false;
        Assert.False(Candidate(Episode("Episodio 3"), null, null, "555", () => true));
    }

    private static Series Series(string name, string? animeClickId, DateTime refreshed)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name, DateLastRefreshed = refreshed };
        if (animeClickId is not null)
        {
            series.SetProviderId("AnimeClick", animeClickId);
        }

        return series;
    }

    private static VirtualFolderInfo Folder(string name, CollectionTypeOptions type, params TypeOptions[] options) => new()
    {
        Name = name,
        ItemId = Guid.NewGuid().ToString("N"),
        CollectionType = type,
        LibraryOptions = new LibraryOptions { TypeOptions = options }
    };
}
