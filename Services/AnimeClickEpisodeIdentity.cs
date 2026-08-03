namespace AnimeClick.Plugin.Services;

/// <summary>
/// Decides which AnimeClick card an episode is matched against, and which one answers for the
/// external synopsis sources.
/// <para>
/// The two questions have different answers on purpose. AnimeClick publishes most franchises as
/// one card per cour, so the card that lists an episode may cover a single season and number it
/// from 1. TheTVDB and TMDB instead index the whole show, so their coordinates must come from the
/// series-level identity paired with the real Jellyfin season number: resolving them from a season
/// card and then asking for season 1 returns a genuine synopsis belonging to another episode.
/// </para>
/// </summary>
internal readonly record struct AnimeClickEpisodeIdentity(
    string? MatchingId,
    bool IsSeasonSpecific,
    string? ExternalSourceId,
    bool ExternalNumbersRestartAtOne)
{
    /// <summary>
    /// Resolves the identity from the IDs Jellyfin carries on the series and on the season.
    /// A season identity is the more specific one and wins for matching, which is what makes a
    /// hand-written ID on Season 2 the escape hatch when the sequel traversal cannot prove the
    /// chain. The external sources keep following the series whenever it has an ID of its own.
    /// </summary>
    internal static AnimeClickEpisodeIdentity Resolve(string? seriesAnimeClickId, string? seasonAnimeClickId)
    {
        // Blank is absent: an empty provider ID must never travel on as if it were an identity.
        var series = string.IsNullOrWhiteSpace(seriesAnimeClickId) ? null : seriesAnimeClickId;
        var season = string.IsNullOrWhiteSpace(seasonAnimeClickId) ? null : seasonAnimeClickId;

        return new AnimeClickEpisodeIdentity(
            MatchingId: season ?? series,
            IsSeasonSpecific: season is not null,
            ExternalSourceId: series ?? season,
            ExternalNumbersRestartAtOne: series is null);
    }
}
