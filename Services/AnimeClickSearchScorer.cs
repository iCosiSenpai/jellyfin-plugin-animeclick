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
            int yearBonus = diff == 0 ? 35 : Math.Max(-30, 12 - (diff * 6));
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
