using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AnimeClick.Plugin.Models;

namespace AnimeClick.Plugin.Services;

public static class AnimeClickEpisodeMatcher
{
    /// <summary>
    /// Score a candidate is held to when the AnimeClick table contradicts itself: below the
    /// acceptance threshold of 70, but close enough that title evidence can still lift it over.
    /// </summary>
    private const int UncorroboratedScoreCap = 55;

    public static AnimeClickEpisodeMatch Match(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        int? jellyfinSeasonNumber,
        int jellyfinEpisodeNumber)
        => Match(
            episodes,
            new AnimeClickEpisodeMatchContext(jellyfinSeasonNumber, jellyfinEpisodeNumber));

    public static AnimeClickEpisodeMatch Match(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        AnimeClickEpisodeMatchContext context)
    {
        if (episodes.Count == 0
            || context.JellyfinEpisodeNumber < 0
            || context.JellyfinSeasonNumber is < 0)
        {
            return AnimeClickEpisodeMatch.None("none", "invalid or empty episode request");
        }

        var ordered = episodes
            .Where(episode => !episode.IsForeignWork)
            .OrderBy(episode => episode.SourceOrder)
            .ToList();
        if (ordered.Count == 0)
        {
            return AnimeClickEpisodeMatch.None("none", "catalog contains no rows belonging to this work");
        }

        // One Jellyfin file spanning multiple episodes must never silently inherit the
        // title of only its first part. Validate the range before any persisted anchor:
        // a file may have changed from E01 to E01-E02 since the provider ID was stored.
        if (context.JellyfinEpisodeNumberEnd is > 0
            && context.JellyfinEpisodeNumberEnd != context.JellyfinEpisodeNumber)
        {
            var range = ordered.Where(episode =>
                    episode.Number == context.JellyfinEpisodeNumber
                    && episode.NumberEnd == context.JellyfinEpisodeNumberEnd
                    && SeasonCompatible(episode, context.JellyfinSeasonNumber))
                .ToList();
            return range.Count == 1
                ? AnimeClickEpisodeMatch.Found(range[0], "explicitRange", 0.98, "matching double-episode range")
                : AnimeClickEpisodeMatch.None("multiEpisodeAmbiguous", "no unique AnimeClick range for a multi-episode file");
        }

        // The ID written by a previous high-confidence match is the strongest anchor, but only
        // after proving that it still belongs to the requested season. This matters when a library
        // now contains only S2 while the series ID still points to an unseasoned S1 card: the old
        // row ID is real, yet applying it would confidently copy S1 titles onto S2.
        if (!string.IsNullOrWhiteSpace(context.ExistingProviderId))
        {
            var anchored = ordered.Where(episode => AnimeClickEpisodeProviderId.Equals(
                    episode.ProviderId,
                    context.ExistingProviderId))
                .ToList();
            if (anchored.Count == 1)
            {
                if (!PersistedAnchorCompatible(anchored[0], context))
                {
                    return AnimeClickEpisodeMatch.None(
                        "staleProviderId",
                        "existing AnimeClick episode ID is not compatible with the requested season");
                }

                return AnimeClickEpisodeMatch.Found(anchored[0], "providerId", 1, "existing AnimeClick episode ID");
            }

            if (anchored.Count > 1)
            {
                return AnimeClickEpisodeMatch.None("ambiguousProviderId", "provider ID occurs more than once");
            }
        }

        // Specials use their own coordinate space. Regular layout overrides must never
        // turn S00E01 into global episode 1 or suppress an explicit special match.
        //
        // Episode zero of a regular season belongs here too. The parser files any row whose
        // printed number is not positive as a special (see ParseEpisodesPage: episodeNumber <= 0
        // makes it one), so a prologue that the library stores as S01E00 — a real shape, from
        // Kimi ni Todoke S02E00 to Dead Dead Demons S01E00 — can only ever be found among the
        // special rows. It used to be rejected outright before any lookup.
        if (context.JellyfinSeasonNumber == 0 || context.JellyfinEpisodeNumber == 0)
        {
            return MatchSpecial(ordered, context);
        }

        if (context.LayoutOverride?.Mode == AnimeClickEpisodeLayoutMode.Explicit)
        {
            return MatchExplicitOnly(ordered, context);
        }

        if (context.LayoutOverride is not null
            && context.JellyfinSeasonNumber.HasValue
            && context.LayoutOverride.TryGetGlobalOrdinal(
                context.JellyfinSeasonNumber.Value,
                context.JellyfinEpisodeNumber,
                out var overrideOrdinal))
        {
            return MatchUniqueGlobal(
                ordered,
                overrideOrdinal,
                context.LayoutOverride.Mode == AnimeClickEpisodeLayoutMode.Flat
                    ? "overrideFlat"
                    : "overrideBoundaries",
                "manual layout override");
        }

        var candidates = new Dictionary<AnimeClickEpisode, MatchCandidate>();
        var requestedSeason = context.JellyfinSeasonNumber;

        if (requestedSeason.HasValue)
        {
            var seasonGroup = ordered.Where(episode =>
                    !episode.IsSpecial
                    && (episode.RawSeasonNumber == requestedSeason.Value
                        || episode.SeasonNumber == requestedSeason.Value))
                .ToList();
            var canUseSeasonOrdinal = CanUseSeasonOrdinal(ordered, seasonGroup, requestedSeason.Value);
            var syntheticSeasonGroup = seasonGroup.Count > 0
                && seasonGroup.All(episode => episode.SeasonNumberIsSynthetic);
            foreach (var episode in seasonGroup)
            {
                if (canUseSeasonOrdinal
                    && episode.SeasonOrdinalNumber == context.JellyfinEpisodeNumber)
                {
                    AddCandidate(
                        candidates,
                        episode,
                        syntheticSeasonGroup ? 55 : 125,
                        syntheticSeasonGroup ? "declaredEqualSplit" : "seasonOrdinal",
                        syntheticSeasonGroup
                            ? "equal split requires title or topology corroboration"
                            : "ordinal in AnimeClick season group");
                }

                if (!syntheticSeasonGroup
                    && !episode.HasNonStandardNumber
                    && !episode.NumberIsAmbiguous
                    && episode.RawEpisodeNumber == context.JellyfinEpisodeNumber)
                {
                    AddCandidate(candidates, episode, 105, "absolute", "number in matching AnimeClick season group");
                }
            }
        }

        if (context.IsSeasonSpecificPage)
        {
            AddGlobalCandidate(
                candidates,
                ordered,
                context.JellyfinEpisodeNumber,
                120,
                "seasonPageOrdinal",
                "resolved AnimeClick sequel page");
        }

        if (requestedSeason.HasValue
            && context.LibraryLayout?.TryGetGlobalOrdinal(
                requestedSeason.Value,
                context.JellyfinEpisodeNumber,
                out var libraryOrdinal,
                out var reliable) == true)
        {
            AddGlobalCandidate(
                candidates,
                ordered,
                libraryOrdinal,
                reliable ? 125 : 55,
                "libraryBoundary",
                reliable
                    ? "contiguous Jellyfin season boundaries"
                    : "partial Jellyfin season boundaries");
        }

        var regular = ordered.Where(episode => !episode.IsSpecial && episode.GlobalOrdinal > 0).ToList();
        var hasExplicitRegularSeasons = regular.Any(episode => episode.RawSeasonNumber is > 0);
        var allSynthetic = regular.Count > 0 && regular.All(episode => episode.SeasonNumberIsSynthetic);

        // Legacy equal split remains a low-confidence fallback only when no explicit
        // AnimeClick season exists and no better Jellyfin topology was available.
        if (requestedSeason is > 0
            && context.DeclaredSeasonsCount is > 1
            && !hasExplicitRegularSeasons
            && regular.Count % context.DeclaredSeasonsCount.Value == 0)
        {
            var perSeason = regular.Count / context.DeclaredSeasonsCount.Value;
            var declaredOrdinal = ((requestedSeason.Value - 1) * perSeason) + context.JellyfinEpisodeNumber;
            if (context.JellyfinEpisodeNumber <= perSeason)
            {
                AddGlobalCandidate(
                    candidates,
                    ordered,
                    declaredOrdinal,
                    55,
                    "declaredEqualSplit",
                    "equal split requires title or topology corroboration");
            }
        }

        if (requestedSeason == 1 && allSynthetic)
        {
            AddGlobalCandidate(
                candidates,
                ordered,
                context.JellyfinEpisodeNumber,
                96,
                "syntheticAbsolute",
                "flat Jellyfin season crossing a synthetic AnimeClick boundary");
        }

        if ((!requestedSeason.HasValue || requestedSeason <= 1) && !hasExplicitRegularSeasons)
        {
            AddGlobalCandidate(
                candidates,
                ordered,
                context.JellyfinEpisodeNumber,
                82,
                "globalOrdinal",
                "canonical flat timeline");
        }

        if (!requestedSeason.HasValue || requestedSeason <= 1)
        {
            foreach (var episode in regular.Where(episode =>
                         !episode.RawSeasonNumber.HasValue
                         && !episode.HasNonStandardNumber
                         && !episode.NumberIsAmbiguous
                         && episode.RawEpisodeNumber == context.JellyfinEpisodeNumber))
            {
                AddCandidate(candidates, episode, 78, "same-page", "unseasoned AnimeClick row number");
            }
        }

        // A table carrying more regular rows than the episode count AnimeClick itself declares is
        // internally inconsistent, and the surplus row is often the first one: on
        // kimi-ni-todoke-ii-tv there are 13 rows against 12 declared, so both the printed numbers
        // and the row positions sit one step ahead of the library and every title lands on the
        // previous episode. That used to be accepted at 0.96 confidence — which is how an entire
        // season gets silently mis-titled, with each episode carrying a plausible wrong name.
        //
        // In that state neither position nor AnimeClick's own numbering is evidence, so every
        // candidate is capped below the acceptance threshold. Capping instead of discarding keeps
        // the mapping usable when the file's own title corroborates it, and leaves the episode
        // untouched when nothing does: no metadata beats confidently wrong metadata. The
        // provider-ID anchor above is unaffected, because identity is not a positional guess.
        var regularRowCount = ordered.Count(episode => !episode.IsSpecial);
        if (context.DeclaredEpisodeCount is > 0
            && regularRowCount > context.DeclaredEpisodeCount.Value)
        {
            foreach (var episode in candidates.Keys.ToList())
            {
                var candidate = candidates[episode];
                if (candidate.Score > UncorroboratedScoreCap)
                {
                    candidates[episode] = candidate with
                    {
                        Score = UncorroboratedScoreCap,
                        Reason = "table carries more rows than AnimeClick declares; needs corroboration"
                    };
                }
            }
        }

        // A row that declares a length incompatible with the file cannot be that file, whatever
        // the numbers say. This is what a short-form broadcast recut for streaming looks like:
        // AnimeClick documents Saiki K. as 120 rows of 5', while the library holds the 24 Netflix
        // episodes of 24' that were cut from them, and every positional strategy still agrees on
        // a row — so a wrong identity used to be accepted at full confidence, taking the runtime
        // with it. Capping instead of discarding keeps the mapping usable when the file's own
        // title corroborates it. Multi-episode files are exempt: there one row legitimately
        // accounts for a fraction of the runtime.
        if (context.LibraryRuntimeMinutes is > 0 && !context.JellyfinEpisodeNumberEnd.HasValue)
        {
            foreach (var episode in candidates.Keys.ToList())
            {
                var candidate = candidates[episode];
                if (candidate.Score > UncorroboratedScoreCap
                    && !episode.NumberEnd.HasValue
                    && IsRuntimeIncompatible(episode.DurationMinutes, context.LibraryRuntimeMinutes.Value))
                {
                    candidates[episode] = candidate with
                    {
                        Score = UncorroboratedScoreCap,
                        Reason = string.Create(
                            CultureInfo.InvariantCulture,
                            $"row declares {episode.DurationMinutes}' against a {context.LibraryRuntimeMinutes.Value:0.#}' file; needs corroboration")
                    };
                }
            }
        }

        ApplyTitleEvidence(candidates, context.JellyfinTitle);
        if (candidates.Count == 0)
        {
            var numberedExtra = MatchNumberedExtra(ordered, context);
            if (numberedExtra.Episode is not null)
            {
                return numberedExtra;
            }

            var requestedGroupExists = requestedSeason.HasValue
                && ordered.Any(episode => episode.RawSeasonNumber == requestedSeason.Value
                    || episode.SeasonNumber == requestedSeason.Value);
            return AnimeClickEpisodeMatch.None(
                requestedGroupExists ? "seasonGroupNoMatch" : "none",
                requestedGroupExists ? "season group exists but has no safe candidate" : "no safe candidate");
        }

        var ranked = candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Episode.SourceOrder)
            .ToList();
        var best = ranked[0];
        if (best.Score < 70)
        {
            return AnimeClickEpisodeMatch.None("lowConfidence", best.Reason);
        }

