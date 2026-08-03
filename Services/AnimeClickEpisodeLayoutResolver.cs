using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AnimeClick.Plugin.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Reads what Jellyfin already knows about the destination file: the numbering of the season it
/// sits in, and its runtime. AnimeClick labels are evidence, not authority: when Jellyfin has a
/// reliable 13+11 layout, that boundary wins over a synthetic equal split, and when it has a
/// probed runtime, a row that declares an incompatible length cannot be that file.
/// </summary>
public sealed class AnimeClickEpisodeLayoutResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AnimeClickEpisodeLayoutResolver> _logger;

    public AnimeClickEpisodeLayoutResolver(
        ILibraryManager libraryManager,
        ILogger<AnimeClickEpisodeLayoutResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public AnimeClickEpisodeLibraryLayout? Resolve(string? episodePath)
    {
        if (string.IsNullOrWhiteSpace(episodePath))
        {
            return null;
        }

        try
        {
            var currentEpisode = _libraryManager.FindByPath(episodePath, isFolder: false) as Episode;
            var series = currentEpisode?.Series;
            if (series is null)
            {
                return null;
            }

            var episodes = series.GetEpisodes(
                    null!,
                    new MediaBrowser.Controller.Dto.DtoOptions(false),
                    shouldIncludeMissingEpisodes: false)
                .OfType<Episode>()
                .DistinctBy(episode => episode.Id)
                .Where(episode => episode.ParentIndexNumber is > 0 && episode.IndexNumber is >= 0)
                .ToList();

            var seasons = new Dictionary<int, AnimeClickEpisodeSeasonLayout>();
            foreach (var group in episodes.GroupBy(episode => episode.ParentIndexNumber!.Value))
            {
                var numbers = new SortedSet<int>();
                foreach (var episode in group)
                {
                    var start = episode.IndexNumber!.Value;
                    var end = Math.Max(start, episode.IndexNumberEnd ?? start);
                    if (end - start > 100)
                    {
                        continue;
                    }

                    for (var number = start; number <= end; number++)
                    {
                        numbers.Add(number);
                    }
                }

                if (numbers.Count == 0)
                {
                    continue;
                }

                var max = numbers.Max;
                var startsAtOne = numbers.Min is 0 or 1;
                var firstRegular = numbers.Min == 0 ? 0 : 1;
                var contiguous = startsAtOne
                    && Enumerable.Range(firstRegular, max - firstRegular + 1).All(numbers.Contains);

                // The year the season actually aired, straight from the files Jellyfin already
                // dated. It is what lets the sequel traversal tell a 2011 continuation from a 2024
                // one on a page that declares neither.
                var airYears = group
                    .Select(episode => episode.PremiereDate?.Year)
                    .Where(year => year is > 1900)
                    .Select(year => year!.Value)
                    .ToList();
                seasons[group.Key] = new AnimeClickEpisodeSeasonLayout(
                    group.Key,
                    max,
                    numbers.Count,
                    startsAtOne,
                    contiguous,
                    airYears.Count > 0 ? airYears.Min() : null);
            }

            return seasons.Count == 0
                ? null
                : new AnimeClickEpisodeLibraryLayout(series.Id, seasons);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: unable to inspect Jellyfin episode layout for {Path}", episodePath);
            return null;
        }
    }

    /// <summary>
    /// Returns the runtime in minutes Jellyfin already probed for an episode file, when it has
    /// one. Unlike the layout this is read for every match, season-specific pages included,
    /// because it is the one piece of evidence that does not depend on how the seasons are cut.
    /// </summary>
    public double? GetKnownRuntimeMinutes(string? episodePath)
    {
        if (string.IsNullOrWhiteSpace(episodePath))
        {
            return null;
        }

        try
        {
            var episode = _libraryManager.FindByPath(episodePath, isFolder: false) as Episode;
            var ticks = episode?.RunTimeTicks;
            return ticks is > 0 ? TimeSpan.FromTicks(ticks.Value).TotalMinutes : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnimeClick: unable to read the Jellyfin runtime for {Path}", episodePath);
            return null;
        }
    }
}

public sealed record AnimeClickEpisodeSeasonLayout(
    int SeasonNumber,
    int MaximumEpisodeNumber,
    int KnownEpisodeCount,
    bool StartsAtOne,
    bool IsContiguous,
    int? FirstAirYear = null);

public sealed class AnimeClickEpisodeLibraryLayout
{
    public AnimeClickEpisodeLibraryLayout(
        Guid seriesId,
        IReadOnlyDictionary<int, AnimeClickEpisodeSeasonLayout> seasons)
    {
        SeriesId = seriesId;
        Seasons = seasons;
    }

    public Guid SeriesId { get; }

    public IReadOnlyDictionary<int, AnimeClickEpisodeSeasonLayout> Seasons { get; }

    public bool TryGetGlobalOrdinal(
        int seasonNumber,
        int episodeNumber,
        out int globalOrdinal,
        out bool reliable)
    {
        globalOrdinal = 0;
        reliable = false;
        if (seasonNumber <= 0 || episodeNumber <= 0 || !Seasons.TryGetValue(seasonNumber, out var target))
        {
            return false;
        }

        var offset = 0;
        for (var season = 1; season < seasonNumber; season++)
        {
            if (!Seasons.TryGetValue(season, out var previous)
                || !previous.StartsAtOne
                || !previous.IsContiguous)
            {
                return false;
            }

            offset += previous.MaximumEpisodeNumber;
        }

        globalOrdinal = offset + episodeNumber;
        reliable = target.StartsAtOne
            && target.IsContiguous
            && episodeNumber <= target.MaximumEpisodeNumber;
        return globalOrdinal > 0;
    }

