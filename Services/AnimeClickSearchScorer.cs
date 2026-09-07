using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeClick.Plugin.Services;

public static class AnimeClickSearchScorer
{
    /// <summary>
    /// Strips diacritics from a string (e.g. <c>Caffè</c> → <c>Caffe</c>,
    /// <c>voilà</c> → <c>voila</c>) by decomposing to FormD and removing
    /// combining marks. Non-Latin base letters and lone marks are preserved.
    /// </summary>
    internal static string RemoveDiacritics(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(value.Length);
        foreach (var ch in normalized.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.NonSpacingMark
                && cat != UnicodeCategory.SpacingCombiningMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
    /// <summary>
    /// Da quale forma del nome viene la query che ha prodotto un candidato.
    /// Piu' la query si allontana dal nome dell'opera, piu' il candidato va verificato.
    /// </summary>
    public enum SearchStage
    {
        /// <summary>Il nome dell'opera, al netto di punteggiatura e residui di release.</summary>
        Primary,

        /// <summary>Il nome accorciato dalla coda: "Saekano the Movie Finale" → "Saekano".</summary>
        Prefix,

        /// <summary>Una parola sola presa dal titolo: "… - Camelot Wandering; …" → "Camelot".</summary>
        DistinctiveToken
    }

    /// <summary>
    /// Il punteggio minimo per fidarsi di un candidato che non viene dal nome dell'opera.
    /// Richiede un riscontro vero: titolo contenuto nella query (45) piu' formato giusto (25),
    /// oppure anno esatto (35) sopra un aggancio di titolo decente.
    /// </summary>
    private const int FallbackScoreFloor = 60;

    /// <summary>La lunghezza sotto la quale un prefisso e' troppo corto per dire qualcosa.</summary>
    private const int MinimumTrustedPrefixLength = 5;

    /// <summary>
    /// Se un candidato puo' essere accettato, viste la provenienza della query e la sua forza.
    /// </summary>
    /// <remarks>
    /// Il provider prende il primo risultato in classifica senza guardare il punteggio: senza
    /// questo filtro una query larga attaccherebbe all'opera la scheda sbagliata, e una sinossi
    /// italiana sbagliata e' peggio di nessuna sinossi. Sono successe entrambe in prova:
    /// "Saekano the Movie Finale" cercato per la parola "Finale" trovava "Gintama - Capitolo
    /// Finale", e la terza stagione di Mushoku Tensei prendeva la sinossi della prima.
    ///
    /// Le tre provenienze si fidano in modo diverso:
    /// <list type="bullet">
    /// <item><see cref="SearchStage.Primary"/>: nessun filtro. E' il comportamento storico,
    /// e un'opera senza anno in libreria deve continuare a risolversi.</item>
    /// <item><see cref="SearchStage.Prefix"/>: la soglia, oppure il caso non ambiguo — un solo
    /// candidato del formato giusto per un prefisso del titolo abbastanza lungo. E' cosi' che
    /// "Saekano" trova <c>saekano-movie</c>, che di suo si chiama "Saenai Heroine no
    /// Sodatekata Fine" e col titolo in libreria non condivide una parola.</item>
    /// <item><see cref="SearchStage.DistinctiveToken"/>: la soglia <b>e</b> l'anno esatto. Una
    /// parola sola non basta a distinguere due opere diverse che la contengono entrambe.</item>
    /// </list>
    /// </remarks>
    /// <param name="score">Il punteggio calcolato da <see cref="Score"/>.</param>
    /// <param name="stage">Da quale forma del nome viene la query.</param>
    /// <param name="yearMatchesExactly">Se l'anno del candidato coincide con quello in libreria.</param>
    /// <param name="isOnlyCompatibleCandidate">Se e' l'unico candidato del formato richiesto.</param>
    /// <param name="queryLength">La lunghezza della query che lo ha prodotto.</param>
    public static bool IsAcceptable(
        int score,
        SearchStage stage,
        bool yearMatchesExactly = false,
        bool isOnlyCompatibleCandidate = false,
        int queryLength = 0)
        => stage switch
        {
            SearchStage.Primary => true,
            SearchStage.Prefix => score >= FallbackScoreFloor
                || (isOnlyCompatibleCandidate && queryLength >= MinimumTrustedPrefixLength),
            SearchStage.DistinctiveToken => score >= FallbackScoreFloor && yearMatchesExactly,
            _ => score >= FallbackScoreFloor
        };

    internal static bool IsFormatCompatible(AnimeClickSearchResult result, bool seriesRequest)
    {
        var format = result.Format ?? string.Empty;
        if (string.IsNullOrWhiteSpace(format))
        {
            // Keep unknown formats: a missing label must not suppress a valid result.
            return true;
        }

        var isTelevision = format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format.Trim(), "TV", StringComparison.OrdinalIgnoreCase);
        var isMovie = format.Contains("Film", StringComparison.OrdinalIgnoreCase)
            || format.Contains("Movie", StringComparison.OrdinalIgnoreCase);
        var isSideContent = format.Contains("OVA", StringComparison.OrdinalIgnoreCase)
            || format.Contains("OAV", StringComparison.OrdinalIgnoreCase)
            || format.Contains("ONA", StringComparison.OrdinalIgnoreCase)
            || format.Contains("Special", StringComparison.OrdinalIgnoreCase);

        return seriesRequest ? isTelevision : isMovie || (!isTelevision && !isSideContent);
    }

    public static int Score(AnimeClickSearchResult result, string query, int? productionYear, bool seriesRequest)
    {
        var score = 0;
        var queryNormalized = NormalizeForScore(query);
        var titleNormalized = NormalizeForScore(result.Title);

        if (titleNormalized == queryNormalized)
        {
            score += 100;
        }
        else if (titleNormalized.Contains(queryNormalized, StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }
        else
        {
            // Light fuzzy: token overlap ratio gives a small bonus even on imperfect matches
            var qTokens = queryNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tTokens = titleNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (qTokens.Length > 0 && tTokens.Length > 0)
            {
                int overlap = tTokens.Count(t => qTokens.Contains(t, StringComparer.OrdinalIgnoreCase));
                double ratio = (double)overlap / Math.Max(qTokens.Length, 1);
                if (ratio >= 0.6) score += 18;
                else if (ratio >= 0.4) score += 8;
            }
        }

        var queryTokens = queryNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleTokens = titleNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        score += titleTokens.Count(queryTokens.Contains) * 8;

        if (productionYear.HasValue && result.ProductionYear.HasValue)
        {
            var diff = Math.Abs(result.ProductionYear.Value - productionYear.Value);
            // Fra due stagioni della stessa serie il titolo non distingue: quello della prima
            // coincide con la radice che portano tutte, e un titolo identico (100) copriva
            // l'anno esatto della stagione giusta (35). La terza stagione di Mushoku Tensei
            // prendeva cosi' la sinossi della prima. Uno scarto di anni e' invece un indizio
            // forte, e come tale pesa: a cinque anni di distanza sono due opere diverse.
            int yearBonus = diff == 0 ? 35 : Math.Max(-45, 12 - (diff * 12));
            // Year bonus is stronger only when we have a decent title match already
            score += (score > 20) ? yearBonus : (yearBonus / 2);
        }

        var format = result.Format ?? string.Empty;
        if (seriesRequest)
        {
            bool titleStrong = score >= 40; // avoid format bonus on weak title hits
            if ((format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(format.Trim(), "TV", StringComparison.OrdinalIgnoreCase)) && titleStrong)
            {
                score += 25;
            }

            if (format.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                || format.Contains("Film", StringComparison.OrdinalIgnoreCase))
            {
                score -= 60;
            }

            if (format.Contains("Special", StringComparison.OrdinalIgnoreCase)
                || format.Contains("OVA", StringComparison.OrdinalIgnoreCase)
                || format.Contains("OAV", StringComparison.OrdinalIgnoreCase)
                || format.Contains("ONA", StringComparison.OrdinalIgnoreCase))
            {
                score -= 80;
            }
        }
        else
        {
            if (format.Contains("Movie", StringComparison.OrdinalIgnoreCase)
                || format.Contains("Film", StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            if (format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format.Trim(), "TV", StringComparison.OrdinalIgnoreCase))
            {
                score -= 60;
            }
        }

        return score;
    }

    private static string NormalizeForScore(string value)
        => Regex.Replace(RemoveDiacritics(value).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
}
