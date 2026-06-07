using System;
using System.Collections.Generic;
using System.Linq;
using AnimeClick.Plugin.Models;

namespace AnimeClick.Plugin.Services;

public static class AnimeClickEpisodeMatcher
{
    public static AnimeClickEpisodeMatch Match(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        int? jellyfinSeasonNumber,
        int jellyfinEpisodeNumber)
    {
        if (episodes.Count == 0 || jellyfinEpisodeNumber <= 0)
        {
            return AnimeClickEpisodeMatch.None("none");
        }

        if (jellyfinSeasonNumber.HasValue)
        {
            var seasonEpisodes = episodes
                .Where(e => e.SeasonNumber == jellyfinSeasonNumber.Value)
                .ToList();

            if (seasonEpisodes.Count > 0)
            {
                var seasonOrdinal = seasonEpisodes.FirstOrDefault(e => e.SeasonOrdinalNumber == jellyfinEpisodeNumber);
                if (seasonOrdinal is not null)
                {
                    return AnimeClickEpisodeMatch.Found(seasonOrdinal, "seasonOrdinal");
                }

                var sameSeasonAbsolute = seasonEpisodes.FirstOrDefault(e => e.AbsoluteNumber == jellyfinEpisodeNumber);
                if (sameSeasonAbsolute is not null)
                {
                    return AnimeClickEpisodeMatch.Found(sameSeasonAbsolute, "absolute");
                }

                return AnimeClickEpisodeMatch.None("seasonGroupNoMatch");
            }
        }

        var exact = episodes.FirstOrDefault(e =>
            (!jellyfinSeasonNumber.HasValue || e.SeasonNumber == jellyfinSeasonNumber.Value) &&
            e.Number == jellyfinEpisodeNumber);
        if (exact is not null)
        {
            return AnimeClickEpisodeMatch.Found(exact, "same-page");
        }

        if (!jellyfinSeasonNumber.HasValue || jellyfinSeasonNumber.Value <= 1)
        {
            var absolute = episodes.FirstOrDefault(e => e.AbsoluteNumber == jellyfinEpisodeNumber);
            if (absolute is not null)
            {
                return AnimeClickEpisodeMatch.Found(absolute, "absolute");
            }
        }

        return AnimeClickEpisodeMatch.None("none");
    }
}

public sealed class AnimeClickEpisodeMatch
{
    private AnimeClickEpisodeMatch(AnimeClickEpisode? episode, string strategy)
    {
        Episode = episode;
        Strategy = strategy;
    }

    public AnimeClickEpisode? Episode { get; }

    public string Strategy { get; }

    public bool Success => Episode is not null;

    public static AnimeClickEpisodeMatch Found(AnimeClickEpisode episode, string strategy)
        => new(episode, strategy);

    public static AnimeClickEpisodeMatch None(string strategy)
        => new(null, strategy);
}
