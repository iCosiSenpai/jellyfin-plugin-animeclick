using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using AnimeClick.Plugin.Tasks;
using Xunit;

public class AnimeClickPureBehaviorTests
{
    [Fact]
    public void EpisodeProviderIdUsesTheStableNumericIdentity()
    {
        Assert.True(AnimeClickEpisodeProviderId.Equals("426549", "426549/riprese-per-la-tv"));
        Assert.True(AnimeClickEpisodeProviderId.Equals("426549-riprese-per-la-tv", "426549/slug-aggiornato"));
        Assert.True(AnimeClickEpisodeProviderId.Equals(
            "216767/c%C3%A8-una-ragione-per-tutto",
            "216767/cè-una-ragione-per-tutto"));
        Assert.False(AnimeClickEpisodeProviderId.Equals("426549/primo", "426550/secondo"));
    }

    [Fact]
    public void CacheSchemasChangeWhenMatchingOrTranslationSemanticsChange()
    {
        Assert.StartsWith(
            "episodes:raw:v6::",
            AnimeClickEpisodeProvider.BuildCatalogCacheKey("123/root", 24, 2));
        Assert.StartsWith(
            "translation:v4::",
            AnimeClickAiTranslator.BuildTranslationCacheKey(
                "123/root",
                "tmdb:series:42:s1:e1",
                "episode.overview",
                "en",
                "it",
                "model",
                "https://example.invalid/v1/chat/completions",
                "secret",
                "Source text"));
    }

    [Fact]
    public void AnthropicUsesAPortableLimitAndRejectsTruncatedReplies()
    {
        var body = AnimeClickAiProviders.BuildRequestBody(
            AnimeClickAiDialect.Anthropic,
            "claude-model",
            "system",
            "translate");

        Assert.Contains("\"max_tokens\":4096", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"max_tokens\":16000", body, StringComparison.Ordinal);
        Assert.True(AnimeClickAiTranslator.IsResponseTruncated(
            AnimeClickAiDialect.Anthropic,
            "{\"stop_reason\":\"max_tokens\",\"content\":[{\"text\":\"testo\"}]}"));
        Assert.False(AnimeClickAiTranslator.IsResponseTruncated(
            AnimeClickAiDialect.Anthropic,
            "{\"stop_reason\":\"end_turn\",\"content\":[{\"text\":\"testo\"}]}"));
        Assert.False(AnimeClickAiTranslator.IsResponseTruncated(
            AnimeClickAiDialect.OpenAi,
            "{\"stop_reason\":\"max_tokens\"}"));
    }

    [Fact]
    public void ForeignRowsCannotAnchorMatchingOrAuditIdentity()
    {
        var foreign = new AnimeClickEpisode
        {
            ProviderId = "900/foreign",
            Title = "Titolo estraneo",
            IsForeignWork = true,
            Number = 1,
            RawEpisodeNumber = 1,
            GlobalOrdinal = 1,
            SourceOrder = 1
        };
        var valid = new AnimeClickEpisode
        {
            ProviderId = "901/valid",
            Title = "Titolo corretto",
            Number = 1,
            RawEpisodeNumber = 1,
            RawSeasonNumber = 1,
            SeasonNumber = 1,
            SeasonOrdinalNumber = 1,
            GlobalOrdinal = 1,
            SourceOrder = 2
        };

        var match = AnimeClickEpisodeMatcher.Match(
            [foreign, valid],
            new AnimeClickEpisodeMatchContext(1, 1)
            {
                ExistingProviderId = "900/foreign"
            });
        Assert.Same(valid, match.Episode);

        var catalog = AnimeClickEpisodeCatalog.Create([foreign, valid], 1, 1);
        Assert.Equal(
            AnimeClickAuditReason.RowVanished,
            AnimeClickLibraryAudit.ClassifyEpisode(
                "900/foreign",
                currentTitle: null,
                titleNeedsRepair: true,
                catalog));
    }

    [Fact]
    public void PersistedAnchorNeedsSeasonEvidenceForASeasonTwoOnlyLibrary()
    {
        var staleSeasonOneRow = new AnimeClickEpisode
        {
            ProviderId = "500/season-one",
            Title = "Titolo della prima stagione",
            Number = 1,
            RawEpisodeNumber = 1,
            GlobalOrdinal = 1,
            SourceOrder = 1
        };
        var seasonTwoOnly = new AnimeClickEpisodeLibraryLayout(
            Guid.NewGuid(),
            new Dictionary<int, AnimeClickEpisodeSeasonLayout>
            {
                [2] = new(2, 12, 12, true, true)
            });

        var stale = AnimeClickEpisodeMatcher.Match(
            [staleSeasonOneRow],
            new AnimeClickEpisodeMatchContext(2, 1)
            {
                ExistingProviderId = "500/season-one",
                LibraryLayout = seasonTwoOnly
            });
        Assert.Null(stale.Episode);
        Assert.Equal("staleProviderId", stale.Strategy);

        var explicitSeasonCard = AnimeClickEpisodeMatcher.Match(
            [staleSeasonOneRow],
            new AnimeClickEpisodeMatchContext(2, 1)
            {
                ExistingProviderId = "500/season-one",
                IsSeasonSpecificPage = true
            });
        Assert.Same(staleSeasonOneRow, explicitSeasonCard.Episode);
    }

