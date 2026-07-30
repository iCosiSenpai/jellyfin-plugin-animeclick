using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;

namespace AnimeClick.Harness;

/// <summary>
/// Compares, series by series, the episode titles stored in Jellyfin with what the plugin's
/// matcher resolves today from AnimeClick.
/// <para>
/// This mode removes the two blind spots of the offline report: the library topology and the
/// file's own title are the real ones, taken from the server, so the matcher sees exactly the
/// evidence it sees in production.
/// </para>
/// <para>
/// A difference is not automatically a defect. This library has AniList, Kitsu, AniSearch, TMDb
/// and others enabled, and a title stored by a higher-priority provider is a legitimate outcome.
/// What matters is the shape of the difference: an episode where AnimeClick resolves nothing, or
/// resolves to a row whose position contradicts the stored one, is worth a look; a different but
/// plausible Italian title usually just means another provider won.
/// </para>
/// </summary>
internal static class LibraryDiff
{
    internal sealed class SeriesOutcome
    {
        public required string SeriesName { get; init; }

        public required string AnimeClickId { get; init; }

        public int EpisodesInLibrary { get; set; }

        public int Resolved { get; set; }

        public int Unresolved { get; set; }

        public int TitleEqual { get; set; }

        public int TitleDifferent { get; set; }

        /// <summary>Library has a placeholder, AnimeClick has a real title: an improvement.</summary>
        public int WouldFillPlaceholder { get; set; }

        /// <summary>Library has a real title, AnimeClick has a placeholder: a downgrade risk.</summary>
        public int WouldOverwriteWithPlaceholder { get; set; }

        /// <summary>Both sides are placeholders, differing only in shape. Noise.</summary>
        public int BothPlaceholder { get; set; }

        public int WeakConfidence { get; set; }

        /// <summary>Seasons whose proposed titles look like the stored ones shifted by a fixed step.</summary>
        public List<string> Shifts { get; } = [];

        public List<string> Samples { get; } = [];
    }

    /// <summary>
    /// Rebuilds what <c>AnimeClickEpisodeLayoutResolver</c> derives from the library: per season,
    /// the highest episode number, how many are known, whether it starts at 1 and whether the
    /// run is contiguous. Computed from the server's own episode list rather than guessed.
    /// </summary>
    internal static AnimeClickEpisodeLibraryLayout BuildLayout(
        Guid seriesId,
        List<JellyfinEpisode> episodes)
    {
        var seasons = new Dictionary<int, AnimeClickEpisodeSeasonLayout>();
        foreach (var group in episodes
                     .Where(e => e.SeasonNumber is > 0 && e.IndexNumber is > 0)
                     .GroupBy(e => e.SeasonNumber!.Value))
        {
            var numbers = group
                .Select(e => e.IndexNumber!.Value)
                .Distinct()
                .OrderBy(number => number)
                .ToList();
            var contiguous = numbers
                .Zip(numbers.Skip(1), (left, right) => right == left + 1)
                .All(step => step);

            seasons[group.Key] = new AnimeClickEpisodeSeasonLayout(
                SeasonNumber: group.Key,
                MaximumEpisodeNumber: numbers[^1],
                KnownEpisodeCount: numbers.Count,
                StartsAtOne: numbers[0] == 1,
                IsContiguous: contiguous);
        }

        return new AnimeClickEpisodeLibraryLayout(seriesId, seasons);
    }

    internal sealed record SeasonSource(
        List<AnimeClickEpisode> Rows,
        int DeclaredSeasonsCount,
        int? DeclaredEpisodeCount,
        bool IsSeasonSpecificPage,
        string AnimeClickId);

