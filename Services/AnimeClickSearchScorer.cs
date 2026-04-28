using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeClick.Plugin.Services;

public static class AnimeClickSearchScorer
{
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
        => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
}
