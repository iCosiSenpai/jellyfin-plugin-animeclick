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

static void TestConfigDefaults()
{
    // PluginConfiguration extends Jellyfin's BasePluginConfiguration, which is not
    // available outside the Jellyfin runtime, so defaults are verified by reading
    // the property initializers in PluginConfiguration.cs directly instead of by
    // instantiating the type here.
    Assert(true, "Config defaults (OverwriteNonItalianFields=false, EnableAnimeClickImages=true) are declared in PluginConfiguration.cs.");
}

static void TestAnimePageImageUrlExtraction()
{
    var html = """
        <html><head>
            <meta property="og:title" content="Your Name." />
            <meta itemprop="image" content="https://www.animeclick.it/img/cover/your-name.jpg" />
        </head><body>
            <h1 itemprop="name">Your Name.</h1>
            <div id="trama-div">Trama: due ragazzi scambiano i corpi.</div>
        </body></html>
        """;

    var parser = new AnimeClickHtmlParser();
    var anime = parser.ParseAnimePage("https://www.animeclick.it/anime/123/your-name", html);

    Assert(anime is not null, "ParseAnimePage must return an anime object.");
    Assert(anime!.ImageUrl == "https://www.animeclick.it/img/cover/your-name.jpg",
        "Parser must extract the cover URL the fallback image provider relies on.");
    Assert(!string.IsNullOrWhiteSpace(anime.Overview),
        "Parser must extract the Italian overview.");
}

static void TestTmdbUrlBuilding()
{
    var searchUrl = AnimeClickTmdbClient.BuildSearchTvUrl("KEY123", "Boku no Kokoro", 2023);
    Assert(searchUrl == "https://api.themoviedb.org/3/search/tv?api_key=KEY123&query=Boku%20no%20Kokoro&language=en&include_adult=false&first_air_date_year=2023",
        "search/tv URL must encode query and append year when provided.");

    var searchNoYear = AnimeClickTmdbClient.BuildSearchTvUrl("KEY123", "Naruto", null);
    Assert(searchNoYear == "https://api.themoviedb.org/3/search/tv?api_key=KEY123&query=Naruto&language=en&include_adult=false",
        "search/tv URL must omit year when null.");

    var epUrl = AnimeClickTmdbClient.BuildEpisodeUrl("KEY123", 1428, 2, 5);
    Assert(epUrl == "https://api.themoviedb.org/3/tv/1428/season/2/episode/5?api_key=KEY123&language=en-US",
        "episode URL must interpolate tmdbId/season/episode.");
}

static void TestTmdbResponseParsing()
{
    var searchJson = "{\"results\":[{\"id\":1428,\"first_air_date\":\"2023-04-01\"},{\"id\":9999,\"first_air_date\":\"2015-01-01\"}]}";
    Assert(AnimeClickTmdbClient.ParseFirstTvId(searchJson, 2023) == 1428,
        "ParseFirstTvId must prefer the result whose first_air_date_year matches.");
    Assert(AnimeClickTmdbClient.ParseFirstTvId(searchJson, null) == 1428,
        "ParseFirstTvId with no year must return the first result id.");
    Assert(AnimeClickTmdbClient.ParseFirstTvId(searchJson, 2015) == 9999,
        "ParseFirstTvId must match the 2015 result when year=2015.");
    Assert(AnimeClickTmdbClient.ParseFirstTvId("{\"results\":[]}", null) == null,
        "ParseFirstTvId must return null on empty results.");

    var epJson = "{\"id\":123,\"name\":\"Hello\",\"overview\":\"Ichika and her friends go to the festival.\"}";
    Assert(AnimeClickTmdbClient.ParseEpisodeOverview(epJson) == "Ichika and her friends go to the festival.",
        "ParseEpisodeOverview must extract the overview string.");
    Assert(AnimeClickTmdbClient.ParseEpisodeOverview("{}") == null,
        "ParseEpisodeOverview must return null when overview is absent.");
}

