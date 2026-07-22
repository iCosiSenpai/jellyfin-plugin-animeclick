using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimeClick.Plugin.Models;

/// <summary>
/// Immutable-at-rest snapshot of the raw AnimeClick episode table. Season mapping is
/// deliberately derived at match time so changes in the Jellyfin library or overrides
/// never require another HTTP fetch.
/// </summary>
public sealed class AnimeClickEpisodeCatalog
{
    public List<AnimeClickEpisode> Episodes { get; set; } = [];

    public int? DeclaredEpisodeCount { get; set; }

    public int DeclaredSeasonsCount { get; set; }

    public string LayoutFingerprint { get; set; } = string.Empty;

    public static AnimeClickEpisodeCatalog Create(
        IEnumerable<AnimeClickEpisode> episodes,
        int? declaredEpisodeCount,
        int declaredSeasonsCount)
    {
        var ordered = episodes
            .OrderBy(episode => episode.SourceOrder)
            .ThenBy(episode => episode.ProviderId, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fingerprintSource = new StringBuilder()
            .Append(declaredEpisodeCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")
            .Append('|')
            .Append(declaredSeasonsCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var episode in ordered)
        {
            fingerprintSource
                .Append('\n')
                .Append(episode.SourceOrder)
                .Append('|')
                .Append(episode.RawNumberLabel)
                .Append('|')
                .Append(episode.RawSeasonNumber)
                .Append('|')
                .Append(episode.RawEpisodeNumber)
                .Append('|')
                .Append(episode.ProviderId)
                .Append('|')
                .Append(episode.Title);
        }

        // FNV-1a is stable, fully managed and sufficient for cache/layout identity.
        // This fingerprint is not used for security or artifact verification.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in Encoding.UTF8.GetBytes(fingerprintSource.ToString()))
        {
            hash ^= value;
            hash *= prime;
        }

        return new AnimeClickEpisodeCatalog
        {
            Episodes = ordered,
            DeclaredEpisodeCount = declaredEpisodeCount,
            DeclaredSeasonsCount = declaredSeasonsCount,
            LayoutFingerprint = hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}
