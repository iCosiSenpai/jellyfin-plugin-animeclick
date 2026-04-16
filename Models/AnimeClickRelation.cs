namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents a related anime (sequel, prequel, spin-off) from AnimeClick.
/// </summary>
public class AnimeClickRelation
{
    /// <summary>Title of the related anime.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>AnimeClick ID (e.g. "73/naruto-shippuden").</summary>
    public string AnimeClickId { get; set; } = string.Empty;

    /// <summary>Full URL on AnimeClick.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Relation type: Sequel, Prequel, Spin-off, Opera derivata, etc.</summary>
    public string RelationType { get; set; } = string.Empty;

    /// <summary>Format: Serie TV, Film, OAV, Special, etc.</summary>
    public string? Format { get; set; }

    /// <summary>Production year.</summary>
    public int? Year { get; set; }
}
