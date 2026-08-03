using Jellyfin.Data.Enums;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Maps the coarse role group produced by the AnimeClick parsers onto a Jellyfin person kind.
/// The precise Italian role text travels separately in <c>PersonInfo.Role</c>, so the kind only
/// has to be semantically compatible — and storable.
/// </summary>
internal static class AnimeClickPersonKinds
{
    /// <summary>
    /// Returns the Jellyfin kind for an AnimeClick role group, defaulting to
    /// <see cref="PersonKind.Unknown"/> for the many granular roles Jellyfin has no name for.
    /// </summary>
    internal static PersonKind Map(string? type) => type switch
    {
        "Director" => PersonKind.Director,
        "Writer" => PersonKind.Writer,
        "Composer" => PersonKind.Composer,
        "Producer" => PersonKind.Producer,

        // Deliberately not PersonKind.Artist. Jellyfin's people repository drops Artist and
        // AlbumArtist before inserting anything (Jellyfin.Server.Implementations/Item/
        // PeopleRepository.cs: ".Where(e => e.Type is not PersonKind.Artist && e.Type is not
        // PersonKind.AlbumArtist)"), so character designers, art directors and the OP/ED
        // performers were sent on every refresh and silently never stored. Unknown is what the
        // plugin already uses for the neighbouring art roles and keeps the Italian role visible.
        "Artist" => PersonKind.Unknown,

        "Editor" => PersonKind.Editor,
        "Colorist" => PersonKind.Colorist,
        "Engineer" => PersonKind.Engineer,
        "Actor" => PersonKind.Actor,
        _ => PersonKind.Unknown
    };
}
