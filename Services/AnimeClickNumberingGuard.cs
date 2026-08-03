using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Echoes back the numbering Jellyfin derived from the file layout onto a provider result.
/// <para>
/// Jellyfin merges a remote result over the stored item with <c>replaceData: true</c> and copies
/// the index unconditionally (<c>MediaBrowser.Providers/Manager/MetadataService.cs</c>:
/// <c>if (replaceData || !target.IndexNumber.HasValue) target.IndexNumber = source.IndexNumber;</c>).
/// The temporary item it merges from is seeded with Path, Id and ParentIndexNumber only, and a
/// refresh started with "replace all metadata" sets <c>RemoveOldMetadata</c>
/// (<c>ItemRefreshController</c>), which skips the step that would fold the stored values back in.
/// A result that reports <c>HasMetadata</c> without carrying the numbering therefore erases the
/// episode or season number parsed from the file name, for every item no other provider matched.
/// </para>
/// <para>
/// For episodes the damage compounds: once one episode of a season loses its number the season is
/// no longer contiguous, so <see cref="AnimeClickEpisodeLibraryLayout.TryGetGlobalOrdinal"/> stops
/// vouching for its own boundaries, the matcher falls under its acceptance threshold and every
/// later episode of that season silently keeps Jellyfin's placeholder title.
/// </para>
/// </summary>
internal static class AnimeClickNumberingGuard
{
    /// <summary>
    /// Carries the episode coordinates from the lookup info onto the result item.
    /// </summary>
    internal static void Preserve(Episode item, EpisodeInfo info)
    {
        item.IndexNumber = info.IndexNumber;
        item.ParentIndexNumber = info.ParentIndexNumber;
        item.IndexNumberEnd = info.IndexNumberEnd;
    }

    /// <summary>
    /// Carries the season number from the lookup info onto the result item.
    /// </summary>
    internal static void Preserve(Season item, SeasonInfo info)
    {
        item.IndexNumber = info.IndexNumber;
    }
}
