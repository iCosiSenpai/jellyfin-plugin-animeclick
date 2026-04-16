namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents a single episode from AnimeClick's episode listing.
/// </summary>
public class AnimeClickEpisode
{
    /// <summary>Episode number (1-based).</summary>
    public int Number { get; set; }

    /// <summary>Italian title of the episode.</summary>
    public string? Title { get; set; }

    /// <summary>Original (Japanese) title if available.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>Air date string from AnimeClick.</summary>
    public string? AirDate { get; set; }

    /// <summary>Duration in minutes.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Detail page URL on AnimeClick.</summary>
    public string? DetailUrl { get; set; }
}
