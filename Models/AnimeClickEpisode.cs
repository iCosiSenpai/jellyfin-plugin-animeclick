namespace AnimeClick.Plugin.Models;

/// <summary>
/// Represents one raw row from AnimeClick plus canonical coordinates derived without
/// trusting AnimeClick's season boundaries.
/// </summary>
public class AnimeClickEpisode
{
    /// <summary>Effective season number. It may be explicit or a legacy synthetic hint.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Season number read from the row before any inference.</summary>
    public int? RawSeasonNumber { get; set; }

    /// <summary>True only when <see cref="SeasonNumber"/> was inferred, never read.</summary>
    public bool SeasonNumberIsSynthetic { get; set; }

    /// <summary>Original number label, for example S2 Ep. 01, 12.5, OVA or 1-2.</summary>
    public string RawNumberLabel { get; set; } = string.Empty;

    /// <summary>Integer start number parsed from the row; 0 when no safe integer exists.</summary>
    public int Number { get; set; }

    /// <summary>Episode number read from the row before any inference.</summary>
    public int? RawEpisodeNumber { get; set; }

    /// <summary>End of a numeric range such as 1-2, when present.</summary>
    public int? NumberEnd { get; set; }

    /// <summary>
    /// Backward-compatible absolute coordinate. For regular episodes this is the canonical
    /// global ordinal, not necessarily the number printed by AnimeClick.
    /// </summary>
    public int AbsoluteNumber { get; set; }

    /// <summary>One-based position among regular episodes across the complete table.</summary>
    public int GlobalOrdinal { get; set; }

    /// <summary>One-based position among regular episodes in an explicit/effective season.</summary>
    public int SeasonOrdinalNumber { get; set; }

    /// <summary>One-based position among specials/extras in source order.</summary>
    public int SpecialOrdinalNumber { get; set; }

    /// <summary>Stable source order after all paginated rows have been merged.</summary>
    public int SourceOrder { get; set; }

    /// <summary>True for S0, OVA/OAD/ONA, recap, bonus, PV, episode 0 and non-integer rows.</summary>
    public bool IsSpecial { get; set; }

    /// <summary>
    /// True for a row that belongs to a different work listed inside this card's table — a numbered
    /// spin-off like K-On!!'s "Ura-On!!" shorts.
    /// <para>
    /// Such a row is neither an episode of this work nor one of its specials, so it must stay out of
    /// both numberings. Filing it among the specials, as a first attempt did, made it collide with
    /// the real ones: its printed number matched a request for the first special, and its presence
    /// shifted every genuine special's ordinal by one.
    /// </para>
    /// </summary>
    public bool IsForeignWork { get; set; }

    /// <summary>True for decimal, suffix, range or non-numeric labels requiring extra evidence.</summary>
    public bool HasNonStandardNumber { get; set; }

    /// <summary>True when multiple rows expose the same numeric coordinate.</summary>
    public bool NumberIsAmbiguous { get; set; }

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
