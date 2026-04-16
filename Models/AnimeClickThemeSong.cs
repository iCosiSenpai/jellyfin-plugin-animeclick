namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents an opening or ending theme song from AnimeClick.
/// </summary>
public class AnimeClickThemeSong
{
    /// <summary>Type: "Opening" or "Ending".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Number (e.g. 1 for OP1, 2 for ED2).</summary>
    public int Number { get; set; } = 1;

    /// <summary>Song title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Artist/band name.</summary>
    public string? Artist { get; set; }

    /// <summary>Formatted display string, e.g. "OP1: GO!!! (FLOW)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist)
            ? $"{(Type == "Opening" ? "OP" : "ED")}{Number}: {Title}"
            : $"{(Type == "Opening" ? "OP" : "ED")}{Number}: {Title} ({Artist})";
}
