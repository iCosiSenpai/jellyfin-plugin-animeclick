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

    /// <summary>
    /// Number of broadcast seasons/cours AnimeClick declares for this title
    /// (parsed from the &lt;dt&gt;Stagioni&lt;/dt&gt; list, e.g. "Autunno (2015) Primavera (2016)" → 2).
    /// 0 when AnimeClick does not declare any. Used by <see cref="AnimeClickHtmlParser.ParseEpisodesPage"/>
    /// to synthesise <see cref="AnimeClickEpisode.SeasonNumber"/> when the /episodi table lacks
    /// explicit <c>S1/S2 Ep.</c> prefixes (e.g. The Asterisk War, 24 eps listed as <c>Ep. 01</c>–<c>Ep. 24</c>).
    /// </summary>
    public int SeasonsCount { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public string? OfficialRating { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> ProductionLocations { get; set; } = [];
    public List<AnimeClickPerson> People { get; set; } = [];
    public List<AnimeClickEpisode> Episodes { get; set; } = [];
    public List<AnimeClickRelation> Relations { get; set; } = [];
    public List<AnimeClickThemeSong> ThemeSongs { get; set; } = [];
    public List<AnimeClickTrailer> Trailers { get; set; } = [];
    public bool MultimediaLoaded { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merges theme songs coming from a secondary page into <see cref="ThemeSongs"/>.
    /// The sigle are published in two unrelated places — the /multimedia video list and the
    /// /staff role sections — and either one can be the only source for a given title, so both
    /// are read and the first entry claiming a slot (same type and number, or same type and
    /// title) wins.
    /// </summary>
    public void AddThemeSongs(IEnumerable<AnimeClickThemeSong> songs)
    {
        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Title))
            {
                continue;
            }

            var alreadyKnown = ThemeSongs.Exists(existing =>
                string.Equals(existing.Type, song.Type, StringComparison.OrdinalIgnoreCase)
                && (existing.Number == song.Number
                    || string.Equals(existing.Title, song.Title, StringComparison.OrdinalIgnoreCase)));
            if (!alreadyKnown)
            {
                ThemeSongs.Add(song);
            }
        }
    }
}