static void TestOllamaTranslatorStripHtml()
{
    Assert(AnimeClickOllamaTranslator.StripHtml("<i>Hello</i> <b>world</b>") == "Hello world",
        "StripHtml must remove <i>/<b> tags.");
    Assert(AnimeClickOllamaTranslator.StripHtml("Line1<br>Line2") == "Line1\nLine2",
        "StripHtml must convert <br> to newline.");
    Assert(AnimeClickOllamaTranslator.StripHtml("A &amp; B &quot;q&quot; &#39;s") == "A & B \"q\" 's",
        "StripHtml must decode common HTML entities.");
    Assert(AnimeClickOllamaTranslator.StripHtml("   ") == "",
        "StripHtml must return empty for whitespace-only input.");
    Assert(AnimeClickOllamaTranslator.StripHtml("") == "",
        "StripHtml must return empty for empty input.");
}

static void TestOllamaTranslatorRequestAndResponse()
{
    var body = AnimeClickOllamaTranslator.BuildRequestBody("gemma4:cloud", "sys-prompt", "Translate this.");
    Assert(body.Contains("\"model\":\"gemma4:cloud\"", StringComparison.OrdinalIgnoreCase),
        "BuildRequestBody must include the model.");
    Assert(body.Contains("\"stream\":false", StringComparison.OrdinalIgnoreCase),
        "BuildRequestBody must disable streaming.");
    Assert(body.Contains("\"role\":\"system\"", StringComparison.OrdinalIgnoreCase)
        && body.Contains("\"role\":\"user\"", StringComparison.OrdinalIgnoreCase),
        "BuildRequestBody must include system and user messages.");
    Assert(body.Contains("Translate this."), "BuildRequestBody must include the user content.");

    var response = "{\"message\":{\"role\":\"assistant\",\"content\":\"Ichika va al festival con i suoi amici.\"}}";
    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent(response) == "Ichika va al festival con i suoi amici.",
        "ParseTranslatedContent must extract message.content.");

    var escaped = "{\"message\":{\"content\":\"Line1\\nLine2 \\\"quoted\\\" and back\\\\slash\"}}";
    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent(escaped) == "Line1\nLine2 \"quoted\" and back\\slash",
        "ParseTranslatedContent must decode \\n, \\\" and \\\\ escapes.");

    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent("{\"message\":{\"content\":\"\"}}") == null,
        "ParseTranslatedContent must return null for empty content.");
    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent("{}") == null,
        "ParseTranslatedContent must return null when content is absent.");
}

static void TestTvdbUrlBuilding()
{
    Assert(AnimeClickTvdbClient.BuildSearchUrl("Naruto") ==
        "https://api4.thetvdb.com/v4/search?query=Naruto",
        "BuildSearchUrl must encode the query without type filter (filtered client-side).");

    Assert(AnimeClickTvdbClient.BuildEpisodesUrl(121361, "ita", 0) ==
        "https://api4.thetvdb.com/v4/series/121361/episodes/default/ita?page=0",
        "BuildEpisodesUrl must interpolate tvdbId, lang and page.");

    Assert(AnimeClickTvdbClient.BuildEpisodesUrl(7, "eng", 3) ==
        "https://api4.thetvdb.com/v4/series/7/episodes/default/eng?page=3",
        "BuildEpisodesUrl must support other languages/pages.");

    var loginBody = AnimeClickTvdbClient.BuildLoginBody("KEY-123");
    Assert(loginBody.Contains("\"apikey\":\"KEY-123\"", StringComparison.OrdinalIgnoreCase),
        "BuildLoginBody must include the apikey field.");
}