    internal static SeriesOutcome Compare(
        JellyfinSeries series,
        List<JellyfinEpisode> libraryEpisodes,
        Func<int?, SeasonSource?> sourceForSeason,
        bool ignoreDeclaredCount = false)
    {
        var outcome = new SeriesOutcome
        {
            SeriesName = series.Name,
            AnimeClickId = series.AnimeClickId
        };

        var layout = BuildLayout(Guid.Parse(series.Id), libraryEpisodes);

        // Collected to look for a systematic offset afterwards: a shift is invisible episode by
        // episode, because every single title looks like a plausible title. It only shows up in
        // the sequence — the proposed name for E01 is the stored name of E02, and so on down the
        // season. That is how "Arrivare a te" hid, and it was found by chance rather than looked
        // for. Position: season -> (episode number, stored title, proposed title).
        var sequences = new Dictionary<int, List<(int Episode, string? Stored, string? Proposed)>>();

        foreach (var episode in libraryEpisodes
                     .Where(e => e.IndexNumber is > 0)
                     .OrderBy(e => e.SeasonNumber ?? 0)
                     .ThenBy(e => e.IndexNumber))
        {
            outcome.EpisodesInLibrary++;

            var source = sourceForSeason(episode.SeasonNumber);
            if (source is null)
            {
                outcome.Unresolved++;
                if (outcome.Samples.Count < 8)
                {
                    outcome.Samples.Add(
                        $"S{episode.SeasonNumber ?? 0:00}E{episode.IndexNumber:00} "
                        + $"\"{Short(episode.Name)}\" → nessuna pagina AnimeClick per questa stagione");
                }

                continue;
            }

            // When the season resolved to its own AnimeClick entry, the plugin asks that page for
            // its own first, second, … episode: exactly what AnimeClickEpisodeProvider does with
            // IsSeasonSpecificPage. Asking it for "season 3 episode 1" would find nothing.
            var context = source.IsSeasonSpecificPage
                ? new AnimeClickEpisodeMatchContext(1, episode.IndexNumber!.Value)
                {
                    JellyfinEpisodeNumberEnd = episode.IndexNumberEnd,
                    JellyfinTitle = episode.Name,
                    ExistingProviderId = episode.AnimeClickProviderId,
                    IsSeasonSpecificPage = true,
                    DeclaredSeasonsCount = source.DeclaredSeasonsCount > 0 ? source.DeclaredSeasonsCount : null,
                    DeclaredEpisodeCount = ignoreDeclaredCount ? null : source.DeclaredEpisodeCount
                }
                : new AnimeClickEpisodeMatchContext(episode.SeasonNumber, episode.IndexNumber!.Value)
                {
                    JellyfinEpisodeNumberEnd = episode.IndexNumberEnd,
                    JellyfinTitle = episode.Name,
                    ExistingProviderId = episode.AnimeClickProviderId,
                    LibraryLayout = layout,
                    DeclaredSeasonsCount = source.DeclaredSeasonsCount > 0 ? source.DeclaredSeasonsCount : null,
                    DeclaredEpisodeCount = ignoreDeclaredCount ? null : source.DeclaredEpisodeCount
                };

            var match = AnimeClickEpisodeMatcher.Match(source.Rows, context);
            var coordinate = $"S{episode.SeasonNumber ?? 0:00}E{episode.IndexNumber:00}";

            if (!match.Success)
            {
                outcome.Unresolved++;
                if (outcome.Samples.Count < 8)
                {
                    outcome.Samples.Add(
                        $"{coordinate} \"{Short(episode.Name)}\" → niente da AnimeClick "
                        + $"[{match.Strategy}: {match.Reason}]");
                }

                continue;
            }

            outcome.Resolved++;
            if (match.Confidence < 0.8)
            {
                outcome.WeakConfidence++;
            }

            var proposed = match.Episode!.Title;

            var seasonKey = episode.SeasonNumber ?? 0;
            if (!sequences.TryGetValue(seasonKey, out var sequence))
            {
                sequence = [];
                sequences[seasonKey] = sequence;
            }

            sequence.Add((episode.IndexNumber!.Value, episode.Name, proposed));

            if (TitlesMatch(episode.Name, proposed))
            {
                outcome.TitleEqual++;
                continue;
            }

            outcome.TitleDifferent++;

            var storedPlaceholder = IsPlaceholderTitle(episode.Name);
            var proposedPlaceholder = IsPlaceholderTitle(proposed);
            if (storedPlaceholder && proposedPlaceholder)
            {
                // "Episodio 1" vs "Episodio 01": nothing to see.
                outcome.BothPlaceholder++;
                continue;
            }

            if (storedPlaceholder)
            {
                outcome.WouldFillPlaceholder++;
                if (outcome.Samples.Count < 8)
                {
                    outcome.Samples.Add(
                        $"{coordinate} DA RIEMPIRE: in libreria segnaposto \"{Short(episode.Name)}\", "
                        + $"AnimeClick ha \"{Short(proposed)}\" [{match.Strategy}, conf. {match.Confidence:0.00}]");
                }

                continue;
            }

            if (proposedPlaceholder)
            {
                outcome.WouldOverwriteWithPlaceholder++;
                if (outcome.Samples.Count < 8)
                {
                    outcome.Samples.Add(
                        $"{coordinate} RISCHIO: in libreria \"{Short(episode.Name)}\", "
                        + $"AnimeClick offre il segnaposto \"{Short(proposed)}\"");
                }

                continue;
            }

            if (outcome.Samples.Count < 8)
            {
                outcome.Samples.Add(
                    $"{coordinate} in libreria \"{Short(episode.Name)}\" ≠ AnimeClick \"{Short(proposed)}\" "
                    + $"[{match.Strategy}, conf. {match.Confidence:0.00}]");
            }
        }

        foreach (var (season, sequence) in sequences.OrderBy(pair => pair.Key))
        {
            var shift = DetectShift(sequence);
            if (shift is not null)
            {
                outcome.Shifts.Add($"S{season:00}: {shift}");
            }
        }

        return outcome;
    }

