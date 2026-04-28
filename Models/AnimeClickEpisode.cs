namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents a single episode from AnimeClick's episode listing.
/// </summary>
public class AnimeClickEpisode
{
    /// <summary>Season number (null if not available from AnimeClick).</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Episode number as shown by AnimeClick.</summary>
    public int Number { get; set; }

    /// <summary>Absolute episode number across the AnimeClick page.</summary>
    public int AbsoluteNumber { get; set; }

    /// <summary>Ordinal episode number inside the AnimeClick season group.</summary>
    public int SeasonOrdinalNumber { get; set; }

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

    /// <summary>Provider ID extracted from the AnimeClick episode detail URL.</summary>
    public string? ProviderId { get; set; }
}
