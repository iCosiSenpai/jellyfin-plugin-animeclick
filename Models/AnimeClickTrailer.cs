namespace AnimeClick.Plugin.Models;

/// <summary>
/// A trailer, teaser or promotional video explicitly labelled as such on the
/// AnimeClick multimedia page.
/// </summary>
public sealed class AnimeClickTrailer
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}
