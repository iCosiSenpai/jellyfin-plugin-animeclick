using AnimeClick.Plugin.Services;

static void TestDangersSeasonOrdinalMatching()
{
    var parser = new AnimeClickHtmlParser();
    var episodes = parser.ParseEpisodesPage(TestFixtures.DangersEpisodesHtml, "https://www.animeclick.it");

    Assert(episodes.Count == 25, "Expected 25 parsed episodes.");

    var s2e1 = AnimeClickEpisodeMatcher.Match(episodes, 2, 1);
    var s2e5 = AnimeClickEpisodeMatcher.Match(episodes, 2, 5);
    var s2e13 = AnimeClickEpisodeMatcher.Match(episodes, 2, 13);

    Assert(s2e1.Episode?.AbsoluteNumber == 13, "S02E01 must map to absolute episode 13.");
    Assert(s2e1.Episode?.Title == "Noi stiamo cercando", "S02E01 title mismatch.");
    Assert(s2e1.Strategy == "seasonOrdinal", "S02E01 should use seasonOrdinal strategy.");

    Assert(s2e5.Episode?.AbsoluteNumber == 17, "S02E05 must map to absolute episode 17.");
    Assert(s2e5.Episode?.Title == "Io voglio saperne di piu", "S02E05 title mismatch.");
    Assert(s2e5.Strategy == "seasonOrdinal", "S02E05 should use seasonOrdinal strategy.");

    Assert(s2e13.Episode?.AbsoluteNumber == 25, "S02E13 must map to absolute episode 25.");
    Assert(s2e13.Episode?.Title == "Il nostro amore piu puro", "S02E13 title mismatch.");
    Assert(s2e13.Strategy == "seasonOrdinal", "S02E13 should use seasonOrdinal strategy.");

    Assert(s2e5.Episode?.Title != "S1 titolo 5", "S02E05 must not fall back to S1 episode 5.");
    Assert(s2e5.Episode?.ProviderId == "90017/io-voglio-saperne-di-piu", "Episode provider ID should come from /episodio URL.");
}

static void TestSearchScoring()
{
    var parser = new AnimeClickHtmlParser();
    var results = parser.ParseSearchResults(TestFixtures.SearchHtml, "https://www.animeclick.it")
        .OrderByDescending(r => AnimeClickSearchScorer.Score(r, "The Dangers in My Heart", 2023, seriesRequest: true))
        .ToList();

    Assert(results[0].Id == "44780/boku-no-kokoro-no-yabai-yatsu", "Expected the 2023 TV series to rank first.");
    Assert(results[0].Format?.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) == true, "Expected parser to retain TV format.");
}

static void TestTrailerOnlyMultimedia()
{
    var parser = new AnimeClickHtmlParser();
    var diagnostics = parser.ParseMultimediaDiagnostics(TestFixtures.TrailerOnlyMultimediaHtml);

    Assert(diagnostics.Songs.Count == 0, "Trailer-only page must not invent OP/ED songs.");
    Assert(diagnostics.HasTrailerOrPvOnly, "Trailer-only page should expose a warning state.");
    Assert(!string.IsNullOrWhiteSpace(diagnostics.Warning), "Trailer-only page should include a diagnostic warning.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var tests = new (string Name, Action Run)[]
{
    ("Dangers S2 matcher uses season ordinal", TestDangersSeasonOrdinalMatching),
    ("Search scorer prefers 2023 series over movie and special", TestSearchScoring),
    ("Trailer-only multimedia reports diagnostic warning", TestTrailerOnlyMultimedia)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