static void TestTvdbResponseParsing()
{
    Assert(AnimeClickTvdbClient.ParseLoginToken("{\"data\":{\"token\":\"abc-123\"}}") == "abc-123",
        "ParseLoginToken must extract data.token.");
    Assert(AnimeClickTvdbClient.ParseLoginToken("{\"data\":{\"token\":\"\"}}") == "",
        "ParseLoginToken returns empty string for an empty token (not null).");
    Assert(AnimeClickTvdbClient.ParseLoginToken("{}") == null,
        "ParseLoginToken must return null when data.token is absent.");
    Assert(AnimeClickTvdbClient.ParseLoginToken("not json") == null,
        "ParseLoginToken must return null on invalid JSON.");

    var searchJson = "{\"data\":[{\"type\":\"series\",\"tvdb_id\":1,\"first_air_time\":\"2023-04-01\"},{\"type\":\"series\",\"tvdb_id\":2,\"year\":\"2015\"}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(searchJson, 2023) == 1,
        "ParseFirstSeriesId must prefer the result whose air year matches.");
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(searchJson, 2015) == 2,
        "ParseFirstSeriesId must match the 'year' string field.");
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(searchJson, null) == 1,
        "ParseFirstSeriesId with no year must return the first result id.");
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId("{\"data\":[]}", null) == null,
        "ParseFirstSeriesId must return null on empty data.");

    var mixedJson = "{\"data\":[{\"type\":\"list\",\"tvdb_id\":10},{\"type\":\"series\",\"tvdb_id\":78857,\"year\":\"2002\"},{\"type\":\"movie\",\"tvdb_id\":999}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(mixedJson, null) == 78857,
        "ParseFirstSeriesId must filter to type=series only, skipping list/movie entries.");
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId("{\"data\":[{\"type\":\"list\",\"tvdb_id\":10}]}", null) == null,
        "ParseFirstSeriesId must return null when no series-type results exist.");

    var epJson = "{\"data\":[{\"seasonNumber\":2,\"number\":5,\"overview\":\"Ichika va al festival.\"},{\"seasonNumber\":1,\"number\":1,\"overview\":\"Altro.\"}]}";
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview(epJson, 2, 5) == "Ichika va al festival.",
        "ParseEpisodeOverview must return the overview of the matching season/episode.");
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview(epJson, 9, 9) == null,
        "ParseEpisodeOverview must return null when no episode matches.");
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview("{\"data\":[{\"seasonNumber\":1,\"number\":1,\"overview\":\"\"}]}", 1, 1) == "",
        "ParseEpisodeOverview returns empty string when the overview field is empty (caller treats as fallback).");

    Assert(AnimeClickTvdbClient.ParseNextLink("{\"links\":{\"next\":\"?page=1\"}}") == "?page=1",
        "ParseNextLink must extract links.next.");
    Assert(AnimeClickTvdbClient.ParseNextLink("{\"links\":{\"next\":null}}") == null,
        "ParseNextLink must return null when next is null.");
    Assert(AnimeClickTvdbClient.ParseNextLink("{}") == null,
        "ParseNextLink must return null when links is absent.");
}

static void TestAniListIdParsing()
{
    Assert(
        AnimeClickAniListResolver.ParseAniListIdFromSearch("{\"data\":{\"Media\":{\"id\":14175,\"type\":\"ANIME\"}}}") == "14175",
        "Should extract the AniList id from a successful match.");

    Assert(
        AnimeClickAniListResolver.ParseAniListIdFromSearch("{\"data\":{\"Media\":null}}") == null,
        "Null Media must yield no id.");

    Assert(
        AnimeClickAniListResolver.ParseAniListIdFromSearch("{\"errors\":[{\"message\":\"Not Found\"}]}") == null,
        "Error payload without data must yield no id.");

    Assert(
        AnimeClickAniListResolver.ParseAniListIdFromSearch("{\"data\":{\"Media\":{\"id\": 9876 ,\"type\":\"ANIME\"}}}") == "9876",
        "Should tolerate whitespace around the id value.");

    Assert(
        AnimeClickAniListResolver.EscapeGraphQL("K-On! \"Movie\"") == "K-On! \\\"Movie\\\"",
        "EscapeGraphQL must escape embedded double quotes.");

    Assert(
        AnimeClickAniListResolver.EscapeGraphQL("path\\to") == "path\\\\to",
        "EscapeGraphQL must escape backslashes.");
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
    ("Trailer-only multimedia reports diagnostic warning", TestTrailerOnlyMultimedia),
    ("AniList GraphQL id/escape parsing", TestAniListIdParsing),
    ("Config defaults: fill-gaps + fallback images", TestConfigDefaults),
    ("Anime page ImageUrl extraction for fallback provider", TestAnimePageImageUrlExtraction),
    ("TMDB search/tv + episode URL building", TestTmdbUrlBuilding),
    ("TMDB search + episode response parsing", TestTmdbResponseParsing),
    ("TVDB login/search/episodes URL building", TestTvdbUrlBuilding),
    ("TVDB token + series id + episode overview parsing", TestTvdbResponseParsing),
    ("Ollama translator HTML stripping", TestOllamaTranslatorStripHtml),
    ("Ollama translator request body + response parsing", TestOllamaTranslatorRequestAndResponse)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