    public string Describe() => string.Join(
        "+",
        Seasons.OrderBy(pair => pair.Key).Select(pair =>
            $"S{pair.Key}:{pair.Value.MaximumEpisodeNumber}"));

    /// <summary>
    /// The year each season aired, for the seasons the library has dated. This is the evidence the
    /// sequel traversal needs on the AnimeClick pages that declare no relation type at all.
    /// </summary>
    /// <returns>Season number to first air year, skipping the seasons with no dates.</returns>
    public IReadOnlyDictionary<int, int> GetSeasonAirYears()
        => Seasons
            .Where(pair => pair.Value.FirstAirYear is > 1900)
            .ToDictionary(pair => pair.Key, pair => pair.Value.FirstAirYear!.Value);

    /// <summary>
    /// True when the library holds a single season, numbered above one, whose episodes the card
    /// accounts for exactly.
    /// <para>
    /// This is the shape of a standalone work filed under a later season number because the rest
    /// of its franchise lives in other folders — "D4DJ All Mix" sits in Season 02 with its own
    /// twelve episodes and its own AnimeClick card. There is no season one to measure an offset
    /// against, so the only sane reading is the flat one. The exact count is what makes it safe:
    /// were the card a longer timeline, row one would belong to a cour the library does not hold.
    /// </para>
    /// </summary>
    /// <param name="seasonNumber">The season the file sits in.</param>
    /// <param name="regularRowCount">Regular, non-special rows the AnimeClick card lists.</param>
    /// <returns>True when the card can be read as that season, numbered from one.</returns>
    public bool IsStandaloneSeason(int seasonNumber, int regularRowCount)
    {
        if (seasonNumber <= 1 || regularRowCount <= 0 || Seasons.Count != 1)
        {
            return false;
        }

        return Seasons.TryGetValue(seasonNumber, out var only)
            && only.StartsAtOne
            && only.IsContiguous
            && only.KnownEpisodeCount == regularRowCount;
    }
}

public enum AnimeClickEpisodeLayoutMode
{
    Auto,
    Flat,
    Explicit,
    Boundaries
}

public sealed class AnimeClickEpisodeLayoutOverride
{
    public AnimeClickEpisodeLayoutOverride(
        AnimeClickEpisodeLayoutMode mode,
        IReadOnlyList<int>? cumulativeBoundaries = null)
    {
        Mode = mode;
        CumulativeBoundaries = cumulativeBoundaries ?? [];
    }

    public AnimeClickEpisodeLayoutMode Mode { get; }

    public IReadOnlyList<int> CumulativeBoundaries { get; }

    public bool TryGetGlobalOrdinal(int seasonNumber, int episodeNumber, out int globalOrdinal)
    {
        globalOrdinal = 0;
        if (episodeNumber <= 0)
        {
            return false;
        }

        if (Mode == AnimeClickEpisodeLayoutMode.Flat)
        {
            if (seasonNumber != 1)
            {
                return false;
            }

            globalOrdinal = episodeNumber;
            return true;
        }

        if (Mode != AnimeClickEpisodeLayoutMode.Boundaries
            || seasonNumber <= 0
            || seasonNumber > CumulativeBoundaries.Count)
        {
            return false;
        }

        var start = seasonNumber == 1 ? 0 : CumulativeBoundaries[seasonNumber - 2];
        var end = CumulativeBoundaries[seasonNumber - 1];
        globalOrdinal = start + episodeNumber;
        return globalOrdinal > start && globalOrdinal <= end;
    }
}

public static class AnimeClickEpisodeLayoutOverrideParser
{
    /// <summary>
    /// Parses one override per line: anime-id=flat, anime-id=explicit, or
    /// anime-id=13,24 where numbers are cumulative season boundaries.
    /// Invalid lines are ignored and automatic matching remains active.
    /// </summary>
    public static AnimeClickEpisodeLayoutOverride? ParseFor(string? value, string animeClickId)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !AnimeClickClient.TryNormalizeAnimeClickId(animeClickId, out var normalizedTarget))
        {
            return null;
        }

        var targetIdentity = GetStableNumericIdentity(normalizedTarget);
        foreach (var rawLine in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var id = line[..separator].Trim();
            if (!AnimeClickClient.TryNormalizeAnimeClickId(id, out var normalizedId)
                || !string.Equals(
                    GetStableNumericIdentity(normalizedId),
                    targetIdentity,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var layout = line[(separator + 1)..].Trim();
            if (string.Equals(layout, "flat", StringComparison.OrdinalIgnoreCase))
            {
                return new AnimeClickEpisodeLayoutOverride(AnimeClickEpisodeLayoutMode.Flat);
            }

            if (string.Equals(layout, "explicit", StringComparison.OrdinalIgnoreCase))
            {
                return new AnimeClickEpisodeLayoutOverride(AnimeClickEpisodeLayoutMode.Explicit);
            }

            var boundaries = new List<int>();
            var previous = 0;
            foreach (var token in layout.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var boundary)
                    || boundary <= previous
                    || boundary > 10000)
                {
                    boundaries.Clear();
                    break;
                }

                boundaries.Add(boundary);
                previous = boundary;
            }

            if (boundaries.Count is > 0 and <= 100)
            {
                return new AnimeClickEpisodeLayoutOverride(
                    AnimeClickEpisodeLayoutMode.Boundaries,
                    boundaries);
            }
        }

        return null;
    }

    private static string GetStableNumericIdentity(string normalizedId)
    {
        var separator = normalizedId.IndexOf('/');
        return separator < 0 ? normalizedId : normalizedId[..separator];
    }
}
