using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Reads a library season and how many episodes it really holds. Shared by the community, which keys a
/// season by its episode count, and by AniList, which checks a sequel against it.
/// </summary>
public static class AnimeClickLibrarySeasons
{
    /// <summary>The library season a metadata request is about, through its folder.</summary>
    public static Season? Find(ILibraryManager? library, string? path)
    {
        if (library is null || string.IsNullOrWhiteSpace(path)) return null;
        try { return library.FindByPath(path, isFolder: true) as Season; }
        catch (Exception) { return null; }
    }

    /// <summary>Episodes of a season from the library, or null when it cannot be read.</summary>
    public static int? CountEpisodes(ILibraryManager? library, Season season)
    {
        if (library is null) return null;
        try
        {
            return AnimeClickCommunityService.CountEpisodes(library.GetItemList(new InternalItemsQuery
            {
                ParentId = season.Id, IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsVirtualItem = false
            }).OfType<Episode>().DistinctBy(episode => episode.Id));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Episodes of the season at a path; null when the season or its episodes cannot be read.</summary>
    public static int? CountEpisodes(ILibraryManager? library, string? path)
        => Find(library, path) is { } season ? CountEpisodes(library, season) : null;
}