        if (ranked.Count > 1
            && ranked[1].Episode != best.Episode
            && best.Score - ranked[1].Score < 10)
        {
            return AnimeClickEpisodeMatch.None(
                "ambiguous",
                $"top candidates too close ({best.Score} vs {ranked[1].Score})");
        }

        return AnimeClickEpisodeMatch.Found(
            best.Episode,
            best.Strategy,
            Math.Min(1, best.Score / 125d),
            best.Reason);
    }

    /// <summary>
    /// Last resort for a file numbered inside the season that the card files among its specials.
    /// <para>
    /// K-On!!'s table ends its regular run at 24 and then lists "Ep. 25 (extra)" and "Ep. 26
    /// (extra)"; the library, quite reasonably, stores those two as S02E25 and S02E26 and every
    /// regular strategy comes up empty. The number written in the label is the evidence, so this is
    /// an exact numeric agreement rather than a positional guess — but it only applies past the end
    /// of the regular run, because inside it a special sharing a number is a companion to that
    /// episode (a recap of episode 5), not the episode itself.
    /// </para>
    /// </summary>
    private static AnimeClickEpisodeMatch MatchNumberedExtra(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        AnimeClickEpisodeMatchContext context)
    {
        var requested = context.JellyfinEpisodeNumber;
        if (requested <= 0)
        {
            return AnimeClickEpisodeMatch.None("none", "no numbered extra for a special request");
        }

        var regularHigh = episodes
            .Where(episode => !episode.IsSpecial && episode.RawEpisodeNumber is > 0)
            .Select(episode => episode.RawEpisodeNumber!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (requested <= regularHigh)
        {
            return AnimeClickEpisodeMatch.None("none", "inside the regular run");
        }

        var candidates = episodes
            .Where(episode => episode.IsSpecial
                && !episode.NumberEnd.HasValue
                && !episode.NumberIsAmbiguous
                && episode.RawEpisodeNumber == requested

                // Season zero is how a special declares it has no season, so it disqualifies
                // nothing; a row that names a different season does.
                && (episode.RawSeasonNumber is null or 0
                    || episode.RawSeasonNumber == context.JellyfinSeasonNumber)
                && !(context.LibraryRuntimeMinutes.HasValue
                    && IsRuntimeIncompatible(episode.DurationMinutes, context.LibraryRuntimeMinutes.Value)))
            .ToList();
        return candidates.Count == 1
            ? AnimeClickEpisodeMatch.Found(
                candidates[0],
                "numberedExtra",
                0.8,
                "special row carries the requested number past the end of the regular run")
            : AnimeClickEpisodeMatch.None(
                candidates.Count > 1 ? "ambiguousNumberedExtra" : "none",
                candidates.Count > 1 ? "several extras share the number" : "no numbered extra");
    }

    /// <summary>
    /// Two lengths are incompatible when one is at least twice the other and they differ by more
    /// than five minutes. The factor catches recuts and split specials; the absolute floor keeps
    /// rounding on very short rows — a 2' row against a 4' file — from meaning anything.
    /// </summary>
    private static bool IsRuntimeIncompatible(int? rowMinutes, double fileMinutes)
    {
        if (rowMinutes is not > 0 || fileMinutes <= 0)
        {
            return false;
        }

        var shorter = Math.Min(rowMinutes.Value, fileMinutes);
        var longer = Math.Max(rowMinutes.Value, fileMinutes);
        return longer >= shorter * 2 && longer - shorter > 5;
    }

    private static bool CanUseSeasonOrdinal(
        IReadOnlyCollection<AnimeClickEpisode> allEpisodes,
        IReadOnlyCollection<AnimeClickEpisode> seasonGroup,
        int requestedSeason)
    {
        if (seasonGroup.Count == 0)
        {
            return false;
        }

        if (seasonGroup.All(episode => episode.SeasonNumberIsSynthetic))
        {
            return true;
        }

        var numbers = seasonGroup
            .Where(episode => episode.RawEpisodeNumber is > 0 && !episode.NumberIsAmbiguous)
            .Select(episode => episode.RawEpisodeNumber!.Value)
            .OrderBy(number => number)
            .ToList();
        if (numbers.Count != seasonGroup.Count)
        {
            return false;
        }

        var contiguous = numbers.Zip(numbers.Skip(1), (left, right) => right == left + 1).All(value => value);
        if (!contiguous)
        {
            return false;
        }

        // Local numbering starts at 1 and remains safe even on a truncated tail. An
        // absolute S2+ group may start later, but only a complete contiguous timeline
        // from episode 1 proves that its first row is really the next season ordinal.
        if (numbers[0] == 1)
        {
            return true;
        }

        if (requestedSeason <= 1
            || allEpisodes.Any(episode => !episode.IsSpecial && !episode.RawSeasonNumber.HasValue))
        {
            return false;
        }

        var throughRequestedSeason = allEpisodes
            .Where(episode =>
                !episode.IsSpecial
                && episode.RawSeasonNumber is > 0
                && episode.RawSeasonNumber <= requestedSeason)
            .ToList();
        if (throughRequestedSeason.Count == 0
            || throughRequestedSeason.Any(episode =>
                episode.RawEpisodeNumber is not > 0
                || episode.HasNonStandardNumber
                || episode.NumberIsAmbiguous))
        {
            return false;
        }

        var absoluteNumbers = throughRequestedSeason
            .Select(episode => episode.RawEpisodeNumber!.Value)
            .OrderBy(number => number)
            .ToList();
        return absoluteNumbers[0] == 1
            && absoluteNumbers
                .Zip(absoluteNumbers.Skip(1), (left, right) => right == left + 1)
                .All(value => value);
    }

    private static AnimeClickEpisodeMatch MatchExplicitOnly(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        AnimeClickEpisodeMatchContext context)
    {
        if (!context.JellyfinSeasonNumber.HasValue)
        {
            return AnimeClickEpisodeMatch.None("explicitNoSeason", "explicit override requires a Jellyfin season");
        }

        var candidates = episodes.Where(episode =>
                !episode.IsSpecial
                && episode.RawSeasonNumber == context.JellyfinSeasonNumber.Value
                && (episode.SeasonOrdinalNumber == context.JellyfinEpisodeNumber
                    || (!episode.HasNonStandardNumber
                        && !episode.NumberIsAmbiguous
                        && episode.RawEpisodeNumber == context.JellyfinEpisodeNumber)))
            .Distinct()
            .ToList();
        return candidates.Count == 1
            ? AnimeClickEpisodeMatch.Found(candidates[0], "overrideExplicit", 1, "manual explicit layout override")
            : AnimeClickEpisodeMatch.None("overrideExplicitNoMatch", "explicit override did not produce one candidate");
    }

    private static AnimeClickEpisodeMatch MatchSpecial(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        AnimeClickEpisodeMatchContext context)
    {
        var candidates = episodes
            .Where(episode => episode.IsSpecial
                && !episode.IsForeignWork
                && !episode.NumberEnd.HasValue)

            // The season-zero bucket is cross-season by nature: Jellyfin numbers every special of a
            // show in one flat sequence, so a request for S00Ex must be free to reach a special the
            // card attributes to season one. A request for a real season's episode zero is the
            // opposite case and there the season has to agree, otherwise S02E00 takes the season-one
            // prologue — a real title written with confidence on the wrong episode.
            //
            // On an AnimeClick row season zero means "not attributed to any numbered season", not
            // "the zeroth season", so it stays compatible with everything just like an absent value.
            .Where(episode => context.JellyfinSeasonNumber is null or 0
                || episode.RawSeasonNumber is null or 0
                || episode.RawSeasonNumber == context.JellyfinSeasonNumber)
            .Where(episode =>
                (!episode.HasNonStandardNumber
                    && !episode.NumberIsAmbiguous
                    && episode.RawEpisodeNumber == context.JellyfinEpisodeNumber)
                || (!episode.NumberIsAmbiguous
                    && context.JellyfinEpisodeNumber > 0
                    && episode.SpecialOrdinalNumber == context.JellyfinEpisodeNumber))
            .Distinct()
            .ToList();

        if (candidates.Count == 1)
        {
            return AnimeClickEpisodeMatch.Found(candidates[0], "specialOrdinal", 0.9, "special/extra ordinal");
        }

        var titleMatch = FindUniqueTitleMatch(candidates, context.JellyfinTitle);
        return titleMatch is not null
            ? AnimeClickEpisodeMatch.Found(titleMatch, "specialTitle", 0.95, "special title disambiguation")
            : AnimeClickEpisodeMatch.None(
                candidates.Count > 1 ? "ambiguousSpecial" : "none",
                candidates.Count > 1 ? "multiple special rows share the coordinate" : "no special candidate");
    }

    private static AnimeClickEpisodeMatch MatchUniqueGlobal(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        int globalOrdinal,
        string strategy,
        string reason)
    {
        var matches = episodes.Where(episode =>
                !episode.IsSpecial
                && !episode.NumberIsAmbiguous
                && episode.GlobalOrdinal == globalOrdinal)
            .ToList();
        return matches.Count == 1
            ? AnimeClickEpisodeMatch.Found(matches[0], strategy, 1, reason)
            : AnimeClickEpisodeMatch.None("ambiguousGlobal", "global ordinal is not unique");
    }

    private static void AddGlobalCandidate(
        IDictionary<AnimeClickEpisode, MatchCandidate> candidates,
        IEnumerable<AnimeClickEpisode> episodes,
        int globalOrdinal,
        int score,
        string strategy,
        string reason)
    {
        foreach (var episode in episodes.Where(episode =>
                     !episode.IsSpecial
                     && !episode.NumberIsAmbiguous
                     && episode.GlobalOrdinal == globalOrdinal))
        {
            AddCandidate(candidates, episode, score, strategy, reason);
        }
    }

    private static void AddCandidate(
        IDictionary<AnimeClickEpisode, MatchCandidate> candidates,
        AnimeClickEpisode episode,
        int score,
        string strategy,
        string reason)
    {
        if (!candidates.TryGetValue(episode, out var current) || score > current.Score)
        {
            candidates[episode] = new MatchCandidate(episode, score, strategy, reason);
        }
    }

    private static void ApplyTitleEvidence(
        IDictionary<AnimeClickEpisode, MatchCandidate> candidates,
        string? jellyfinTitle)
    {
        var normalizedTarget = NormalizeTitle(jellyfinTitle);
        if (string.IsNullOrWhiteSpace(normalizedTarget) || IsGenericTitle(normalizedTarget))
        {
            return;
        }

        foreach (var episode in candidates.Keys.ToList())
        {
            var normalizedCandidate = NormalizeTitle(episode.Title);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                continue;
            }

            var current = candidates[episode];
            if (string.Equals(normalizedTarget, normalizedCandidate, StringComparison.Ordinal))
            {
                candidates[episode] = current with
                {
                    Score = current.Score + 35,
                    Reason = current.Reason + "; exact title"
                };
                continue;
            }

            var targetTokens = normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var candidateTokens = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var union = targetTokens.Union(candidateTokens).Count();
            var overlap = union == 0 ? 0 : targetTokens.Intersect(candidateTokens).Count() / (double)union;
            if (overlap >= 0.65)
            {
                candidates[episode] = current with
                {
                    Score = current.Score + 20,
                    Reason = current.Reason + "; similar title"
                };
            }
        }
    }

    private static AnimeClickEpisode? FindUniqueTitleMatch(
        IReadOnlyCollection<AnimeClickEpisode> episodes,
        string? jellyfinTitle)
    {
        var target = NormalizeTitle(jellyfinTitle);
        if (string.IsNullOrWhiteSpace(target) || IsGenericTitle(target))
        {
            return null;
        }

        var matches = episodes.Where(episode => string.Equals(
                NormalizeTitle(episode.Title),
                target,
                StringComparison.Ordinal))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool PersistedAnchorCompatible(
        AnimeClickEpisode episode,
        AnimeClickEpisodeMatchContext context)
    {
        if (context.IsSeasonSpecificPage)
        {
            return true;
        }

        var requestedSeason = context.JellyfinSeasonNumber;
        if (!requestedSeason.HasValue)
        {
            return true;
        }

        if (requestedSeason == 0)
        {
            return episode.IsSpecial;
        }

        if (episode.IsSpecial)
        {
            if (episode.RawSeasonNumber is > 0)
            {
                return episode.RawSeasonNumber == requestedSeason;
            }

            // Decimal/suffixed rows are represented as specials by the parser even when they are
            // episodes of the flat first season. Their durable detail ID remains valid there, but
            // an unseasoned row still cannot prove S2+.
            return requestedSeason <= 1;
        }

        // A season printed by AnimeClick is direct evidence. Synthetic equal-split seasons are
        // intentionally excluded: they are the inference whose staleness we are guarding against.
        if (episode.RawSeasonNumber is > 0)
        {
            return episode.RawSeasonNumber == requestedSeason;
        }

        if (!episode.SeasonNumberIsSynthetic && episode.SeasonNumber is > 0)
        {
            return episode.SeasonNumber == requestedSeason;
        }

        // An unseasoned row is the normal flat/S1 shape.
        if (requestedSeason <= 1)
        {
            return true;
        }

        if (context.LayoutOverride?.TryGetGlobalOrdinal(
                requestedSeason.Value,
                context.JellyfinEpisodeNumber,
                out var overrideOrdinal) == true)
        {
            return episode.GlobalOrdinal == overrideOrdinal;
        }

        return context.LibraryLayout?.TryGetGlobalOrdinal(
                   requestedSeason.Value,
                   context.JellyfinEpisodeNumber,
                   out var libraryOrdinal,
                   out var reliable) == true
               && reliable
               && episode.GlobalOrdinal == libraryOrdinal;
    }

    private static bool SeasonCompatible(AnimeClickEpisode episode, int? jellyfinSeasonNumber)
        => !jellyfinSeasonNumber.HasValue
            || episode.RawSeasonNumber == jellyfinSeasonNumber
            || episode.SeasonNumber == jellyfinSeasonNumber
            || !episode.RawSeasonNumber.HasValue;

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsGenericTitle(string normalizedTitle)
    {
        var tokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length <= 2
            && tokens.Length > 0
            && (tokens[0] is "episodio" or "episode" or "ep")
            && tokens.Skip(1).All(token => token.All(char.IsDigit));
    }

    private sealed record MatchCandidate(
        AnimeClickEpisode Episode,
        int Score,
        string Strategy,
        string Reason);
}

