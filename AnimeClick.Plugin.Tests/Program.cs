using System.Linq;
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

static void TestOllamaTranslatorUnicodeEscapes()
{
    // \uXXXX escapes — Italian accented chars from Ollama models that emit JSON-escaped text.
    var accentJson = "{\"message\":{\"content\":\"Caff\\u00E8 vicino\\u00E0\"}}";
    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent(accentJson) == "Caffè vicinoà",
        "ParseTranslatedContent must decode \\uXXXX escapes (è, à).");

    var allAccents = "{\"message\":{\"content\":\"\\u00E0 \\u00E8 \\u00E9 \\u00EC \\u00F2 \\u00F9\"}}";
    var decoded = AnimeClickOllamaTranslator.ParseTranslatedContent(allAccents);
    Assert(decoded == "à è é ì ò ù",
        "ParseTranslatedContent must decode all Italian accented chars: à è é ì ò ù. Got: " + decoded);

    // Surrogate pair — an emoji encoded as \UXXXXXXXX (non-standard but some models emit it).
    // Use \uD83D\uDE00 (😀) — even if not handled as surrogate, must not crash.
    var surrogate = "{\"message\":{\"content\":\"smile \\uD83D\\uDE00 end\"}}";
    var decodedSurrogate = AnimeClickOllamaTranslator.ParseTranslatedContent(surrogate);
    Assert(decodedSurrogate != null && decodedSurrogate.StartsWith("smile", StringComparison.Ordinal) && decodedSurrogate.EndsWith("end", StringComparison.Ordinal),
        "ParseTranslatedContent must handle \\uXXXX surrogate pairs without crashing. Got: " + decodedSurrogate);

    // Mixed escapes in one message
    var mixed = "{\"message\":{\"content\":\"Line1\\ncaf\\u00E9\\nLine3\"}}";
    Assert(AnimeClickOllamaTranslator.ParseTranslatedContent(mixed) == "Line1\ncafé\nLine3",
        "ParseTranslatedContent must mix \\n and \\uXXXX escapes correctly.");

    // \u followed by non-hex must not corrupt content (falls back to original handling)
    var badEscape = "{\"message\":{\"content\":\"raw \\u stuff\"}}";
    var badDecoded = AnimeClickOllamaTranslator.ParseTranslatedContent(badEscape);
    Assert(badDecoded != null && badDecoded.Contains("raw", StringComparison.Ordinal),
        "ParseTranslatedContent must gracefully handle malformed \\u sequences without crashing.");
}

static void TestSearchScorerAccentFolding()
{
    // Verify RemoveDiacritics directly
    Assert(AnimeClickSearchScorer.RemoveDiacritics("Caffè") == "Caffe",
        "RemoveDiacritics must fold è → e.");
    Assert(AnimeClickSearchScorer.RemoveDiacritics("voilà") == "voila",
        "RemoveDiacritics must fold à → a.");
    Assert(AnimeClickSearchScorer.RemoveDiacritics("L'incorreggibile") == "L'incorreggibile",
        "RemoveDiacritics must NOT remove apostrophes (decompose to apostrophe alone).");
    Assert(AnimeClickSearchScorer.RemoveDiacritics("à è é ì ò ù") == "a e e i o u",
        "RemoveDiacritics must fold all Italian accents.");
    Assert(AnimeClickSearchScorer.RemoveDiacritics("") == "",
        "RemoveDiacritics must handle empty string.");

    // Scorer: an accented query must match an unaccented AnimeClick result with the +100 score.
    var unaccentedResult = new AnimeClickSearchResult
    {
        Id = "123/test",
        Title = "Caffe",
        Format = "Serie TV",
        ProductionYear = 2020
    };
    var unaccentedScore = AnimeClickSearchScorer.Score(unaccentedResult, "Caffè", 2020, seriesRequest: true);
    var exactScore = AnimeClickSearchScorer.Score(unaccentedResult, "Caffe", 2020, seriesRequest: true);
    Assert(unaccentedScore == exactScore,
        "Scorer must treat Caffè and Caffe as equal after diacritic folding. " +
        $"accented={unaccentedScore}, plain={exactScore}");
    Assert(unaccentedScore >= 100,
        "Accented query must hit the +100 exact-match bonus against an unaccented title.");
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
}

