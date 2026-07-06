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

        var queryTokens = queryNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleTokens = titleNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        score += titleTokens.Count(queryTokens.Contains) * 8;

        if (productionYear.HasValue && result.ProductionYear.HasValue)
        {
            var diff = Math.Abs(result.ProductionYear.Value - productionYear.Value);
            score += diff == 0 ? 35 : Math.Max(-30, 12 - (diff * 6));
        }

        var format = result.Format ?? string.Empty;
        if (seriesRequest)
        {
            if (format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("TV", StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }

            if (format.Contains("Movie", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("Film", StringComparison.OrdinalIgnoreCase))
            {
                score -= 60;
            }

            if (format.Contains("Special", StringComparison.OrdinalIgnoreCase))
            {
                score -= 45;
            }
        }

        return score;
    }

    private static string NormalizeForScore(string value)
        => Regex.Replace(RemoveDiacritics(value).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
}
