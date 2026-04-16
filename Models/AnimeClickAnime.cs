using System;
using System.Collections.Generic;

namespace AnimeClick.Plugin.Models;

public class AnimeClickAnime
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? ImageUrl { get; set; }
    public string? BannerUrl { get; set; }
    public DateTimeOffset? PremiereDate { get; set; }
    public float? CommunityRating { get; set; }
    public int? ProductionYear { get; set; }
    public int? RatingCount { get; set; }
    public int? EpisodeCount { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public string? OfficialRating { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<AnimeClickPerson> People { get; set; } = [];
    public List<AnimeClickEpisode> Episodes { get; set; } = [];
    public List<AnimeClickRelation> Relations { get; set; } = [];
    public List<AnimeClickThemeSong> ThemeSongs { get; set; } = [];
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