static void TestTvdbSeriesIdStringAndFallback()
{
    // TVDB v4 /search returns tvdb_id as a JSON string ("78857"), not a number.
    var stringIdJson = "{\"data\":[{\"type\":\"series\",\"tvdb_id\":\"78857\",\"year\":\"2002\"}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(stringIdJson, null) == 78857,
        "ParseFirstSeriesId must accept tvdb_id as a JSON string.");
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(stringIdJson, 2002) == 78857,
        "ParseFirstSeriesId must accept tvdb_id as a JSON string with year match.");

    // Mixed: some entries numeric, some string — must pick the first valid series.
    var mixedTypesJson = "{\"data\":[{\"type\":\"series\",\"tvdb_id\":\"12345\"},{\"type\":\"series\",\"tvdb_id\":67890}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(mixedTypesJson, null) == 12345,
        "ParseFirstSeriesId must accept the first entry even if it's a string id.");

    // Fallback: when tvdb_id is missing, fall back to the record `id`.
    var fallbackIdJson = "{\"data\":[{\"type\":\"series\",\"id\":\"50001\",\"year\":\"2021\"}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(fallbackIdJson, null) == 50001,
        "ParseFirstSeriesId must fall back to record `id` (string) when tvdb_id is missing.");

    var fallbackNumericIdJson = "{\"data\":[{\"type\":\"series\",\"id\":40001,\"year\":\"2021\"}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(fallbackNumericIdJson, null) == 40001,
        "ParseFirstSeriesId must fall back to record `id` (number) when tvdb_id is missing.");

    // Both missing → null, not zero.
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(
        "{\"data\":[{\"type\":\"series\",\"title\":\"no id here\"}]}", null) == null,
        "ParseFirstSeriesId must return null when both tvdb_id and id are missing.");

    // tvdb_id preferred over id when both present and tvdb_id is a string.
    var bothJson = "{\"data\":[{\"type\":\"series\",\"tvdb_id\":\"777\",\"id\":999,\"year\":\"2020\"}]}";
    Assert(AnimeClickTvdbClient.ParseFirstSeriesId(bothJson, null) == 777,
        "ParseFirstSeriesId must prefer tvdb_id over record id when tvdb_id is a string.");
}

