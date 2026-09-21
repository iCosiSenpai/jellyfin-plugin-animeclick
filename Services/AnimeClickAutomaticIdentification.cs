using System.Text.RegularExpressions;
using AnimeClick.Plugin.Providers;
using MediaBrowser.Model.Providers;

namespace AnimeClick.Plugin.Services;

/// <summary>Automatic scans require title evidence and a clear winner. Manual search remains available.</summary>
internal static partial class AnimeClickAutomaticIdentification
{
    internal static RemoteSearchResult? Select(
        IEnumerable<RemoteSearchResult> candidates, string name, int? year)
    {
        var query = Normalize(AnimeClickSeriesSearchProvider.CleanSearchQuery(name));
        if (query.Length == 0) return null;

        var requestedPart = PartNumber(name);
        var ranked = candidates
            .Where(candidate => candidate.ProviderIds.TryGetValue("AnimeClick", out var id)
                && AnimeClickClient.TryNormalizeAnimeClickId(id, out _))
            .DistinctBy(candidate =>
            {
                AnimeClickClient.TryNormalizeAnimeClickId(candidate.ProviderIds["AnimeClick"], out var id);
                return id.Split('/')[0];
            })
            .Select(candidate => new { Candidate = candidate, Score = Score(candidate, query, requestedPart, year) })
            .Where(candidate => candidate.Score >= 65)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        if (ranked.Count == 0 || (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 15))
            return null;

        return ranked[0].Candidate;
    }

    private static int Score(RemoteSearchResult candidate, string query, int? requestedPart, int? year)
    {
        var title = Normalize(AnimeClickSeriesSearchProvider.CleanSearchQuery(candidate.Name ?? string.Empty));
        AnimeClickClient.TryNormalizeAnimeClickId(candidate.ProviderIds["AnimeClick"], out var id);
        var slug = Normalize(AnimeClickSeriesSearchProvider.CleanSearchQuery(
            id.Contains('/') ? id[(id.IndexOf('/') + 1)..].Replace('-', ' ') : string.Empty));
        var part = PartNumber(candidate.Name ?? string.Empty) ?? PartNumber(slug);
        if (requestedPart.HasValue && requestedPart != (part ?? 1)) return 0;
        if (!requestedPart.HasValue && part > 1) return 0;

        // A year is corroboration, never a substitute for matching the title.
        if (year.HasValue && candidate.ProductionYear.HasValue
            && Math.Abs((long)year.Value - candidate.ProductionYear.Value) > 1) return 0;

        var confidence = Math.Max(TitleConfidence(query, title), TitleConfidence(query, slug));
        if (confidence < 65) return 0;
        return confidence + (year.HasValue && year == candidate.ProductionYear ? 25 : 0);
    }

    private static int TitleConfidence(string query, string title)
    {
        if (title.Length == 0) return 0;
        if (title == query) return 100;
        var wanted = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var found = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var overlap = wanted.Count(found.Contains);
        // Token boundaries matter: "86" must not identify "1986", nor "Blue" "Blueberry".
        if (overlap == 0 || (double)overlap / wanted.Count < 0.65) return 0;
        if (wanted.Count == 1 && query.Length < 5) return 0;
        return (int)(80.0 * overlap / Math.Max(wanted.Count, found.Count));
    }

    private static string Normalize(string value)
        => Regex.Replace(AnimeClickSearchScorer.RemoveDiacritics(value).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();

    internal static int? PartNumber(string value)
    {
        var match = PartRegex().Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var part)) return part;
        match = OrdinalRegex().Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, out part)) return part;
        var words = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null; // "86" is a title, not a season.
        var last = words[^1].ToUpperInvariant();
        if (int.TryParse(last, out part) && part is >= 2 and <= 99) return part;
        return last switch { "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6,
            "VII" => 7, "VIII" => 8, "IX" => 9, "X" => 10, _ => null };
    }

    [GeneratedRegex(@"\b(?:season|stagione|part|parte|s)\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"\b(\d+)(?:st|nd|rd|th)\s+season\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalRegex();
}