    [Fact]
    public void PersistedAnchorCanUseReliableLibraryBoundaries()
    {
        var anchored = new AnimeClickEpisode
        {
            ProviderId = "513/season-two",
            Title = "Titolo corretto",
            Number = 13,
            RawEpisodeNumber = 13,
            GlobalOrdinal = 13,
            SourceOrder = 13
        };
        var layout = new AnimeClickEpisodeLibraryLayout(
            Guid.NewGuid(),
            new Dictionary<int, AnimeClickEpisodeSeasonLayout>
            {
                [1] = new(1, 12, 12, true, true),
                [2] = new(2, 12, 12, true, true)
            });

        var match = AnimeClickEpisodeMatcher.Match(
            [anchored],
            new AnimeClickEpisodeMatchContext(2, 1)
            {
                ExistingProviderId = "513/season-two",
                LibraryLayout = layout
            });

        Assert.Same(anchored, match.Episode);
        Assert.Equal("providerId", match.Strategy);
    }

    [Theory]
    [InlineData(
        "Questa è una storia che racconta come una ragazza viene coinvolta nella vita degli amici mentre tutto cambia.",
        AnimeClickTextLanguage.Italian)]
    [InlineData(
        "This is a story about a girl who discovers that her friends have changed while they are living together.",
        AnimeClickTextLanguage.English)]
    [InlineData("Una storia molto breve.", AnimeClickTextLanguage.Unknown)]
    [InlineData(
        "Galaxy pilots confront ancient machines beyond distant planets during catastrophic battles beneath crimson moons.",
        AnimeClickTextLanguage.Unknown)]
    [InlineData(
        "Questa una che sono this that they are heroes fighting together tonight.",
        AnimeClickTextLanguage.Unknown)]
    public void LanguageDetectorStaysConservative(string text, AnimeClickTextLanguage expected)
    {
        Assert.Equal(expected, AnimeClickMetadataLanguageDetector.Detect(text).Language);
    }

    [Fact]
    public void LibraryAuditComparesStableIdentityAndEditorialTitle()
    {
        var catalog = AnimeClickEpisodeCatalog.Create(
            [new AnimeClickEpisode
            {
                ProviderId = "426549/riprese-per-la-tv",
                Title = "Perché siamo qui?",
                Number = 5
            }],
            declaredEpisodeCount: 1,
            declaredSeasonsCount: 1);

        Assert.Equal(
            AnimeClickAuditReason.PendingRefresh,
            AnimeClickLibraryAudit.ClassifyEpisode(
                "426549/vecchio-slug",
                "Why Are We Here?",
                titleNeedsRepair: false,
                catalog));
        Assert.Equal(
            AnimeClickAuditReason.Ok,
            AnimeClickLibraryAudit.ClassifyEpisode(
                "426549",
                "PERCHE, SIAMO QUI",
                titleNeedsRepair: false,
                catalog));
    }

    [Fact]
    public void NameLockOnlyChangesAnActionableRefresh()
    {
        Assert.Equal(
            AnimeClickAuditReason.Locked,
            AnimeClickLibraryAudit.ApplyNameLock(AnimeClickAuditReason.PendingRefresh, isNameLocked: true));
        Assert.Equal(
            AnimeClickAuditReason.PendingRefresh,
            AnimeClickLibraryAudit.ApplyNameLock(AnimeClickAuditReason.PendingRefresh, isNameLocked: false));
        Assert.Equal(
            AnimeClickAuditReason.RowVanished,
            AnimeClickLibraryAudit.ApplyNameLock(AnimeClickAuditReason.RowVanished, isNameLocked: true));
        Assert.Equal(
            AnimeClickAuditReason.Ok,
            AnimeClickLibraryAudit.ApplyNameLock(AnimeClickAuditReason.Ok, isNameLocked: true));
    }

    [Fact]
    public void RotatingWindowCoversEveryCandidateWithoutStarvation()
    {
        var values = Enumerable.Range(0, 450).ToList();

        var first = AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, 0, 200);
        var second = AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, first.NextCursor!.Value, 200);
        var third = AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, second.NextCursor!.Value, 200);
        var negative = AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, -50, 200);

        Assert.Equal(Enumerable.Range(0, 200), first.Items);
        Assert.Equal(200, first.NextCursor);
        Assert.Equal(Enumerable.Range(200, 200), second.Items);
        Assert.Equal(400, second.NextCursor);
        Assert.Equal(Enumerable.Range(400, 50).Concat(Enumerable.Range(0, 150)), third.Items);
        Assert.Equal(150, third.NextCursor);
        Assert.Equal(third.Items, negative.Items);
    }

    [Fact]
    public void RotatingWindowDoesNotRequestPersistenceWhenEverythingFits()
    {
        var values = Enumerable.Range(0, 200).ToList();
        var window = AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, 137, 200);

        Assert.Equal(values, window.Items);
        Assert.Null(window.NextCursor);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AnimeClickRefreshMissingTitlesTask.SelectRotatingWindow(values, 0, 0));
    }
}