    /// <summary>
    /// Looks for a constant offset between the proposed titles and the stored ones. Returns a
    /// description when one explains the season clearly better than no offset at all.
    /// <para>
    /// This is the mechanical signature of the defect that mis-titles a whole season: each episode
    /// receives its neighbour's name, so every title is individually plausible and only the
    /// sequence gives it away. Checking it by eye across a library is not realistic, which is
    /// exactly why it went unnoticed.
    /// </para>
    /// </summary>
    private static string? DetectShift(List<(int Episode, string? Stored, string? Proposed)> sequence)
    {
        // Grouped rather than indexed directly: a library can hold two episodes with the same
        // number in one season (duplicate files, split parts), and that must not throw here.
        var stored = sequence
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Stored))
            .GroupBy(entry => entry.Episode)
            .ToDictionary(group => group.Key, group => Normalize(group.First().Stored!));
        if (stored.Count < 4)
        {
            return null;
        }

        int Agreement(int offset) => sequence.Count(entry =>
            !string.IsNullOrWhiteSpace(entry.Proposed)
            && stored.TryGetValue(entry.Episode + offset, out var storedTitle)
            && storedTitle == Normalize(entry.Proposed!));

        var aligned = Agreement(0);
        var best = 0;
        var bestAgreement = aligned;
        foreach (var offset in new[] { -3, -2, -1, 1, 2, 3 })
        {
            var agreement = Agreement(offset);
            if (agreement > bestAgreement)
            {
                bestAgreement = agreement;
                best = offset;
            }
        }

        // Require the offset to explain most of the season and to beat the aligned reading
        // clearly, so a couple of coincidentally repeated titles cannot raise a false alarm.
        if (best == 0 || bestAgreement < 3 || bestAgreement < aligned + 3)
        {
            return null;
        }

        var direction = best > 0
            ? $"AnimeClick è avanti di {best}"
            : $"AnimeClick è indietro di {-best}";
        return $"{direction} — {bestAgreement} episodi combaciano con lo scarto, "
               + $"{aligned} senza. Esempio: E{sequence[0].Episode:00} in libreria "
               + $"\"{Short(sequence[0].Stored)}\", AnimeClick propone \"{Short(sequence[0].Proposed)}\"";
    }

    /// <summary>
    /// A title that carries no information beyond the number: "Episodio 1", "Episode 12", "Ep. 3".
    /// Mirrors the shape the plugin already refuses to accept as an episode overview
    /// (<c>EpisodeOverviewPlaceholderRegex</c>), applied here to titles.
    /// </summary>
    private static bool IsPlaceholderTitle(string? title)
        => !string.IsNullOrWhiteSpace(title) && PlaceholderTitleRegex.IsMatch(title.Trim());

    private static readonly System.Text.RegularExpressions.Regex PlaceholderTitleRegex =
        new(
            @"^(?:Episodio|Episode|Ep\.?|Puntata)\s*#?\s*\d+(?:[\.,]\d+)?(?:\s*[-–/]\s*\d+)?[\.!]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static bool TitlesMatch(string? stored, string? proposed)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(proposed))
        {
            return false;
        }

        return string.Equals(Normalize(stored), Normalize(proposed), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapses the differences that are not differences: spacing, quote style, case.</summary>
    private static string Normalize(string value)
        => new string(value
                .Replace('\u2019', '\'')
                .Replace('\u2018', '\'')
                .Replace('\u201C', '"')
                .Replace('\u201D', '"')
                .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))
                .ToArray())
            .ToLowerInvariant();

    private static string Short(string? value)
        => value is null ? "(nessun titolo)" : value.Length <= 42 ? value : value[..41] + "…";
}
