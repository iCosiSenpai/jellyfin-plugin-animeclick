using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

public partial class AnimeClickSeriesSearchProvider
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickSeriesSearchProvider> _logger;

    public AnimeClickSeriesSearchProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickSeriesSearchProvider> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _logger = logger;
    }

    public async Task<IEnumerable<RemoteSearchResult>> SearchAsync(
        string name,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        int? productionYear = null,
        bool seriesRequest = true)
    {
        var trimmed = name.Trim();
        var deaccented = AnimeClickSearchScorer.RemoveDiacritics(trimmed);
        var useForSearch = deaccented != trimmed ? deaccented : trimmed;

        // ── Direct ID lookup ──
        // If the query looks like an AnimeClick ID (e.g. "72", "72/naruto"),
        // skip text search and fetch the anime page directly.
        if (AnimeClickClient.TryNormalizeAnimeClickId(trimmed, out var normalizedId))
        {
            return await DirectLookupAsync(normalizedId, configuration, cancellationToken);
        }

        // ── Text search ──
        var cleanedQuery = CleanSearchQuery(useForSearch);

        var cacheKey = $"search:v3::{cleanedQuery.ToLowerInvariant()}::{productionYear?.ToString() ?? "any"}::{(seriesRequest ? "series" : "movie")}";
        var negativeCacheKey = $"search-empty:v2::{cleanedQuery.ToLowerInvariant()}::{productionYear?.ToString() ?? "any"}::{(seriesRequest ? "series" : "movie")}";
        var cached = await _cache.GetAsync<List<RemoteSearchResult>>(cacheKey, configuration.CacheHours, cancellationToken);
        if (cached is not null)
        {
            _logger.LogDebug("AnimeClick search cache hit: {Key}", cacheKey);
            return cached;
        }

        var negativeCached = await _cache.GetAsync<string>(negativeCacheKey, configuration.NegativeCacheHours, cancellationToken);
        if (negativeCached == "empty")
        {
            _logger.LogDebug("AnimeClick negative search cache hit: {Key}", negativeCacheKey);
            return [];
        }

        var attemptsHadErrors = false;

        // Try original cleaned query first
        var attempt = await ExecuteSearchAsync(cleanedQuery, configuration, cancellationToken, productionYear, seriesRequest);
        attemptsHadErrors |= attempt.HadError;
        var results = attempt.Results;

        // If no results, try the (deaccented) original form
        if (results.Count == 0 && cleanedQuery != useForSearch)
        {
            _logger.LogInformation("AnimeClick: No results for '{Clean}', retrying with original '{Original}'",
                cleanedQuery, useForSearch);
            attempt = await ExecuteSearchAsync(useForSearch, configuration, cancellationToken, productionYear, seriesRequest);
            attemptsHadErrors |= attempt.HadError;
            results = attempt.Results;
        }

        // If still no results, try removing colons, special chars
        if (results.Count == 0)
        {
            var simplified = SimplifyQuery(cleanedQuery);
            if (simplified != cleanedQuery)
            {
                _logger.LogInformation("AnimeClick: Retrying with simplified '{Simplified}'", simplified);
                attempt = await ExecuteSearchAsync(simplified, configuration, cancellationToken, productionYear, seriesRequest);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
            }
        }

        // If the full Japanese/romaji title only matched an OVA or another
        // incompatible format, retry with the distinctive suffix (for example
        // "Toaru Kagaku no Railgun S" → "Railgun S").
        if (results.Count == 0)
        {
            var suffixQuery = GetSuffixQuery(useForSearch);
            if (suffixQuery is not null
                && !string.Equals(suffixQuery, cleanedQuery, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("AnimeClick: Retrying with distinctive suffix '{Suffix}'", suffixQuery);
                attempt = await ExecuteSearchAsync(suffixQuery, configuration, cancellationToken, productionYear, seriesRequest, AnimeClickSearchScorer.SearchStage.Prefix);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
            }
        }

        // If still no results, try just the first 2-3 significant words
        if (results.Count == 0)
        {
            var shortQuery = GetShortQuery(cleanedQuery);
            if (shortQuery is not null)
            {
                _logger.LogInformation("AnimeClick: Retrying with short query '{Short}'", shortQuery);
                attempt = await ExecuteSearchAsync(shortQuery, configuration, cancellationToken, productionYear, seriesRequest, AnimeClickSearchScorer.SearchStage.Prefix);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
            }
        }

        // Il titolo accorciato dalla coda, una parola alla volta. AnimeClick cerca sottostringhe
        // contigue e conosce l'opera con un altro titolo: "Saekano the Movie Finale" non esiste
        // sul sito, "Saekano" trova la scheda (slug saekano-movie).
        var alreadyTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            cleanedQuery, useForSearch, SimplifyQuery(cleanedQuery)
        };
        // Anche i due tentativi qui sopra: il prefisso di tre parole coincide spesso con
        // la "short query", e rifarlo sarebbe una richiesta al sito buttata via.
        if (GetSuffixQuery(useForSearch) is { } triedSuffix) alreadyTried.Add(triedSuffix);
        if (GetShortQuery(cleanedQuery) is { } triedShort) alreadyTried.Add(triedShort);

        if (results.Count == 0)
        {
            foreach (var prefix in GetPrefixQueries(cleanedQuery))
            {
                if (!alreadyTried.Add(prefix))
                {
                    continue;
                }

                _logger.LogInformation("AnimeClick: Retrying with prefix '{Prefix}'", prefix);
                attempt = await ExecuteSearchAsync(prefix, configuration, cancellationToken, productionYear, seriesRequest, AnimeClickSearchScorer.SearchStage.Prefix);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
                if (results.Count > 0)
                {
                    break;
                }
            }
        }

        // Ultima spiaggia: le parole che da sole identificano l'opera, per i titoli in cui
        // quella che conta sta in mezzo e nessun prefisso la raggiunge (Fate/Grand Order …
        // Camelot …, slug fate-grand-order-camelot, si trova solo con "Camelot").
        if (results.Count == 0)
        {
            foreach (var token in GetDistinctiveTokens(cleanedQuery))
            {
                if (!alreadyTried.Add(token))
                {
                    continue;
                }

                _logger.LogInformation("AnimeClick: Retrying with distinctive token '{Token}'", token);
                attempt = await ExecuteSearchAsync(token, configuration, cancellationToken, productionYear, seriesRequest, AnimeClickSearchScorer.SearchStage.DistinctiveToken);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
                if (results.Count > 0)
                {
                    break;
                }
            }
        }

        if (results.Count > 0)
        {
            await _cache.SetAsync(cacheKey, results, cancellationToken);
        }
        else if (!attemptsHadErrors)
        {
            await _cache.SetAsync(negativeCacheKey, "empty", cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Direct lookup by AnimeClick ID — fetches the page and returns it as a search result.
    /// </summary>
    private async Task<List<RemoteSearchResult>> DirectLookupAsync(string idOrSlug, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, idOrSlug);
            _logger.LogInformation("AnimeClick: Direct ID lookup → {Url}", url);

            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var anime = _parser.ParseAnimePage(url, html);

            // Cache the full anime data since we already have it
            var cacheKey = $"anime::{url}";
            await _cache.SetAsync(cacheKey, anime, cancellationToken);

            return
            [
                new RemoteSearchResult
                {
                    Name = anime.Title,
                    ProductionYear = anime.ProductionYear,
                    SearchProviderName = "AnimeClick",
                    ImageUrl = anime.ImageUrl,
                    Overview = anime.Overview,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["AnimeClick"] = anime.Id
                    }
                }
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Direct lookup failed for ID '{Id}'", idOrSlug);
            return [];
        }
    }

    private async Task<SearchAttempt> ExecuteSearchAsync(
        string query,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        int? productionYear,
        bool seriesRequest,
        AnimeClickSearchScorer.SearchStage stage = AnimeClickSearchScorer.SearchStage.Primary)
    {
        var slug = Uri.EscapeDataString(query);
        var url = $"{configuration.BaseUrl}/cerca?name={slug}";
        _logger.LogInformation("Ricerca AnimeClick: {Query} → {Url}", query, url);

        try
        {
            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var parsedResults = _parser.ParseSearchResults(html, configuration.BaseUrl);
            var searchResults = parsedResults
                .Where(result => AnimeClickSearchScorer.IsFormatCompatible(result, seriesRequest))
                .ToList();
            var configuredMaxResults = configuration.MaxSearchResults > 0
                ? configuration.MaxSearchResults
                : 10;
            var maxResults = Math.Min(configuredMaxResults, 25);

            var ranked = searchResults
                .Select(r => new { Result = r, Score = AnimeClickSearchScorer.Score(r, query, productionYear, seriesRequest) })
                // Il provider prende il primo della classifica senza guardare il punteggio.
                // Su una query di ripiego — un prefisso corto, una parola sola — questo
                // basterebbe ad attaccare all'opera una scheda che non c'entra: cercando
                // "Camelot" tornano anche due film del 1990 e del 1998. Meglio nessuna
                // sinossi che la sinossi di un'altra opera.
                .Where(x => AnimeClickSearchScorer.IsAcceptable(
                    x.Score,
                    stage,
                    yearMatchesExactly: productionYear.HasValue
                        && x.Result.ProductionYear == productionYear,
                    isOnlyCompatibleCandidate: searchResults.Count == 1,
                    queryLength: query.Length))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Result.ProductionYear ?? 9999)
                .ThenBy(x => x.Result.Title)
                .ToList();

            _logger.LogInformation(
                "AnimeClick: Parsed {Count} search candidates for '{Query}', {Compatible} compatible with {Kind}, {Accepted} oltre la soglia",
                parsedResults.Count,
                query,
                searchResults.Count,
                seriesRequest ? "series" : "movie",
                ranked.Count);

            foreach (var candidate in ranked.Take(Math.Min(5, ranked.Count)))
            {
                _logger.LogDebug(
                    "AnimeClick search candidate score={Score} title={Title} year={Year} format={Format} id={Id}",
                    candidate.Score,
                    candidate.Result.Title,
                    candidate.Result.ProductionYear,
                    candidate.Result.Format,
                    candidate.Result.Id);
            }

            return SearchAttempt.Success(ranked
                .Take(maxResults)
                .Select(x => x.Result)
                .Select(r => new RemoteSearchResult
                {
                    Name = r.Title,
                    ProductionYear = r.ProductionYear,
                    SearchProviderName = "AnimeClick",
                    ImageUrl = r.ThumbnailUrl,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["AnimeClick"] = r.Id
                    }
                })
                .ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Search failed for '{Query}'", query);
            return SearchAttempt.Error();
        }
    }

    // ── Query cleaning helpers ──

    /// <summary>
    /// Removes media type suffixes that Jellyfin or file naming conventions add
    /// but AnimeClick doesn't understand (TV, Movie, OVA, etc.).
    /// Also removes year suffixes like "(2024)".
    /// Fortified with full-width chars, quotes, ampersands and more sequel forms.
    /// </summary>
    internal static string CleanSearchQuery(string query)
    {
        var cleaned = query;

        // Normalize some full-width and fancy punctuation early
        cleaned = Regex.Replace(cleaned, "[\uFF01-\uFF5E]", m => ((char)(m.Value[0] - 0xFEE0)).ToString());
        cleaned = cleaned.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"');

        // Remove common suffixes in parentheses: (TV), (Movie), (2024), (Serie TV), (OVA)
        cleaned = Regex.Replace(cleaned, @"\s*\((?:TV|Movie|Film|Serie\s*TV|OVA|OAV|Special|ONA|OVA|OAV|\d{4})\)\s*", " ",
            RegexOptions.IgnoreCase);

        // Remove standalone media type words at end
        cleaned = Regex.Replace(cleaned, @"\s+(?:TV|Movie|Film|the Animation|Season \d+|S\d)\s*$", "",
            RegexOptions.IgnoreCase);

        // Remove season indicators: S01, Season 1, 2nd Season, Part 2, etc.
        cleaned = Regex.Replace(cleaned, @"\s+(?:S\d+|Season\s*\d+|Stagione\s*\d+|2nd Season|Second Season|Part \d+|II|III)\s*$", "",
            RegexOptions.IgnoreCase);

        // Collapse & / and variants, quotes
        cleaned = Regex.Replace(cleaned, @"\s*&\s*", " and ");
        cleaned = Regex.Replace(cleaned, @"[""']", " ");

        cleaned = StripReleaseNoise(cleaned);

        return cleaned.Trim();
    }

    /// <summary>
    /// Taglia il nome davanti al primo residuo di release.
    /// </summary>
    /// <remarks>
    /// Quando una cartella si chiama <c>Paprika.2006.4K.HDR.DV.2160p.BDRip Ita Eng Jap x265-NAHOM</c>,
    /// Jellyfin usa quella stringa come titolo dell'elemento e la passa qui tale e quale.
    /// AnimeClick cerca sottostringhe contigue, quindi nessuna variante di quel nome puo'
    /// trovare qualcosa: la scheda esiste ("Paprika - Sognando un sogno") ma resta irraggiungibile.
    ///
    /// Si taglia al primo marcatore tecnico invece di rimuoverli uno per uno: tutto cio' che
    /// segue una risoluzione o un codec e' nomenclatura di release, mai parte del titolo.
    /// Prima del primo marcatore non si tocca nulla, cosi' "Perfect Blue" e "Cowboy Bebop"
    /// restano intatti.
    /// </remarks>
    private static string StripReleaseNoise(string query)
    {
        var match = ReleaseNoiseRegex().Match(query);
        if (!match.Success || match.Index == 0)
        {
            // match.Index == 0 vorrebbe dire che il titolo *inizia* con un marcatore:
            // meglio lasciarlo com'e' che restituire una stringa vuota.
            return query;
        }

        var head = query[..match.Index];

        // I separatori tipici delle release ("Paprika.2006", "Akira_1988") vanno resi spazi
        // solo qui: un punto in "Dr. Stone" o in "Steins;Gate" fa parte del titolo e nel
        // ramo senza marcatori non viene toccato.
        head = Regex.Replace(head, @"[._]+", " ");
        head = Regex.Replace(head, @"\s{2,}", " ");

        // "Paprika 2006", "Akira 1988": nelle release l'anno precede i marcatori tecnici.
        // Si toglie solo qui, dove un marcatore c'e' gia' stato: nel ramo dei titoli normali
        // un numero finale non viene toccato, cosi' "Eyeshield 21" e "Steins;Gate 0" restano interi.
        head = Regex.Replace(head, @"\s+(?:19|20)\d{2}\s*$", string.Empty);

        var trimmed = head.Trim(' ', '-', '–', '—', '[', '(', '{', ',');
        return string.IsNullOrWhiteSpace(trimmed) ? query : trimmed;
    }

    /// <summary>
    /// Il primo marcatore tecnico di una release: risoluzione, sorgente, codec, tracce audio,
    /// profilo HDR. Deve essere delimitato, altrimenti "K-On!" perderebbe la K e
    /// "Eyeshield 21" il numero.
    /// </summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{Nd}])(?:"
        + @"\d{3,4}[pi]|4K|8K|UHD|"
        + @"BD(?:Rip|MV)?|BluRay|Blu-Ray|WEB-?DL|WEB-?Rip|HDTV|DVD(?:Rip)?|REMUX|"
        + @"[xh]\.?26[45]|HEVC|AVC|Hi10P?|10bits?|8bits?|"
        + @"HDR10?|DoVi|SDR|"
        + @"AAC|AC-?3|E?AC3|DTS(?:-HD)?|DDP?[0-9]?|FLAC|Opus|TrueHD|"
        + @"Dual-?Audio|MULTi|VOSTFR|Multi-?Subs|Sub-?ITA|"
        + @"\d{1,2}\.\d(?:ch)?"
        + @")(?![\p{L}\p{Nd}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNoiseRegex();

    /// <summary>
    /// Simplifies by removing special characters (colons, dashes, dots) that
    /// might cause AnimeClick search to fail.
    /// </summary>
    internal static string SimplifyQuery(string query)
    {
        // Replace colons, dashes, dots, slashes with spaces
        var simplified = Regex.Replace(query, @"[:.\-–—/\\]", " ");
        // Collapse multiple spaces
        simplified = Regex.Replace(simplified, @"\s{2,}", " ");
        return simplified.Trim();
    }

    /// <summary>
    /// Extracts the last two significant terms. AnimeClick search often indexes
    /// localized titles better than long romaji prefixes, while keeping sequel
    /// markers such as S, II or 3 useful for disambiguation.
    /// </summary>
    internal static string? GetSuffixQuery(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "of", "no", "to", "and"
        };
        var words = Regex.Split(query, @"[^\p{L}\p{Nd}]+")
            .Where(word => word.Length > 0 && !stopWords.Contains(word))
            .ToArray();

        if (words.Length < 2)
        {
            return null;
        }

        var suffix = string.Join(' ', words.TakeLast(2));
        return string.Equals(suffix, query, StringComparison.OrdinalIgnoreCase) ? null : suffix;
    }

    /// <summary>
    /// Extracts the first 2-3 meaningful words for a "fuzzy" search fallback.
    /// </summary>
    internal static string? GetShortQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1) // skip single-char words
            .Take(3)
            .ToArray();

        if (words.Length < 2) return null;
        var shortQuery = string.Join(' ', words);
        return shortQuery == query ? null : shortQuery;
    }

    /// <summary>
    /// Le parole comuni che non identificano un'opera: articoli, preposizioni, ausiliari,
    /// e i termini di formato che compaiono in mezzo mondo di titoli.
    /// </summary>
    /// <summary>Quante parole al massimo per il prefisso piu' lungo che si prova.</summary>
    private const int MaxPrefixWords = 5;

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "no", "to", "and", "or", "in", "on", "at", "for", "with",
        "is", "are", "am", "be", "been", "was", "were", "do", "does", "did", "doing",
        "you", "your", "we", "us", "it", "its", "my", "me", "he", "she", "they", "them",
        "what", "who", "when", "where", "why", "how", "will", "would", "can", "could",
        "not", "all", "any", "out", "up", "so", "if", "as", "by", "from", "that", "this",
        "used", "just", "very", "more", "most", "movie", "film", "season", "part", "series",
        "il", "lo", "la", "i", "gli", "le", "di", "da", "del", "della", "un", "una", "e",
        "che", "per", "con", "su", "tra", "fra", "il", "al", "dal", "nel"
    };

    /// <summary>
    /// Il titolo accorciato dalla coda, una parola alla volta, fino alla prima.
    /// </summary>
    /// <remarks>
    /// AnimeClick cerca sottostringhe contigue nei titoli e nello slug, quindi un prefisso
    /// del titolo e' il tentativo che ha piu' probabilita' di andare a segno quando il nome
    /// completo non trova nulla: "Saekano the Movie Finale" non esiste sul sito, ma la scheda
    /// ha slug <c>saekano-movie</c> e "Saekano" da sola la trova.
    ///
    /// Non restituisce il titolo intero: e' gia' stato provato prima e ripeterlo
    /// sarebbe una richiesta sprecata.
    /// </remarks>
    internal static IEnumerable<string> GetPrefixQueries(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Ogni prefisso e' una richiesta al sito. I prefissi lunghi sono anche i piu' inutili:
        // se il titolo intero non ha trovato nulla, togliere una parola su quindici cambia
        // poco. Si parte da cinque parole e si scende.
        var start = Math.Min(words.Length - 1, MaxPrefixWords);
        for (var take = start; take >= 1; take--)
        {
            var prefix = string.Join(' ', words.Take(take));
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                yield return prefix;
            }
        }
    }

    /// <summary>
    /// Le poche parole che da sole possono identificare l'opera, dalla piu' lunga in giu'.
    /// </summary>
    /// <remarks>
    /// Ultima spiaggia, per i titoli in cui la parola che conta sta in mezzo e nessun prefisso
    /// la raggiunge: "Fate/Grand Order: Divine Realm of the Round Table - Camelot Wandering;
    /// Agateram" si trova solo cercando "Camelot" (slug <c>fate-grand-order-camelot</c>).
    ///
    /// Una parola sola e' una query larga, che puo' restituire opere senza rapporto: per
    /// questo i risultati di questi tentativi passano dalla soglia di
    /// <see cref="AnimeClickSearchScorer.IsAcceptable"/>, che senza un riscontro di anno o
    /// formato li rifiuta. Il numero di parole e' limitato perche' ognuna e' una richiesta
    /// al sito.
    /// </remarks>
    internal static IEnumerable<string> GetDistinctiveTokens(string query)
        => Regex.Split(query, @"[^\p{L}\p{Nd}]+")
            .Where(word => word.Length >= 5 && !GenericWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(word => word.Length)
            .ThenBy(word => word, StringComparer.OrdinalIgnoreCase)
            .Take(3);

    private sealed class SearchAttempt
    {
        private SearchAttempt(List<RemoteSearchResult> results, bool hadError)
        {
            Results = results;
            HadError = hadError;
        }

        public List<RemoteSearchResult> Results { get; }

        public bool HadError { get; }

        public static SearchAttempt Success(List<RemoteSearchResult> results) => new(results, false);

        public static SearchAttempt Error() => new([], true);
    }
}
