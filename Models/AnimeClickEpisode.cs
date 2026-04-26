namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents a single episode from AnimeClick's episode listing.
/// </summary>
public class AnimeClickEpisode
{
    /// <summary>Season number (null if not available from AnimeClick).</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Episode number (1-based, within season if SeasonNumber is set).</summary>
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