public sealed class AnimeClickEpisodeMatchContext
{
    public AnimeClickEpisodeMatchContext(int? jellyfinSeasonNumber, int jellyfinEpisodeNumber)
    {
        JellyfinSeasonNumber = jellyfinSeasonNumber;
        JellyfinEpisodeNumber = jellyfinEpisodeNumber;
    }

    public int? JellyfinSeasonNumber { get; }

    public int JellyfinEpisodeNumber { get; }

    public int? JellyfinEpisodeNumberEnd { get; init; }

    public string? ExistingProviderId { get; init; }

    public string? JellyfinTitle { get; init; }

    public AnimeClickEpisodeLibraryLayout? LibraryLayout { get; init; }

    public AnimeClickEpisodeLayoutOverride? LayoutOverride { get; init; }

    public int? DeclaredSeasonsCount { get; init; }

    /// <summary>
    /// Episode count AnimeClick declares for the page the rows came from, when known. Compared
    /// against the rows actually parsed to detect a table that carries more than it counts.
    /// </summary>
    public int? DeclaredEpisodeCount { get; init; }

    /// <summary>
    /// Runtime in minutes Jellyfin already knows for the file being matched, when it has one.
    /// A row whose declared length is incompatible with it cannot be that file, however well
    /// the numbers line up.
    /// </summary>
    public double? LibraryRuntimeMinutes { get; init; }

    public bool IsSeasonSpecificPage { get; init; }
}

public sealed class AnimeClickEpisodeMatch
{
    private AnimeClickEpisodeMatch(
        AnimeClickEpisode? episode,
        string strategy,
        double confidence,
        string reason)
    {
        Episode = episode;
        Strategy = strategy;
        Confidence = confidence;
        Reason = reason;
    }

    public AnimeClickEpisode? Episode { get; }

    public string Strategy { get; }

    public double Confidence { get; }

    public string Reason { get; }

    public bool Success => Episode is not null;

    public static AnimeClickEpisodeMatch Found(
        AnimeClickEpisode episode,
        string strategy,
        double confidence = 1,
        string reason = "")
        => new(episode, strategy, confidence, reason);

    public static AnimeClickEpisodeMatch None(string strategy, string reason = "")
        => new(null, strategy, 0, reason);
}
