using System;
using System.Globalization;
using System.Text;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Compares AnimeClick episode identities independently from their mutable slug.
/// AnimeClick first exposes a row as, for example, <c>426549</c> and appends
/// <c>/riprese-per-la-tv</c> when the editorial title is published. The numeric
/// component is the durable identity; treating the complete value as a key makes
/// the very update we are waiting for look like a vanished row.
/// </summary>
internal static class AnimeClickEpisodeProviderId
{
    public static bool TryGetStableId(string? providerId, out string stableId)
    {
        stableId = string.Empty;
        if (!AnimeClickClient.TryNormalizeAnimeClickId(providerId, out var normalized))
        {
            return false;
        }

        var numeric = normalized.Split('/', 2)[0];
        if (!long.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        stableId = numeric;
        return true;
    }

    public static bool Equals(string? left, string? right)
    {
        if (TryGetStableId(left, out var leftStable)
            && TryGetStableId(right, out var rightStable))
        {
            return string.Equals(leftStable, rightStable, StringComparison.Ordinal);
        }

        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares editorial titles without letting punctuation, casing or accents create
    /// a refresh loop. Different words still remain different, which is what lets an
    /// English downstream title be replaced by the Italian AnimeClick title.
    /// </summary>
    public static bool TitlesEquivalent(string? left, string? right)
        => string.Equals(NormalizeTitle(left), NormalizeTitle(right), StringComparison.Ordinal);

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
