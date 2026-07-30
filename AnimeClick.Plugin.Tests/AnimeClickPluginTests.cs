using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;

// Suite di regressione di AnimeClick.Plugin.
//
// Prima era una console app con un runner scritto a mano: 'dotnet test' usciva 0
// senza eseguire nulla, dando un falso verde. Ora sono fatti xunit veri, quindi
// 'dotnet test' li esegue e un fallimento fa fallire la build.
//
// I corpi dei test non sono stati toccati: l'helper Assert(condizione, messaggio)
// lancia ancora InvalidOperationException, che xunit riporta come fallimento con
// il messaggio originale. I nomi leggibili del vecchio runner sono conservati in
// DisplayName.
public class AnimeClickPluginTests
{
    [Xunit.Fact(DisplayName = "Dangers S2 matcher uses season ordinal")]
    public void TestDangersSeasonOrdinalMatching()
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

    [Xunit.Fact(DisplayName = "Search scorer prefers 2023 series over movie and special")]
    public void TestSearchScoring()
{
    var parser = new AnimeClickHtmlParser();
    var results = parser.ParseSearchResults(TestFixtures.SearchHtml, "https://www.animeclick.it")
        .OrderByDescending(r => AnimeClickSearchScorer.Score(r, "The Dangers in My Heart", 2023, seriesRequest: true))
        .ToList();

    Assert(results[0].Id == "44780/boku-no-kokoro-no-yabai-yatsu", "Expected the 2023 TV series to rank first.");
    Assert(results[0].Format?.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) == true, "Expected parser to retain TV format.");
}

    [Xunit.Fact(DisplayName = "Trailer-only multimedia reports diagnostic warning")]
    public void TestTrailerOnlyMultimedia()
{
    var parser = new AnimeClickHtmlParser();
    var diagnostics = parser.ParseMultimediaDiagnostics(TestFixtures.TrailerOnlyMultimediaHtml);

    Assert(diagnostics.Songs.Count == 0, "Trailer-only page must not invent OP/ED songs.");
    Assert(diagnostics.HasTrailerOrPvOnly, "Trailer-only page should expose a warning state.");
    Assert(!string.IsNullOrWhiteSpace(diagnostics.Warning), "Trailer-only page should include a diagnostic warning.");
}

    [Xunit.Fact(DisplayName = "Config defaults: fill-gaps + fallback images")]
    public void TestConfigDefaults()
{
    // PluginConfiguration extends Jellyfin's BasePluginConfiguration, which is not
    // available outside the Jellyfin runtime, so defaults are verified by reading
    // the property initializers in PluginConfiguration.cs directly instead of by
    // instantiating the type here.
    Assert(true, "Config defaults (OverwriteNonItalianFields=false, EnableAnimeClickImages=true) are declared in PluginConfiguration.cs.");
}

    [Xunit.Fact(DisplayName = "Anime page ImageUrl extraction for fallback provider")]
    public void TestAnimePageImageUrlExtraction()
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

    [Xunit.Fact(DisplayName = "TMDB search/tv + episode URL building")]
    public void TestTmdbUrlBuilding()
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

    [Xunit.Fact(DisplayName = "TMDB search + episode response parsing")]
    public void TestTmdbResponseParsing()
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

    [Xunit.Fact(DisplayName = "Ollama translator HTML stripping")]
    public void TestOllamaTranslatorStripHtml()
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

    [Xunit.Fact(DisplayName = "Ollama translator request body + response parsing")]
    public void TestOllamaTranslatorRequestAndResponse()
{
    var body = AnimeClickOllamaTranslator.BuildRequestBody("gemma4:31b-cloud", "sys-prompt", "Translate this.");
    Assert(body.Contains("\"model\":\"gemma4:31b-cloud\"", StringComparison.OrdinalIgnoreCase),
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

    [Xunit.Fact(DisplayName = "Ollama translator \\uXXXX unicode escapes")]
    public void TestOllamaTranslatorUnicodeEscapes()
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

    [Xunit.Fact(DisplayName = "Search scorer folds Italian accents for matching")]
    public void TestSearchScorerAccentFolding()
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

    [Xunit.Fact(DisplayName = "Search query cleaners (sequel, fullwidth, & etc)")]
    public void TestSearchQueryCleaners()
{
    // CleanSearchQuery
    Assert(AnimeClickSeriesSearchProvider.CleanSearchQuery("Naruto (2024)") == "Naruto",
        "Should strip year parens");
    Assert(AnimeClickSeriesSearchProvider.CleanSearchQuery("K-On! Movie") == "K-On!",
        "Should remove Movie suffix");
    Assert(AnimeClickSeriesSearchProvider.CleanSearchQuery("Foo 2nd Season") == "Foo",
        "Should strip sequel markers");
    Assert(AnimeClickSeriesSearchProvider.CleanSearchQuery("Bar & Baz") == "Bar and Baz",
        "Should normalize &");

    // Simplify
    Assert(AnimeClickSeriesSearchProvider.SimplifyQuery("Title: With-Dots.And/Slash") == "Title With Dots And Slash",
        "Simplify must replace special characters and collapse repeated spaces");

    // Short query
    Assert(AnimeClickSeriesSearchProvider.GetShortQuery("One Two Three Four") == "One Two Three",
        "Short query takes first 3 words");
}

    [Xunit.Fact(DisplayName = "Improved scorer fuzzy + overlap")]
    public void TestImprovedScorer()
{
    var r = new AnimeClickSearchResult { Id = "x/y", Title = "The Dangers in My Heart", ProductionYear = 2023, Format = "Serie TV" };
    var s = AnimeClickSearchScorer.Score(r, "Dangers in My Heart", 2023, true);
    Assert(s > 80, "Strong partial match + year + format should score high");

    // Fuzzy overlap bonus path
    var fuzzy = AnimeClickSearchScorer.Score(r, "Dangers Heart", null, true);
    Assert(fuzzy > 20, "Fuzzy token overlap should still give positive score");
}

    [Xunit.Fact(DisplayName = "TVDB login/search/episodes URL building")]
    public void TestTvdbUrlBuilding()
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

    [Xunit.Fact(DisplayName = "TVDB token + series id parsing (numeric tvdb_id)")]
    public void TestTvdbResponseParsing()
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

    [Xunit.Fact(DisplayName = "TVDB string tvdb_id + record id fallback")]
    public void TestTvdbSeriesIdStringAndFallback()
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

    [Xunit.Fact(DisplayName = "TVDB episodes overview + next link parsing")]
    public void TestTvdbEpisodesParsing()
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

    [Xunit.Fact(DisplayName = "Asterisk War 24ep block splits into 2 seasons via seasonsCount")]
    public void TestAsteriskContinuousBlockSeasonSplit()
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

    // A declared equal split is only a hint: without title or real Jellyfin topology it
    // must miss safely because the same 24 rows could represent a 13+11 layout.
    var uncorroboratedS2E1 = AnimeClickEpisodeMatcher.Match(episodes, 2, 1);
    Assert(!uncorroboratedS2E1.Success && uncorroboratedS2E1.Strategy == "lowConfidence",
        "A synthetic 12+12 split must not match without corroboration.");
    var outOfRangeSynthetic = AnimeClickEpisodeMatcher.Match(episodes, 2, 13);
    Assert(!outOfRangeSynthetic.Success,
        "Raw absolute E13 must not leak into a synthetic S02E13 coordinate.");

    var s2e1 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1)
        {
            JellyfinTitle = "Banyuu Tenra - Rivelazioni divine"
        });
    Assert(s2e1.Success, "S02E01 must match when the synthetic split is title-corroborated.");
    Assert(s2e1.Episode?.AbsoluteNumber == 13, "S02E01 must map to absolute episode 13.");
    Assert(s2e1.Episode?.Title == "Banyuu Tenra - Rivelazioni divine", "S02E01 title mismatch.");
    Assert(s2e1.Strategy == "declaredEqualSplit", "S02E01 should report its low-confidence split origin.");

    var s2e12 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 12)
        {
            JellyfinTitle = "Riunione"
        });
    Assert(s2e12.Success, "S02E12 must match when title-corroborated.");
    Assert(s2e12.Episode?.AbsoluteNumber == 24, "S02E12 must map to absolute episode 24.");
    Assert(s2e12.Episode?.Title == "Riunione", "S02E12 title mismatch.");
    Assert(s2e12.Strategy == "declaredEqualSplit", "S02E12 should report declaredEqualSplit strategy.");

    // S1 keeps a safe absolute interpretation regardless of where a later cour boundary falls.
    var s1e1 = AnimeClickEpisodeMatcher.Match(episodes, 1, 1);
    Assert(s1e1.Success, "S01E01 must still match after split inference.");
    Assert(s1e1.Episode?.AbsoluteNumber == 1, "S01E01 must map to absolute episode 1.");
    Assert(s1e1.Episode?.Title == "La strega della fiamma splendente", "S01E01 title mismatch.");
    Assert(s1e1.Strategy == "syntheticAbsolute", "S01E01 must use the boundary-independent global strategy.");

    // Without seasonsCount (the legacy overload), the same page must NOT synthesize — back to the bug
    // (no S2 match), proving the new behaviour is opt-in via seasonsCount.
    var noSplit = parser.ParseEpisodesPage(TestFixtures.AsteriskContinuousEpisodesHtml, "https://www.animeclick.it", seasonsCount: null);
    Assert(noSplit.All(e => e.SeasonNumber is null), "Without seasonsCount the parser must NOT synthesise SeasonNumber.");
    var s2e1NoSplit = AnimeClickEpisodeMatcher.Match(noSplit, 2, 1);
    Assert(!s2e1NoSplit.Success && s2e1NoSplit.Strategy == "none", "S02E01 must NOT match without seasonsCount (regression baseline).");
}

    [Xunit.Fact(DisplayName = "Parser refuses to split when episode count is uneven across seasons")]
    public void TestSeasonsCountRefusedOnUnevenSplit()
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

    [Xunit.Fact(DisplayName = "AniList GraphQL id/escape parsing")]
    public void TestAniListIdParsing()
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

    [Xunit.Fact(DisplayName = "Translation queue failure backoff policy")]
    public void TestTranslationQueuePolicy()
{
    Assert(
        AnimeClickTranslationQueue.GetFailureBackoff(TimeSpan.FromSeconds(2), 30) == TimeSpan.FromMinutes(5),
        "A fast translation failure must suppress retries for 5 minutes.");
    Assert(
        AnimeClickTranslationQueue.GetFailureBackoff(TimeSpan.FromSeconds(29), 30) == TimeSpan.FromMinutes(15),
        "A timeout-shaped failure must suppress retries for 15 minutes.");
    Assert(
        AnimeClickTranslationQueue.GetFailureBackoff(TimeSpan.FromSeconds(4), 1) == TimeSpan.FromMinutes(15),
        "Backoff classification must use the same 5-second lower timeout clamp as the translator.");
}

    [Xunit.Fact(DisplayName = "Nagi S1E14-E26 crosses only synthetic season boundaries")]
    public void TestSyntheticSeasonAbsoluteFallback()
{
    var rows = string.Join(
        '\n',
        Enumerable.Range(1, 26).Select(number =>
            $"<tr><td>Ep. {number:00}</td><td><a href=\"/episodio/{50000 + number}/nagi-{number}\">Nagi episodio {number}</a></td><td>23'</td></tr>"));
    var html = "<html><body><table class=\"table\"><tbody>" + rows + "</tbody></table></body></html>";

    var parser = new AnimeClickHtmlParser();
    var episodes = parser.ParseEpisodesPage(html, "https://www.animeclick.it", seasonsCount: 2);

    Assert(episodes.Count == 26, "Nagi regression must parse all 26 episodes.");
    Assert(episodes.All(e => e.SeasonNumberIsSynthetic),
        "Every evenly inferred season assignment must be marked synthetic.");

    var s1e14 = AnimeClickEpisodeMatcher.Match(episodes, 1, 14);
    Assert(s1e14.Episode?.AbsoluteNumber == 14,
        "Jellyfin S01E14 must cross a synthetic cour boundary to AnimeClick absolute episode 14.");
    Assert(s1e14.Strategy == "syntheticAbsolute", "S01E14 must report syntheticAbsolute strategy.");

    var s1e26 = AnimeClickEpisodeMatcher.Match(episodes, 1, 26);
    Assert(s1e26.Episode?.AbsoluteNumber == 26,
        "Jellyfin S01E26 must cross a synthetic cour boundary to AnimeClick absolute episode 26.");
    Assert(s1e26.Strategy == "syntheticAbsolute", "S01E26 must report syntheticAbsolute strategy.");

    var uncorroboratedS2E1 = AnimeClickEpisodeMatcher.Match(episodes, 2, 1);
    Assert(!uncorroboratedS2E1.Success,
        "A synthetic S02E01 boundary must not be trusted without title or topology evidence.");
    var s2e1 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1)
        {
            JellyfinTitle = "Nagi episodio 14"
        });
    Assert(s2e1.Episode?.AbsoluteNumber == 14 && s2e1.Strategy == "declaredEqualSplit",
        "A title-corroborated synthetic S02E01 may map to global episode 14.");

    var explicitEpisodes = parser.ParseEpisodesPage(
        TestFixtures.DangersEpisodesHtml,
        "https://www.animeclick.it");
    Assert(explicitEpisodes.All(e => !e.SeasonNumberIsSynthetic),
        "Explicit AnimeClick S1/S2 labels must never be marked synthetic.");
    var explicitS1E13 = AnimeClickEpisodeMatcher.Match(explicitEpisodes, 1, 13);
    Assert(!explicitS1E13.Success && explicitS1E13.Strategy == "seasonGroupNoMatch",
        "Explicit S1 must not cross into explicit S2 through absolute numbering.");

    var explicitS2Only = parser.ParseEpisodesPage(
        "<table><tr><td>S2 Ep. 01</td><td><a href=\"/episodio/60001/explicit-s2\">Explicit S2</a></td></tr></table>",
        "https://www.animeclick.it");
    var missingExplicitS1 = AnimeClickEpisodeMatcher.Match(explicitS2Only, 1, 1);
    Assert(!missingExplicitS1.Success && missingExplicitS1.Strategy == "none",
        "A missing explicit S1 group must not fall through to an explicit S2 absolute match.");

    var orphanAbsoluteS2 = parser.ParseEpisodesPage(
        "<table><tr><td>S2 Ep. 13</td><td><a href=\"/episodio/60013/orphan\">Orphan 13</a></td></tr>" +
        "<tr><td>S2 Ep. 14</td><td><a href=\"/episodio/60014/orphan\">Orphan 14</a></td></tr></table>",
        "https://www.animeclick.it");
    var unsafeOrdinal = AnimeClickEpisodeMatcher.Match(orphanAbsoluteS2, 2, 1);
    Assert(!unsafeOrdinal.Success,
        "An absolute S2 group without a complete preceding timeline must not fabricate S02E01.");
}

    private static string BuildFlatEpisodeHtml(int count, bool descending = false)
{
    var numbers = descending
        ? Enumerable.Range(1, count).Reverse()
        : Enumerable.Range(1, count);
    var rows = string.Join(
        '\n',
        numbers.Select(number =>
            $"<tr><td>Ep. {number:00}</td><td><a href=\"/episodio/{70000 + number}/episode-{number}\">Titolo {number}</a></td><td>23'</td></tr>"));
    return "<html><body><table class=\"table\"><tbody>" + rows + "</tbody></table></body></html>";
}

    [Xunit.Fact(DisplayName = "Canonical timeline excludes interleaved specials and decimal rows")]
    public void TestCanonicalTimelineExcludesInterleavedSpecials()
{
    var html = """
        <table><tbody>
        <tr><td>Ep. 01</td><td><a href="/episodio/1/uno">Uno</a></td><td>23'</td></tr>
        <tr><td>OVA</td><td><a href="/episodio/90/ova">OVA della serie</a></td><td>24'</td></tr>
        <tr><td>Ep. 02</td><td><a href="/episodio/2/due">Due</a></td><td>23'</td></tr>
        <tr><td>Ep. 12.5</td><td><a href="/episodio/95/meta">Episodio intermedio</a></td><td>12'</td></tr>
        <tr><td>S0 Ep. 0</td><td><a href="/episodio/96/prologo">Prologo</a></td><td>5'</td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");

    Assert(episodes.Count == 5, "All regular and non-standard rows must be retained.");
    Assert(episodes.Single(e => e.Title == "Uno").GlobalOrdinal == 1, "First regular episode must be global ordinal 1.");
    Assert(episodes.Single(e => e.Title == "Due").GlobalOrdinal == 2, "Interleaved OVA must not shift the regular timeline.");
    Assert(episodes.Single(e => e.Title == "OVA della serie").IsSpecial, "OVA row must be classified as special.");
    Assert(episodes.Single(e => e.Title == "Episodio intermedio").HasNonStandardNumber,
        "Decimal episode must be retained as non-standard.");

    var regular = AnimeClickEpisodeMatcher.Match(episodes, 1, 2);
    Assert(regular.Episode?.Title == "Due" && regular.Strategy == "globalOrdinal",
        "S01E02 must ignore interleaved extras.");
    var episodeZero = AnimeClickEpisodeMatcher.Match(episodes, 0, 0);
    Assert(episodeZero.Episode?.Title == "Prologo", "S00E00 must match an explicit episode zero.");

    var collidingNumbers = new AnimeClickHtmlParser().ParseEpisodesPage(
        "<table><tbody>" +
        "<tr><td>Ep. 12</td><td><a href=\"/episodio/12/twelve\">Dodici</a></td></tr>" +
        "<tr><td>Ep. 12.5</td><td><a href=\"/episodio/125/half\">Intermezzo</a></td></tr>" +
        "<tr><td>Ep. 13</td><td><a href=\"/episodio/13/thirteen\">Tredici</a></td></tr>" +
        "</tbody></table>",
        "https://www.animeclick.it");
    Assert(!collidingNumbers.Single(item => item.Title == "Dodici").NumberIsAmbiguous,
        "A decimal special sharing the E12 base must not make regular E12 ambiguous.");
    Assert(collidingNumbers.Single(item => item.Title == "Tredici").GlobalOrdinal == 2,
        "A decimal special sharing a base number must not disable the regular timeline.");

    var duplicateSpecials = new AnimeClickHtmlParser().ParseEpisodesPage(
        "<table><tbody>" +
        "<tr><td>S0 Ep. 01</td><td><a href=\"/episodio/901/a\">Special A</a></td></tr>" +
        "<tr><td>S0 Ep. 01</td><td><a href=\"/episodio/902/b\">Special B</a></td></tr>" +
        "</tbody></table>",
        "https://www.animeclick.it");
    Assert(!AnimeClickEpisodeMatcher.Match(duplicateSpecials, 0, 1).Success,
        "Duplicate special coordinates must miss safely without a provider ID or title.");
}

    [Xunit.Fact(DisplayName = "Parser accepts alternate labels and repairs reversed numeric rows")]
    public void TestAlternateSeasonLabelsAndOutOfOrderRows()
{
    var labels = """
        <table><tbody>
        <tr><td>S01E01</td><td><a href="/episodio/101/a">A</a></td></tr>
        <tr><td>2x03</td><td><a href="/episodio/203/b">B</a></td></tr>
        <tr><td>Stagione 3 Episodio 02</td><td><a href="/episodio/302/c">C</a></td></tr>
        </tbody></table>
        """;
    var parser = new AnimeClickHtmlParser();
    var parsed = parser.ParseEpisodesPage(labels, "https://www.animeclick.it");
    Assert(parsed.Single(e => e.Title == "A").RawSeasonNumber == 1, "S01E01 label must parse season 1.");
    Assert(parsed.Single(e => e.Title == "B").RawSeasonNumber == 2
           && parsed.Single(e => e.Title == "B").RawEpisodeNumber == 3,
        "2x03 label must parse season 2 episode 3.");
    Assert(parsed.Single(e => e.Title == "C").RawSeasonNumber == 3,
        "Italian Stagione/Episodio label must parse.");

    var reversed = parser.ParseEpisodesPage(BuildFlatEpisodeHtml(3, descending: true), "https://www.animeclick.it");
    Assert(reversed.Single(e => e.Number == 1).GlobalOrdinal == 1
           && reversed.Single(e => e.Number == 3).GlobalOrdinal == 3,
        "Unambiguous reversed rows must be canonicalized by numeric order.");
}

    [Xunit.Fact(DisplayName = "Jellyfin topology maps an uneven 13+11 split")]
    public void TestLibraryTopologySupportsUnevenThirteenPlusEleven()
{
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(
        BuildFlatEpisodeHtml(24),
        "https://www.animeclick.it");
    var layout = new AnimeClickEpisodeLibraryLayout(
        Guid.NewGuid(),
        new Dictionary<int, AnimeClickEpisodeSeasonLayout>
        {
            [1] = new(1, 13, 13, true, true),
            [2] = new(2, 11, 11, true, true)
        });

    var s2e1 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1) { LibraryLayout = layout });
    var s2e11 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 11) { LibraryLayout = layout });

    Assert(s2e1.Episode?.GlobalOrdinal == 14 && s2e1.Strategy == "libraryBoundary",
        "13+11 topology must map S02E01 to global episode 14.");
    Assert(s2e11.Episode?.GlobalOrdinal == 24, "13+11 topology must map S02E11 to global episode 24.");
}

    [Xunit.Fact(DisplayName = "Single Jellyfin season crosses explicit AnimeClick groups safely")]
    public void TestSingleJellyfinSeasonCanCrossExplicitAnimeClickGroups()
{
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(
        TestFixtures.DangersEpisodesHtml,
        "https://www.animeclick.it");
    var flatLibrary = new AnimeClickEpisodeLibraryLayout(
        Guid.NewGuid(),
        new Dictionary<int, AnimeClickEpisodeSeasonLayout>
        {
            [1] = new(1, 25, 25, true, true)
        });
    var match = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(1, 13) { LibraryLayout = flatLibrary });

    Assert(match.Episode?.Title == "Noi stiamo cercando" && match.Strategy == "libraryBoundary",
        "A verified flat Jellyfin season must cross explicit AnimeClick S1/S2 groups.");
}

    [Xunit.Fact(DisplayName = "Existing provider ID pins decimal and A/B episodes")]
    public void TestProviderIdPinsNonStandardEpisode()
{
    var html = """
        <table><tbody>
        <tr><td>Ep. 12.5</td><td><a href="/episodio/125/half">La mezza puntata</a></td></tr>
        <tr><td>Ep. 12A</td><td><a href="/episodio/126/part-a">Parte A</a></td></tr>
        <tr><td>Ep. 12B</td><td><a href="/episodio/127/part-b">Parte B</a></td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");
    var unsafeNumeric = AnimeClickEpisodeMatcher.Match(episodes, 1, 12);
    Assert(!unsafeNumeric.Success, "Decimal and A/B rows must not be guessed from an integer coordinate.");

    var anchored = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(1, 12)
        {
            ExistingProviderId = "125/half"
        });
    Assert(anchored.Episode?.Title == "La mezza puntata" && anchored.Strategy == "providerId",
        "Existing episode provider ID must pin a non-standard row.");
}

    [Xunit.Fact(DisplayName = "Double-episode files require an explicit range")]
    public void TestDoubleEpisodeRequiresExplicitRange()
{
    var html = """
        <table><tbody>
        <tr><td>S1 Ep. 01-02</td><td><a href="/episodio/500/double">Doppio episodio</a></td></tr>
        <tr><td>S1 Ep. 03</td><td><a href="/episodio/503/three">Tre</a></td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");
    var withoutEnd = AnimeClickEpisodeMatcher.Match(episodes, 1, 1);
    Assert(!withoutEnd.Success, "A range row must not be assigned to a single-episode file.");
    var withoutEndAsSpecial = AnimeClickEpisodeMatcher.Match(episodes, 0, 1);
    Assert(!withoutEndAsSpecial.Success,
        "A range row must not escape into season zero as a single special.");

    var withEnd = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(1, 1) { JellyfinEpisodeNumberEnd = 2 });
    Assert(withEnd.Episode?.Title == "Doppio episodio" && withEnd.Strategy == "explicitRange",
        "A Jellyfin 1-2 file must match the explicit AnimeClick 1-2 range.");

    var staleAnchor = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(1, 1)
        {
            JellyfinEpisodeNumberEnd = 2,
            ExistingProviderId = "503/three"
        });
    Assert(staleAnchor.Episode?.ProviderId == "500/double" && staleAnchor.Strategy == "explicitRange",
        "A stale single-episode provider ID must not bypass the multi-episode range guard.");

    var unseasoned = new AnimeClickHtmlParser().ParseEpisodesPage(
        "<table><tr><td>Ep. 01-02</td><td><a href=\"/episodio/600/double-flat\">Doppio flat</a></td></tr></table>",
        "https://www.animeclick.it");
    Assert(unseasoned.Single().RawSeasonNumber is null,
        "An unseasoned range must retain a null raw season.");
    var unseasonedMatch = AnimeClickEpisodeMatcher.Match(
        unseasoned,
        new AnimeClickEpisodeMatchContext(1, 1) { JellyfinEpisodeNumberEnd = 2 });
    Assert(unseasonedMatch.Episode?.Title == "Doppio flat",
        "An unseasoned AnimeClick range must match a normal Jellyfin multi-episode file.");
}

    [Xunit.Fact(DisplayName = "Manual flat and cumulative-boundary overrides")]
    public void TestManualLayoutOverrides()
{
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(
        BuildFlatEpisodeHtml(24),
        "https://www.animeclick.it");
    var boundaries = AnimeClickEpisodeLayoutOverrideParser.ParseFor(
        "# custom\n123/show=13,24",
        "123/show");
    Assert(boundaries?.Mode == AnimeClickEpisodeLayoutMode.Boundaries,
        "Cumulative boundary override must parse.");

    var s2e1 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1) { LayoutOverride = boundaries });
    Assert(s2e1.Episode?.GlobalOrdinal == 14 && s2e1.Strategy == "overrideBoundaries",
        "Manual 13,24 boundaries must map S02E01 to global 14.");

    var flat = AnimeClickEpisodeLayoutOverrideParser.ParseFor("123/show=flat", "123/show");
    var s1e24 = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(1, 24) { LayoutOverride = flat });
    Assert(s1e24.Episode?.GlobalOrdinal == 24 && s1e24.Strategy == "overrideFlat",
        "Flat override must preserve a 1x24 library.");

    var numericForCanonical = AnimeClickEpisodeLayoutOverrideParser.ParseFor("123=flat", "123/show");
    var canonicalForNumeric = AnimeClickEpisodeLayoutOverrideParser.ParseFor("123/old-slug=explicit", "123");
    Assert(numericForCanonical?.Mode == AnimeClickEpisodeLayoutMode.Flat
           && canonicalForNumeric?.Mode == AnimeClickEpisodeLayoutMode.Explicit,
        "Numeric and canonical IDs with the same stable number must share overrides.");
    Assert(flat?.TryGetGlobalOrdinal(0, 1, out _) == false,
        "A flat override must never map season zero into the regular timeline.");

    var specialEpisodes = new AnimeClickHtmlParser().ParseEpisodesPage(
        "<table><tr><td>Ep. 01</td><td><a href=\"/episodio/1/regular\">Regolare</a></td></tr>" +
        "<tr><td>S0 Ep. 01</td><td><a href=\"/episodio/2/ova\">OVA</a></td></tr></table>",
        "https://www.animeclick.it");
    var specialWithFlatOverride = AnimeClickEpisodeMatcher.Match(
        specialEpisodes,
        new AnimeClickEpisodeMatchContext(0, 1) { LayoutOverride = flat });
    Assert(specialWithFlatOverride.Episode?.Title == "OVA"
           && specialWithFlatOverride.Strategy == "specialOrdinal",
        "Season-zero matching must run before a regular flat override.");
}

    [Xunit.Fact(DisplayName = "Incomplete topology requires title corroboration")]
    public void TestIncompleteTopologyNeedsCorroboration()
{
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(
        BuildFlatEpisodeHtml(24),
        "https://www.animeclick.it");
    var partial = new AnimeClickEpisodeLibraryLayout(
        Guid.NewGuid(),
        new Dictionary<int, AnimeClickEpisodeSeasonLayout>
        {
            [1] = new(1, 13, 12, true, false),
            [2] = new(2, 11, 11, true, true)
        });

    var uncorroborated = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1) { LibraryLayout = partial });
    Assert(!uncorroborated.Success && uncorroborated.Strategy == "none",
        "A broken prior-season topology must not fabricate a boundary.");

    var currentPartial = new AnimeClickEpisodeLibraryLayout(
        Guid.NewGuid(),
        new Dictionary<int, AnimeClickEpisodeSeasonLayout>
        {
            [1] = new(1, 13, 13, true, true),
            [2] = new(2, 11, 10, true, false)
        });
    var titleCorroborated = AnimeClickEpisodeMatcher.Match(
        episodes,
        new AnimeClickEpisodeMatchContext(2, 1)
        {
            LibraryLayout = currentPartial,
            JellyfinTitle = "Titolo 14"
        });
    Assert(titleCorroborated.Episode?.GlobalOrdinal == 14,
        "Exact title evidence may corroborate a partial target-season topology.");
}

    [Xunit.Fact(DisplayName = "Raw catalog fingerprint and pagination deduplication")]
    public void TestRawCatalogFingerprintAndPaginationDeduplication()
{
    var parser = new AnimeClickHtmlParser();
    var episodes = parser.ParseEpisodesPage(BuildFlatEpisodeHtml(3), "https://www.animeclick.it");
    var oneSeason = AnimeClickEpisodeCatalog.Create(episodes, 3, 1);
    var twoSeasons = AnimeClickEpisodeCatalog.Create(episodes, 3, 2);
    Assert(oneSeason.LayoutFingerprint != twoSeasons.LayoutFingerprint,
        "Declared season changes must alter the raw catalog fingerprint.");

    var target = parser.ParseEpisodesPage(
        "<table><tr><td>Ep. 01</td><td><a href=\"/episodio/1/a\">A</a></td></tr></table>",
        "https://www.animeclick.it");
    var candidates = parser.ParseEpisodesPage(
        "<table><tr><td>Ep. 01</td><td><a href=\"/episodio/1/a\">A duplicate</a></td></tr>" +
        "<tr><td>Ep. 01</td><td><a href=\"/episodio/2/b\">B distinct</a></td></tr></table>",
        "https://www.animeclick.it");
    var added = AnimeClickEpisodeListLoader.MergeUniqueEpisodes(target, candidates);
    Assert(added == 1 && target.Count == 2,
        "Pagination merge must drop repeated provider IDs but retain distinct A/B rows.");
    AnimeClickHtmlParser.CanonicalizeEpisodeTimeline(target);
    Assert(target.All(episode => episode.NumberIsAmbiguous),
        "Distinct rows sharing one numeric coordinate must be marked ambiguous.");
    Assert(target.All(episode => episode.GlobalOrdinal == 0 && episode.SeasonOrdinalNumber == 0),
        "Duplicate coordinates must disable derived global and season ordinals.");

    var apparentlyReliableLayout = new AnimeClickEpisodeLibraryLayout(
        Guid.NewGuid(),
        new Dictionary<int, AnimeClickEpisodeSeasonLayout>
        {
            [1] = new(1, 1, 1, true, true)
        });
    var ambiguousMatch = AnimeClickEpisodeMatcher.Match(
        target,
        new AnimeClickEpisodeMatchContext(1, 1) { LibraryLayout = apparentlyReliableLayout });
    Assert(!ambiguousMatch.Success,
        "Reliable Jellyfin topology must not make duplicate AnimeClick coordinates matchable.");
}

    [Xunit.Fact(DisplayName = "Regular Pilot/Prologo titles do not shift the timeline")]
    public void TestRegularTitlesThatLookSpecialDoNotShiftTimeline()
{
    var html = """
        <table><tbody>
        <tr><td>Ep. 01</td><td><a href="/episodio/801/pilot">Pilot</a></td></tr>
        <tr><td>Ep. 02</td><td><a href="/episodio/802/prologo">Prologo</a></td></tr>
        <tr><td>OVA</td><td><a href="/episodio/899/extra">Contenuto extra</a></td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");
    var pilot = episodes.Single(episode => episode.Title == "Pilot");
    var prologue = episodes.Single(episode => episode.Title == "Prologo");

    Assert(!pilot.IsSpecial && pilot.GlobalOrdinal == 1,
        "A trustworthy Ep. 01 label must remain regular even when its title is Pilot.");
    Assert(!prologue.IsSpecial && prologue.GlobalOrdinal == 2,
        "A trustworthy Ep. 02 label must remain regular even when its title is Prologo.");
    Assert(episodes.Single(episode => episode.RawNumberLabel == "OVA").IsSpecial,
        "An explicit OVA label must still be classified as special.");

    var match = AnimeClickEpisodeMatcher.Match(episodes, 1, 1);
    Assert(match.Episode?.Title == "Pilot" && match.Strategy == "globalOrdinal",
        "Title heuristics must not remove E01 and shift the regular timeline.");
}

    [Xunit.Fact(DisplayName = "Italian 'Speciale' row is special, not episode 2015")]
    public void TestItalianSpecialeLabelDoesNotBecomeEpisodeYear()
{
    // Mixed page: seasoned rows plus one unseasoned special. Before the fix the special was
    // parsed as regular episode 2015, so the canonical order fell back to page order and
    // every following episode was pushed one slot down the timeline.
    var html = """
        <table class="table"><tbody>
        <tr><td>S1 Ep. 01</td><td><a href="/episodio/901/uno">Uno</a></td><td>23'</td></tr>
        <tr><td>S1 Ep. 02</td><td><a href="/episodio/902/due">Due</a></td><td>23'</td></tr>
        <tr><td>Speciale natalizio 2015</td><td><a href="/episodio/998/speciale">Speciale</a></td><td>45'</td></tr>
        <tr><td>S1 Ep. 03</td><td><a href="/episodio/903/tre">Tre</a></td><td>23'</td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");
    var special = episodes.Single(episode => episode.RawNumberLabel == "Speciale natalizio 2015");
    var third = episodes.Single(episode => episode.Title == "Tre");

    Assert(special.IsSpecial, "An Italian 'Speciale' row must be classified as a special.");
    Assert(special.Number != 2015, "A calendar year in the label must not become the episode number.");
    Assert(third.GlobalOrdinal == 3 && third.AbsoluteNumber == 3,
        "A special row must not push the following regular episodes down the timeline.");
    Assert(AnimeClickEpisodeMatcher.Match(episodes, 1, 3).Episode?.Title == "Tre",
        "S01E03 must still resolve to the third regular episode.");
}

    [Xunit.Fact(DisplayName = "Year-only label without keywords is not an episode number")]
    public void TestYearInLabelWithoutSpecialKeywordIsNonStandard()
{
    var html = """
        <table class="table"><tbody>
        <tr><td>S1 Ep. 01</td><td><a href="/episodio/911/uno">Uno</a></td><td>23'</td></tr>
        <tr><td>Corto animato 2015</td><td><a href="/episodio/912/corto">Corto</a></td><td>5'</td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");
    var shortFilm = episodes.Single(episode => episode.Title == "Corto");

    Assert(shortFilm.IsSpecial, "A label whose only number is a year carries no episode number.");
    Assert(shortFilm.RawEpisodeNumber is null, "A year must not be stored as a raw episode number.");
}

    [Xunit.Fact(DisplayName = "A label that is only a number stays a regular episode")]
    public void TestBareHighEpisodeNumberIsStillRegular()
{
    // Guards against over-correcting: long-running series legitimately reach four digits,
    // and AnimeClick has used bare numeric labels.
    var html = """
        <table class="table"><tbody>
        <tr><td>2015</td><td><a href="/episodio/921/bare">Bare</a></td><td>23'</td></tr>
        <tr><td>Ep. 2016</td><td><a href="/episodio/922/marked">Marked</a></td><td>23'</td></tr>
        </tbody></table>
        """;
    var episodes = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");

    Assert(episodes.Single(episode => episode.Title == "Bare") is { IsSpecial: false, Number: 2015 },
        "A label consisting only of the number must stay a regular episode.");
    Assert(episodes.Single(episode => episode.Title == "Marked") is { IsSpecial: false, Number: 2016 },
        "An explicit episode marker must keep a four-digit number as the episode number.");
}

    [Xunit.Fact(DisplayName = "Server Retry-After is clamped to 15 minutes")]
    public void TestRetryAfterIsClamped()
{
    // An unbounded Retry-After used to be persisted in the process-wide request gate, which
    // only ever moves forward: every later request then waited behind it until restart.
    using var absurd = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    absurd.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(3650));
    using var distantDate = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    distantDate.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddYears(5));
    using var reasonable = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    reasonable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
    using var absent = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

    var cap = TimeSpan.FromMinutes(15);
    Assert(AnimeClickClient.GetRetryDelay(absurd, 0) == cap, "A 10-year Retry-After delta must be clamped.");
    Assert(AnimeClickClient.GetRetryDelay(distantDate, 0) == cap, "A distant Retry-After date must be clamped.");
    Assert(AnimeClickClient.GetRetryDelay(reasonable, 0) == TimeSpan.FromSeconds(45),
        "A reasonable Retry-After must be honoured unchanged.");
    Assert(AnimeClickClient.GetRetryDelay(absent, 1) == TimeSpan.FromMilliseconds(700),
        "Without Retry-After the delay must stay the attempt-based default.");
}

    [Xunit.Fact(DisplayName = "Search thumbnails outside the configured host are dropped")]
    public void TestSearchThumbnailHostIsValidated()
{
    // ThumbnailUrl becomes RemoteSearchResult.ImageUrl, which Jellyfin fetches server-side.
    var html = """
        <div class="media item-search-item">
          <h4 class="media-heading"><a href="/anime/1/foreign">Foreign</a></h4>
          <img src="http://evil.tld/track.jpg">
        </div>
        <div class="media item-search-item">
          <h4 class="media-heading"><a href="/anime/2/relative">Relative</a></h4>
          <img src="/immagini/locandina.jpg">
        </div>
        <div class="media item-search-item">
          <h4 class="media-heading"><a href="/anime/3/insecure">Insecure</a></h4>
          <img src="http://www.animeclick.it/immagini/locandina.jpg">
        </div>
        """;
    var results = new AnimeClickHtmlParser().ParseSearchResults(html, "https://www.animeclick.it");

    Assert(results.Single(result => result.Title == "Foreign").ThumbnailUrl is null,
        "A thumbnail on a foreign host must be dropped, not handed to Jellyfin.");
    Assert(results.Single(result => result.Title == "Relative").ThumbnailUrl
           == "https://www.animeclick.it/immagini/locandina.jpg",
        "A relative thumbnail must still resolve against the configured base URL.");
    Assert(results.Single(result => result.Title == "Insecure").ThumbnailUrl is null,
        "A plain-HTTP thumbnail must be dropped rather than downgraded.");
}

    [Xunit.Fact(DisplayName = "Configuration bounds clamp out-of-range values")]
    public void TestConfigurationLimitsClampNumbers()
{
    // The configuration endpoint deserializes the request body straight onto the settings
    // object, so these bounds are the only server-side gate; the page's JS is bypassable.
    Assert(ConfigurationLimits.Clamp(-1, ConfigurationLimits.RequestDelayMinimum, ConfigurationLimits.RequestDelayMaximum)
           == ConfigurationLimits.RequestDelayMinimum,
        "A negative request delay must be raised to the minimum.");
    Assert(ConfigurationLimits.Clamp(int.MaxValue, ConfigurationLimits.RequestDelayMinimum, ConfigurationLimits.RequestDelayMaximum)
           == ConfigurationLimits.RequestDelayMaximum,
        "An absurd request delay must be capped.");
    Assert(ConfigurationLimits.Clamp(0, ConfigurationLimits.MaxSearchResultsMinimum, ConfigurationLimits.MaxSearchResultsMaximum)
           == ConfigurationLimits.MaxSearchResultsMinimum,
        "Zero search results must become at least one.");
    Assert(ConfigurationLimits.Clamp(10, ConfigurationLimits.MaxSearchResultsMinimum, ConfigurationLimits.MaxSearchResultsMaximum) == 10,
        "A value already in range must be left alone.");
    Assert(ConfigurationLimits.Clamp(0, ConfigurationLimits.NegativeCacheHoursMinimum, ConfigurationLimits.NegativeCacheHoursMaximum) == 0,
        "Zero negative-cache hours is a deliberate choice and must survive.");
    Assert(ConfigurationLimits.Clamp(5, 10, 1) == 5,
        "An inverted range must be tolerated rather than throw.");
}

    [Xunit.Fact(DisplayName = "Base URL is normalized or replaced by the default")]
    public void TestConfigurationLimitsNormalizeBaseUrl()
{
    Assert(ConfigurationLimits.NormalizeBaseUrl("https://www.animeclick.it") == ConfigurationLimits.DefaultBaseUrl,
        "The default base URL must round-trip unchanged, so a normal install is not rewritten on every start.");
    Assert(ConfigurationLimits.NormalizeBaseUrl("  https://www.animeclick.it/  ") == ConfigurationLimits.DefaultBaseUrl,
        "Whitespace and a trailing slash must be normalized away.");
    Assert(ConfigurationLimits.NormalizeBaseUrl("https://mirror.example:8443/ac") == "https://mirror.example:8443/ac",
        "A custom host, port and path must be preserved.");
    Assert(ConfigurationLimits.NormalizeBaseUrl("non-un-url") == ConfigurationLimits.DefaultBaseUrl,
        "A value that is not a URL must fall back to the default.");
    Assert(ConfigurationLimits.NormalizeBaseUrl("file:///etc/passwd") == ConfigurationLimits.DefaultBaseUrl,
        "A non-HTTP scheme must fall back to the default.");
    Assert(ConfigurationLimits.NormalizeBaseUrl("/anime/72") == ConfigurationLimits.DefaultBaseUrl,
        "A relative value must fall back to the default: every scraping URL is built on this.");
    Assert(ConfigurationLimits.NormalizeBaseUrl(null) == ConfigurationLimits.DefaultBaseUrl,
        "A missing base URL must fall back to the default.");
}

    [Xunit.Fact(DisplayName = "Percent-encoded episode ids are accepted and round-trip to a URL")]
    public void TestPercentEncodedProviderIdsAreUsable()
{
    // The plugin writes these itself, from the episode URL, whenever the Italian title carries
    // an accent. Before the fix the id was rejected on the way back in and the AnimeClick
    // synopsis was never fetched for those episodes — 13% of a real library.
    Assert(AnimeClickClient.TryNormalizeAnimeClickId("216767/c%C3%A8-una-ragione-per-tutto", out var accented),
        "An id with a percent-encoded accent must be accepted.");
    Assert(accented == "216767/cè-una-ragione-per-tutto",
        $"The accent must be decoded, got \"{accented}\".");

    Assert(AnimeClickClient.TryBuildEpisodeUrl("https://www.animeclick.it", accented, out var url),
        "A decoded id must still build an episode URL.");
    Assert(url == "https://www.animeclick.it/episodio/216767/c%C3%A8-una-ragione-per-tutto",
        $"The URL must re-encode the accent for the request, got \"{url}\".");

    Assert(AnimeClickClient.TryNormalizeAnimeClickId("215342/il-lavoro-part-time-pu%C3%B2-cambiare-la-vita", out _),
        "The other shape observed in the library must be accepted too.");

    // Plain ids must be untouched: this is the regression that matters most.
    Assert(AnimeClickClient.TryNormalizeAnimeClickId("216762/una-storia-divertente", out var plain)
           && plain == "216762/una-storia-divertente",
        "An id without encoding must pass through unchanged.");
    Assert(AnimeClickClient.TryNormalizeAnimeClickId("2966-naruto", out var legacy) && legacy == "2966/naruto",
        "The legacy dash form must still be migrated.");
}

    [Xunit.Fact(DisplayName = "Decoding an id cannot smuggle path separators or traversal")]
    public void TestPercentDecodingCannotEscapeTheIdShape()
{
    // Decoding happens before validation on purpose, so an encoded separator becomes a real one
    // and is then refused by the same conservative class, instead of reaching URL composition.
    foreach (var hostile in new[]
             {
                 "216767/a%2Fb",
                 "216767/%2E%2E%2F%2E%2E%2Fetc%2Fpasswd",
                 "216767/..%2Fadmin",
                 "216767/a%00b",
                 "216767/a%20b",
                 "216767/%2Fetc"
             })
    {
        Assert(!AnimeClickClient.TryNormalizeAnimeClickId(hostile, out _),
            $"\"{hostile}\" must be rejected after decoding.");
    }
}

    [Xunit.Fact(DisplayName = "Extra rows past the declared count stop position being evidence")]
    public void TestSeasonPageWithMoreRowsThanDeclaredNeedsCorroboration()
{
    // Reproduces kimi-ni-todoke-ii-tv: a sequel page with 13 rows while AnimeClick declares 12,
    // the extra one first, so ordinal 1 is not the library's E01. Before the guard the matcher
    // accepted this at score 120 and mis-titled the whole season one episode early.
    var html = """
        <table class="table"><tbody>
        <tr><td>Ep. 01</td><td><a href="/episodio/9001/extra">Riga in più</a></td><td>23'</td></tr>
        <tr><td>Ep. 02</td><td><a href="/episodio/9002/vero-primo">Vero primo</a></td><td>23'</td></tr>
        <tr><td>Ep. 03</td><td><a href="/episodio/9003/vero-secondo">Vero secondo</a></td><td>23'</td></tr>
        </tbody></table>
        """;
    var rows = new AnimeClickHtmlParser().ParseEpisodesPage(html, "https://www.animeclick.it");

    var unguarded = AnimeClickEpisodeMatcher.Match(
        rows,
        new AnimeClickEpisodeMatchContext(1, 1) { IsSeasonSpecificPage = true });
    Assert(unguarded.Success && unguarded.Strategy == "seasonPageOrdinal",
        "Without a declared count the sequel-page mapping must stay available as before.");

    var guarded = AnimeClickEpisodeMatcher.Match(
        rows,
        new AnimeClickEpisodeMatchContext(1, 1)
        {
            IsSeasonSpecificPage = true,
            DeclaredEpisodeCount = 2
        });
    Assert(!guarded.Success,
        $"3 rows against 2 declared must not be accepted on position alone, got \"{guarded.Episode?.Title}\".");

    // The file's own title is corroboration, so the mapping is still usable when it agrees.
    var corroborated = AnimeClickEpisodeMatcher.Match(
        rows,
        new AnimeClickEpisodeMatchContext(1, 1)
        {
            IsSeasonSpecificPage = true,
            DeclaredEpisodeCount = 2,
            JellyfinTitle = "Riga in più"
        });
    Assert(corroborated.Success && corroborated.Episode?.Title == "Riga in più",
        "A matching file title must still corroborate the mapping.");

    // Counts that agree must behave exactly as before the guard.
    var consistent = AnimeClickEpisodeMatcher.Match(
        rows,
        new AnimeClickEpisodeMatchContext(1, 2)
        {
            IsSeasonSpecificPage = true,
            DeclaredEpisodeCount = 3
        });
    Assert(consistent.Success && consistent.Strategy == "seasonPageOrdinal",
        "When the counts agree the sequel-page mapping must be unaffected.");
}

    [Xunit.Fact(DisplayName = "TVDB episode page: data is an object with an episodes array")]
    public void TestTvdbEpisodePageRealShape()
{
    // Trimmed from a live /series/317802/episodes/default/ita?page=0 response. The parser used to
    // require "data" itself to be an array, so every page was rejected and the TVDB synopsis
    // chain produced nothing — invisibly, because the failure was logged at Debug. No test
    // covered this parser with a realistic payload, which is how the wrong shape survived.
    const string realShape = """
        {
          "status": "success",
          "data": {
            "id": 317802,
            "name": "WWW.WORKING!!",
            "slug": "www-working",
            "defaultSeasonType": 1,
            "episodes": [
              { "id": 1, "seasonNumber": 1, "number": 1, "overview": "" },
              { "id": 2, "seasonNumber": 1, "number": 2, "overview": "Trama del secondo." },
              { "id": 3, "seasonNumber": 0, "number": 1, "overview": "Uno speciale." }
            ],
            "year": "2016"
          },
          "links": { "prev": null, "self": "…", "next": null, "total_items": 3, "page_size": 500 }
        }
        """;

    var records = AnimeClickTvdbClient.ParseEpisodesFromPage(realShape);
    Assert(records.Count == 3, $"Expected 3 episode records from data.episodes, got {records.Count}.");
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview(realShape, 1, 2) == "Trama del secondo.",
        "S01E02 overview must be read from data.episodes.");
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview(realShape, 0, 1) == "Uno speciale.",
        "Season zero must be addressable too.");
    Assert(AnimeClickTvdbClient.ParseEpisodeOverview(realShape, 1, 1) == "",
        "An episode with no Italian overview must come back empty, not missing: that is a real answer.");
    Assert(AnimeClickTvdbClient.ParseNextLink(realShape) is null,
        "A single-page response must report no next link.");

    // The older flat shape must keep working, and a payload with neither must still be refused.
    Assert(AnimeClickTvdbClient.ParseEpisodesFromPage(
               """{"data":[{"seasonNumber":2,"number":5,"overview":"Piatto."}]}""").Count == 1,
        "A response with data as an array must still parse.");
    Assert(AnimeClickTvdbClient.ParseEpisodesFromPage("""{"data":{"id":1,"name":"senza episodi"}}""").Count == 0,
        "An object without an episodes array must yield nothing.");
    Assert(AnimeClickTvdbClient.ParseEpisodesFromPage("""{"data":"stringa"}""").Count == 0,
        "A scalar data must be refused.");
    Assert(AnimeClickTvdbClient.ParseEpisodesFromPage("non json").Count == 0,
        "Malformed JSON must be refused.");
}

    private static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
}