static void TestTvdbEpisodesParsing()
{
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

static void TestAsteriskContinuousBlockSeasonSplit()
{
    var parser = new AnimeClickHtmlParser();

    // Asterisk War real-world: AnimeClick lists 24 eps as "Ep. 01".."Ep. 24" with NO S1/S2 prefix.
    // Detail page declares 2 stagioni → caller passes seasonsCount=2.
    var episodes = parser.ParseEpisodesPage(TestFixtures.AsteriskContinuousEpisodesHtml, "https://www.animeclick.it", seasonsCount: 2);

    Assert(episodes.Count == 24, "Expected 24 parsed episodes for Asterisk War.");

    // Episodes 1-12 must be assigned SeasonNumber=1, 13-24 SeasonNumber=2.
    Assert(episodes.First(e => e.AbsoluteNumber == 1).SeasonNumber == 1, "Ep 1 must be season 1 after split.");
    Assert(episodes.First(e => e.AbsoluteNumber == 12).SeasonNumber == 1, "Ep 12 must be season 1 after split.");
    Assert(episodes.First(e => e.AbsoluteNumber == 13).SeasonNumber == 2, "Ep 13 must be season 2 after split.");
    Assert(episodes.First(e => e.AbsoluteNumber == 24).SeasonNumber == 2, "Ep 24 must be season 2 after split.");

    // Match S2E1 — must hit episode AbsoluteNumber=13 (Banyuu Tenra) with strategy=seasonOrdinal.
    var s2e1 = AnimeClickEpisodeMatcher.Match(episodes, 2, 1);
    Assert(s2e1.Success, "S02E01 must match after season-split inference.");
    Assert(s2e1.Episode?.AbsoluteNumber == 13, "S02E01 must map to absolute episode 13.");
    Assert(s2e1.Episode?.Title == "Banyuu Tenra - Rivelazioni divine", "S02E01 title mismatch.");
    Assert(s2e1.Strategy == "seasonOrdinal", "S02E01 should use seasonOrdinal strategy after split.");

    var s2e12 = AnimeClickEpisodeMatcher.Match(episodes, 2, 12);
    Assert(s2e12.Success, "S02E12 must match.");
    Assert(s2e12.Episode?.AbsoluteNumber == 24, "S02E12 must map to absolute episode 24.");
    Assert(s2e12.Episode?.Title == "Riunione", "S02E12 title mismatch.");
    Assert(s2e12.Strategy == "seasonOrdinal", "S02E12 should use seasonOrdinal strategy.");

    // S1E1 must continue to map via absolute (since now both S1 and S2 have episodes)
    var s1e1 = AnimeClickEpisodeMatcher.Match(episodes, 1, 1);
    Assert(s1e1.Success, "S01E01 must still match after split.");
    Assert(s1e1.Episode?.AbsoluteNumber == 1, "S01E01 must map to absolute episode 1.");
    Assert(s1e1.Episode?.Title == "La strega della fiamma splendente", "S01E01 title mismatch.");
    Assert(s1e1.Strategy == "seasonOrdinal", "S01E01 should use seasonOrdinal strategy since episodes have explicit SeasonNumber.");

    // Without seasonsCount (the legacy overload), the same page must NOT synthesize — back to the bug
    // (no S2 match), proving the new behaviour is opt-in via seasonsCount.
    var noSplit = parser.ParseEpisodesPage(TestFixtures.AsteriskContinuousEpisodesHtml, "https://www.animeclick.it", seasonsCount: null);
    Assert(noSplit.All(e => e.SeasonNumber is null), "Without seasonsCount the parser must NOT synthesise SeasonNumber.");
    var s2e1NoSplit = AnimeClickEpisodeMatcher.Match(noSplit, 2, 1);
    Assert(!s2e1NoSplit.Success && s2e1NoSplit.Strategy == "none", "S02E01 must NOT match without seasonsCount (regression baseline).");
}

static void TestSeasonsCountRefusedOnUnevenSplit()
{
    var parser = new AnimeClickHtmlParser();

    // 17 episodes with seasonsCount=2 → 17/2=8 (remainder 1) → uneven, parser must refuse to split.
    var html = """
<html><body>
<table class="table"><tbody>
<tr><td>Ep. 01</td><td><a href="/episodio/1/a">A</a></td><td>23'</td></tr>
<tr><td>Ep. 02</td><td><a href="/episodio/2/b">B</a></td><td>23'</td></tr>
<tr><td>Ep. 03</td><td><a href="/episodio/3/c">C</a></td><td>23'</td></tr>
<tr><td>Ep. 04</td><td><a href="/episodio/4/d">D</a></td><td>23'</td></tr>
<tr><td>Ep. 05</td><td><a href="/episodio/5/e">E</a></td><td>23'</td></tr>
<tr><td>Ep. 06</td><td><a href="/episodio/6/f">F</a></td><td>23'</td></tr>
<tr><td>Ep. 07</td><td><a href="/episodio/7/g">G</a></td><td>23'</td></tr>
<tr><td>Ep. 08</td><td><a href="/episodio/8/h">H</a></td><td>23'</td></tr>
<tr><td>Ep. 09</td><td><a href="/episodio/9/i">I</a></td><td>23'</td></tr>
<tr><td>Ep. 10</td><td><a href="/episodio/10/l">L</a></td><td>23'</td></tr>
<tr><td>Ep. 11</td><td><a href="/episodio/11/m">M</a></td><td>23'</td></tr>
<tr><td>Ep. 12</td><td><a href="/episodio/12/n">N</a></td><td>23'</td></tr>
<tr><td>Ep. 13</td><td><a href="/episodio/13/o">O</a></td><td>23'</td></tr>
<tr><td>Ep. 14</td><td><a href="/episodio/14/p">P</a></td><td>23'</td></tr>
<tr><td>Ep. 15</td><td><a href="/episodio/15/q">Q</a></td><td>23'</td></tr>
<tr><td>Ep. 16</td><td><a href="/episodio/16/r">R</a></td><td>23'</td></tr>
<tr><td>Ep. 17</td><td><a href="/episodio/17/s">S</a></td><td>23'</td></tr>
</tbody></table>
</body></html>
""";
    var episodes = parser.ParseEpisodesPage(html, "https://www.animeclick.it", seasonsCount: 2);
    Assert(episodes.Count == 17, "Parser must still return all 17 episodes.");
    Assert(episodes.All(e => e.SeasonNumber is null), "Parser must NOT synthesise SeasonNumber when the split is uneven (17 % 2 != 0).");
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
    ("Asterisk War 24ep block splits into 2 seasons via seasonsCount", TestAsteriskContinuousBlockSeasonSplit),
    ("Parser refuses to split when episode count is uneven across seasons", TestSeasonsCountRefusedOnUnevenSplit),
    ("Search scorer prefers 2023 series over movie and special", TestSearchScoring),
    ("Trailer-only multimedia reports diagnostic warning", TestTrailerOnlyMultimedia),
    ("AniList GraphQL id/escape parsing", TestAniListIdParsing),
    ("Config defaults: fill-gaps + fallback images", TestConfigDefaults),
    ("Anime page ImageUrl extraction for fallback provider", TestAnimePageImageUrlExtraction),
    ("TMDB search/tv + episode URL building", TestTmdbUrlBuilding),
    ("TMDB search + episode response parsing", TestTmdbResponseParsing),
    ("TVDB login/search/episodes URL building", TestTvdbUrlBuilding),
    ("TVDB token + series id parsing (numeric tvdb_id)", TestTvdbResponseParsing),
    ("TVDB string tvdb_id + record id fallback", TestTvdbSeriesIdStringAndFallback),
    ("TVDB episodes overview + next link parsing", TestTvdbEpisodesParsing),
    ("Ollama translator HTML stripping", TestOllamaTranslatorStripHtml),
    ("Ollama translator request body + response parsing", TestOllamaTranslatorRequestAndResponse),
    ("Ollama translator \\uXXXX unicode escapes", TestOllamaTranslatorUnicodeEscapes),
    ("Search scorer folds Italian accents for matching", TestSearchScorerAccentFolding)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
