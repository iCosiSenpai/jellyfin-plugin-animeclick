using System.Globalization;
using System.Text.Json;

namespace AnimeClick.Plugin.Models;

/// <summary>
/// One AniList entry — a single film or a single cour — reduced to what the plugin can use. AniList
/// has no key and no Italian text: it completes cast, studio, dates, score, trailer and artwork, and
/// its sequel chain tells which year each season aired.
/// </summary>
public sealed class AnimeClickAniListMedia
{
    public int Id { get; set; }
    public string? Format { get; set; }
    public int? Episodes { get; set; }
    public string? Status { get; set; }
    public int? SeasonYear { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? AverageScore { get; set; }
    public string? RomajiTitle { get; set; }
    public List<string> Studios { get; set; } = [];
    public string? TrailerYouTubeId { get; set; }
    public string? CoverUrl { get; set; }
    public string? BannerUrl { get; set; }
    public List<AnimeClickAniListPerson> Cast { get; set; } = [];
    public List<AnimeClickAniListPerson> Staff { get; set; } = [];
    public List<AnimeClickAniListRelation> Relations { get; set; } = [];

    /// <summary>The year the entry started airing, from the season or else from the start date.</summary>
    public int? Year => SeasonYear ?? StartDate?.Year;

    /// <summary>Reads the <c>Media</c> object of an AniList response; null when it is not an anime entry.</summary>
    public static AnimeClickAniListMedia? Parse(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Object || Int(media, "id") is not { } id) return null;
        var result = new AnimeClickAniListMedia
        {
            Id = id,
            Format = Text(media, "format"),
            Episodes = Int(media, "episodes"),
            Status = Text(media, "status"),
            SeasonYear = Int(media, "seasonYear"),
            StartDate = Date(Child(media, "startDate")),
            EndDate = Date(Child(media, "endDate")),
            AverageScore = Int(media, "averageScore"),
            RomajiTitle = Text(Child(media, "title"), "romaji"),
            CoverUrl = Text(Child(media, "coverImage"), "extraLarge") ?? Text(Child(media, "coverImage"), "large"),
            BannerUrl = Text(media, "bannerImage")
        };

        var trailer = Child(media, "trailer");
        if (string.Equals(Text(trailer, "site"), "youtube", StringComparison.OrdinalIgnoreCase)) result.TrailerYouTubeId = Text(trailer, "id");

        foreach (var studio in Items(Child(Child(media, "studios"), "nodes")))
            if (Text(studio, "name") is { } name) result.Studios.Add(name);

        foreach (var edge in Items(Child(Child(media, "characters"), "edges")))
        {
            var character = Text(Child(Child(edge, "node"), "name"), "full");
            foreach (var actor in Items(Child(edge, "voiceActors")))
            {
                if (Text(Child(actor, "name"), "full") is not { } name) continue;
                result.Cast.Add(new AnimeClickAniListPerson { Name = name, Role = character, ImageUrl = Text(Child(actor, "image"), "large") });
            }
        }

        foreach (var edge in Items(Child(Child(media, "staff"), "edges")))
        {
            if (Text(Child(Child(edge, "node"), "name"), "full") is not { } name) continue;
            result.Staff.Add(new AnimeClickAniListPerson
            {
                Name = name, Role = Text(edge, "role"), ImageUrl = Text(Child(Child(edge, "node"), "image"), "large")
            });
        }

        foreach (var edge in Items(Child(Child(media, "relations"), "edges")))
        {
            var node = Child(edge, "node");
            if (Int(node, "id") is not { } relatedId || Text(node, "type") is not (null or "ANIME")) continue;
            result.Relations.Add(new AnimeClickAniListRelation
            {
                Type = Text(edge, "relationType") ?? string.Empty, Id = relatedId, Format = Text(node, "format"),
                SeasonYear = Int(node, "seasonYear"), Episodes = Int(node, "episodes")
            });
        }

        return result;
    }

    private static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Int(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static DateTime? Date(JsonElement element)
    {
        if (Int(element, "year") is not { } year || year < 1900) return null;
        var month = Int(element, "month") is { } m and >= 1 and <= 12 ? m : 1;
        var day = Int(element, "day") is { } d && d >= 1 && d <= DateTime.DaysInMonth(year, month) ? d : 1;
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"AniList {Id} {Format} {Year}");
}

public sealed class AnimeClickAniListPerson
{
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class AnimeClickAniListRelation
{
    public string Type { get; set; } = string.Empty;
    public int Id { get; set; }
    public string? Format { get; set; }
    public int? SeasonYear { get; set; }
    public int? Episodes { get; set; }
}
