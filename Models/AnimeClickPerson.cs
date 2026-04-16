namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents a person (actor, director, writer, etc.) from AnimeClick.
/// </summary>
public class AnimeClickPerson
{
    /// <summary>Person's name (e.g. "Junko Takeuchi").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Role type for Jellyfin: "Actor", "Director", "Writer", "Composer", etc.
    /// </summary>
    public string Type { get; set; } = "Actor";

    /// <summary>
    /// For actors: the character name they voice (e.g. "Naruto Uzumaki").
    /// For staff: the specific role title (e.g. "Character Design").
    /// </summary>
    public string? Role { get; set; }

    /// <summary>Profile image URL from AnimeClick.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>AnimeClick author/actor ID path (e.g. "/autore/35/emanuela-pacotto").</summary>
    public string? Id { get; set; }
}
