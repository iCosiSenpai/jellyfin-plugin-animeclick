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
        bool IsSeasonSpecificPage,
        string AnimeClickId);

    internal static SeriesOutcome Compare(
        JellyfinSeries series,
        List<JellyfinEpisode> libraryEpisodes,
        Func<int?, SeasonSource?> sourceForSeason)
    {
        var outcome = new SeriesOutcome
        {
            SeriesName = series.Name,
            AnimeClickId = series.AnimeClickId
        };

        var layout = BuildLayout(Guid.Parse(series.Id), libraryEpisodes);

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
                    DeclaredSeasonsCount = source.DeclaredSeasonsCount > 0 ? source.DeclaredSeasonsCount : null
                }
                : new AnimeClickEpisodeMatchContext(episode.SeasonNumber, episode.IndexNumber!.Value)
                {
                    JellyfinEpisodeNumberEnd = episode.IndexNumberEnd,
                    JellyfinTitle = episode.Name,
                    ExistingProviderId = episode.AnimeClickProviderId,
                    LibraryLayout = layout,
                    DeclaredSeasonsCount = source.DeclaredSeasonsCount > 0 ? source.DeclaredSeasonsCount : null
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

        return outcome;
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
