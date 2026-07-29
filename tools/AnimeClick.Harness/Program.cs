using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;

namespace AnimeClick.Harness;

/// <summary>
/// Offline audit of what the plugin would write into a Jellyfin library.
/// <para>
/// Fetches the real AnimeClick pages and runs the production parser and matcher over them —
/// <see cref="AnimeClickHtmlParser"/> and <see cref="AnimeClickEpisodeMatcher"/>, not a copy —
/// then reports what would land on each episode and what looks wrong. It exists because the
/// failure modes that matter here are silent: a changed selector yields no exception, just an
/// empty list or a plausible-looking wrong title, and spotting that by browsing a library of
/// hundreds of episodes by hand is not realistic.
/// </para>
/// <para>Jellyfin is not required: the parser and matcher are pure functions over HTML.</para>
/// </summary>
internal static class Program
{
    private const string BaseUrl = "https://www.animeclick.it";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var ids = new List<string>();
        string? search = null;
        var overviewSamples = 3;
        var dumpRows = false;
        var refresh = false;
        var delaySeconds = 1.5;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--id" when i + 1 < args.Length:
                    ids.Add(args[++i]);
                    break;
                case "--file" when i + 1 < args.Length:
                    ids.AddRange(
                        (await File.ReadAllLinesAsync(args[++i]).ConfigureAwait(false))
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0 && !line.StartsWith('#')));
                    break;
                case "--search" when i + 1 < args.Length:
                    search = args[++i];
                    break;
                case "--overview-samples" when i + 1 < args.Length:
                    overviewSamples = int.Parse(args[++i]);
                    break;
                case "--delay" when i + 1 < args.Length:
                    delaySeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--dump-rows":
                    dumpRows = true;
                    break;
                case "--refresh":
                    refresh = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    Console.Error.WriteLine($"argomento non riconosciuto: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "animeclick-harness");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var fetcher = new Fetcher(cacheDirectory, TimeSpan.FromSeconds(delaySeconds), refresh);
        var parser = new AnimeClickHtmlParser();

        if (search is not null)
        {
            await SearchAsync(fetcher, parser, search, cts.Token).ConfigureAwait(false);
            return 0;
        }

        if (ids.Count == 0)
        {
            Console.Error.WriteLine("nessun id: usa --id, --file oppure --search");
            return 2;
        }

        var reports = new List<AnimeReport>();
        foreach (var id in ids)
        {
            if (cts.IsCancellationRequested)
            {
                break;
            }

            var report = await AuditAsync(fetcher, parser, id, overviewSamples, dumpRows, verbose, cts.Token)
                .ConfigureAwait(false);
            reports.Add(report);
            Print(report, verbose);
        }

        PrintSummary(reports, fetcher, cacheDirectory);
        return reports.Any(r => r.HasErrors) ? 1 : 0;
    }

    private static async Task<AnimeReport> AuditAsync(
        Fetcher fetcher,
        AnimeClickHtmlParser parser,
        string animeClickId,
        int overviewSamples,
        bool dumpRows,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var report = new AnimeReport { AnimeClickId = animeClickId };
        var animeUrl = $"{BaseUrl}/anime/{animeClickId.Trim('/')}";

        var detailHtml = await fetcher.GetAsync(animeUrl, cancellationToken).ConfigureAwait(false);
        if (detailHtml is null)
        {
            report.Add(Severity.Error, "pagina-assente", $"la pagina anime non è raggiungibile: {animeUrl}");
            return report;
        }

        var anime = parser.ParseAnimePage(animeUrl, detailHtml);
        report.Title = anime.Title;
        report.Year = anime.ProductionYear;
        report.DeclaredEpisodeCount = anime.EpisodeCount;
        report.DeclaredSeasonsCount = anime.SeasonsCount;

        // The parser's title chain ends in `?? anime.Id`, so a changed <h1> does not fail: it
        // writes the provider id into the library as if it were the title.
        if (string.Equals(anime.Title, animeClickId.Trim('/'), StringComparison.OrdinalIgnoreCase)
            || anime.Title.Contains('/', StringComparison.Ordinal))
        {
            report.Add(
                Severity.Error,
                "titolo-degradato",
                $"il titolo è \"{anime.Title}\", cioè l'id: il selettore del titolo non ha trovato nulla");
        }

        if (string.IsNullOrWhiteSpace(anime.Overview))
        {
            report.Add(Severity.Warning, "trama-assente", "nessuna trama estratta dalla pagina anime");
        }

        if (anime.Genres.Count == 0)
        {
            report.Add(Severity.Note, "generi-assenti", "nessun genere estratto");
        }

        var episodes = await LoadEpisodesAsync(fetcher, parser, animeUrl, report, cancellationToken)
            .ConfigureAwait(false);
        if (episodes.Count == 0)
        {
            report.Add(
                Severity.Error,
                "episodi-assenti",
                "nessuna riga episodio estratta: la tabella /episodi non è stata riconosciuta");
            return report;
        }

        AnalyseRows(episodes, report);
        if (dumpRows)
        {
            DumpRows(episodes);
        }

        SimulateMatching(episodes, anime.SeasonsCount, report, verbose);
        await SampleOverviewsAsync(fetcher, parser, episodes, overviewSamples, report, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Mirrors <c>AnimeClickEpisodeListLoader</c>: page 1, then <c>?page=N</c> until a page adds
    /// nothing. The merge predicate is duplicated rather than called because the loader's own
    /// type pulls in Jellyfin and logging assemblies that this tool deliberately does not have.
    /// </summary>
    private static async Task<List<AnimeClickEpisode>> LoadEpisodesAsync(
        Fetcher fetcher,
        AnimeClickHtmlParser parser,
        string animeUrl,
        AnimeReport report,
        CancellationToken cancellationToken)
    {
        var episodesUrl = animeUrl + "/episodi";
        var all = new List<AnimeClickEpisode>();

        var firstHtml = await fetcher.GetAsync(episodesUrl, cancellationToken).ConfigureAwait(false);
        if (firstHtml is null)
        {
            return all;
        }

        report.PagesFetched = 1;
        all.AddRange(parser.ParseEpisodesPage(firstHtml, BaseUrl));

        for (var page = 2; page <= 100; page++)
        {
            var html = await fetcher.GetAsync($"{episodesUrl}?page={page}", cancellationToken)
                .ConfigureAwait(false);
            if (html is null)
            {
                break;
            }

            var next = parser.ParseEpisodesPage(html, BaseUrl);
            if (next.Count == 0 || MergeUnique(all, next) == 0)
            {
                break;
            }

            report.PagesFetched = page;
        }

        report.RowsParsed = all.Count;
        AnimeClickHtmlParser.FinalizeEpisodeList(all, seasonsCount: null);
        return all;
    }

    private static int MergeUnique(List<AnimeClickEpisode> target, List<AnimeClickEpisode> candidates)
    {
        var added = 0;
        foreach (var candidate in candidates.OrderBy(episode => episode.SourceOrder))
        {
            var sameProviderId = !string.IsNullOrWhiteSpace(candidate.ProviderId)
                && target.Any(existing => string.Equals(
                    existing.ProviderId, candidate.ProviderId, StringComparison.OrdinalIgnoreCase));
            var sameRawRow = string.IsNullOrWhiteSpace(candidate.ProviderId)
                && target.Any(existing =>
                    existing.RawSeasonNumber == candidate.RawSeasonNumber
                    && string.Equals(existing.RawNumberLabel, candidate.RawNumberLabel, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Title, candidate.Title, StringComparison.OrdinalIgnoreCase));

            if (sameProviderId || sameRawRow)
            {
                continue;
            }

            candidate.SourceOrder = target.Count + 1;
            target.Add(candidate);
            added++;
        }

        return added;
    }

    private static void AnalyseRows(List<AnimeClickEpisode> episodes, AnimeReport report)
    {
        var regular = episodes.Where(e => !e.IsSpecial).ToList();
        var specials = episodes.Where(e => e.IsSpecial).ToList();
        report.RegularEpisodes = regular.Count;
        report.Specials = specials.Count;

        if (report.DeclaredEpisodeCount is > 0 && report.DeclaredEpisodeCount != regular.Count)
        {
            report.Add(
                Severity.Warning,
                "conteggio-diverso",
                $"AnimeClick dichiara {report.DeclaredEpisodeCount} episodi, la tabella ne espone {regular.Count} regolari");
        }

        // GlobalOrdinal == 0 on a regular row means canonicalisation refused to number the
        // timeline, which happens when any regular coordinate is ambiguous. Everything that
        // depends on absolute numbering then silently stops working.
        var unnumbered = regular.Count(e => e.GlobalOrdinal == 0);
        if (unnumbered > 0)
        {
            report.Add(
                Severity.Error,
                "timeline-non-numerata",
                $"{unnumbered} episodi regolari senza ordinale canonico: coordinate ambigue nella tabella");
        }

        foreach (var group in regular
                     .Where(e => e.NumberIsAmbiguous)
                     .GroupBy(e => (e.RawSeasonNumber, e.RawEpisodeNumber)))
        {
            report.Add(
                Severity.Error,
                "numero-duplicato",
                $"più righe con la stessa coordinata S{group.Key.RawSeasonNumber?.ToString() ?? "-"}"
                + $"E{group.Key.RawEpisodeNumber?.ToString() ?? "-"}: "
                + string.Join(" | ", group.Select(e => $"\"{e.RawNumberLabel}\" → {e.Title}")));
        }

        // Gaps in the printed numbering, per season when explicit.
        foreach (var season in regular.GroupBy(e => e.RawSeasonNumber))
        {
            var numbers = season
                .Where(e => e.RawEpisodeNumber is > 0)
                .Select(e => e.RawEpisodeNumber!.Value)
                .OrderBy(n => n)
                .ToList();
            if (numbers.Count < 2)
            {
                continue;
            }

            var missing = Enumerable.Range(numbers[0], numbers[^1] - numbers[0] + 1)
                .Except(numbers)
                .ToList();
            if (missing.Count > 0 && missing.Count <= 20)
            {
                report.Add(
                    Severity.Warning,
                    "buchi-numerazione",
                    $"stagione {season.Key?.ToString() ?? "senza numero"}: numeri mancanti "
                    + string.Join(", ", missing));
            }
        }

        var yearLike = episodes.Where(e => e.Number is >= 1900 and <= 2099).ToList();
        if (yearLike.Count > 0)
        {
            report.Add(
                Severity.Error,
                "numero-anno",
                "righe il cui numero di episodio sembra un anno: "
                + string.Join(" | ", yearLike.Select(e => $"\"{e.RawNumberLabel}\"")));
        }

        var untitled = episodes.Count(e => string.IsNullOrWhiteSpace(e.Title));
        if (untitled > 0)
        {
            report.Add(Severity.Warning, "titoli-assenti", $"{untitled} righe senza titolo");
        }

        var withoutProviderId = episodes.Count(e => string.IsNullOrWhiteSpace(e.ProviderId));
        if (withoutProviderId > 0)
        {
            report.Add(
                Severity.Note,
                "senza-id-episodio",
                $"{withoutProviderId} righe senza link /episodio: per queste non è possibile la sinossi per episodio");
        }

        // A duration read from the wrong column is the classic symptom of a table that gained
        // or lost one: durations are read positionally from the third cell.
        var oddDurations = episodes
            .Where(e => e.DurationMinutes is > 0 and < 3 or > 200)
            .Select(e => $"\"{e.RawNumberLabel}\" → {e.DurationMinutes}min")
            .ToList();
        if (oddDurations.Count > 0)
        {
            report.Add(
                Severity.Warning,
                "durata-sospetta",
                "durate implausibili, possibile colonna spostata: " + string.Join(" | ", oddDurations));
        }

        if (specials.Count > 0)
        {
            report.Add(
                Severity.Note,
                "speciali",
                string.Join(" | ", specials.Take(12).Select(e => $"\"{e.RawNumberLabel}\" → {e.Title}")));
        }
    }

    /// <summary>
    /// Replays the matcher for the coordinates a Jellyfin library would actually ask for, in the
    /// two shapes that exist in practice: seasons as AnimeClick labels them, and a flat library
    /// numbered 1..N.
    /// <para>
    /// Fidelity notes. The full row list is passed, exactly as
    /// <c>AnimeClickEpisodeProvider</c> passes <c>catalog.Episodes</c> — specials included,
    /// because they participate in some branches. <c>DeclaredSeasonsCount</c> is supplied so the
    /// equal-split branch behaves as in production. Two inputs cannot be reproduced without the
    /// server and are deliberately left empty: the Jellyfin topology (<c>LibraryLayout</c>),
    /// which feeds the matcher's strongest branch, and the file's own title
    /// (<c>JellyfinTitle</c>), which can rescue a weak match. The report is therefore the
    /// pessimistic case: what the plugin resolves on evidence from AnimeClick alone.
    /// </para>
    /// </summary>
    private static void SimulateMatching(
        List<AnimeClickEpisode> episodes,
        int declaredSeasonsCount,
        AnimeReport report,
        bool verbose)
    {
        var regular = episodes.Where(e => !e.IsSpecial).ToList();
        if (regular.Count == 0)
        {
            return;
        }

        var explicitSeasons = regular
            .Where(e => e.RawSeasonNumber is > 0)
            .GroupBy(e => e.RawSeasonNumber!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        // A well-organised library: every season numbered 1..N, contiguous. This is what the
        // layout resolver would hand the matcher on a real server, and it feeds the matcher's
        // highest-scoring branch, so a report without it would over-report failures. Each
        // request shape gets the topology that shape implies — passing the seasoned layout to a
        // flat request would invent failures that no real library would produce.
        var seasonedLayout = BuildLayout(CountsBySeason(regular, explicitSeasons));
        var flatLayout = BuildLayout(new Dictionary<int, int> { [1] = regular.Count });

        var requests = new List<(int? Season, int Episode, string Shape, AnimeClickEpisodeLibraryLayout Layout)>();
        if (explicitSeasons.Count > 0)
        {
            foreach (var season in explicitSeasons)
            {
                for (var index = 1; index <= season.Count(); index++)
                {
                    requests.Add((season.Key, index, "per stagione", seasonedLayout));
                }
            }
        }

        for (var index = 1; index <= regular.Count; index++)
        {
            requests.Add((1, index, "piatta 1..N", flatLayout));
        }

        var failures = new List<string>();
        var weak = new List<string>();
        var topologyDependent = new List<string>();
        var collisions = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (season, episode, shape, layout) in requests)
        {
            var bare = Match(episodes, season, episode, declaredSeasonsCount, null);
            var withTopology = Match(episodes, season, episode, declaredSeasonsCount, layout);
            var coordinate = $"S{season?.ToString() ?? "-"}E{episode:00} ({shape})";

            if (!withTopology.Success)
            {
                failures.Add($"{coordinate} → nessun match [{withTopology.Strategy}: {withTopology.Reason}]");
                continue;
            }

            if (!bare.Success)
            {
                topologyDependent.Add($"{coordinate} → \"{withTopology.Episode!.Title}\"");
            }
            else if (bare.Episode != withTopology.Episode)
            {
                failures.Add(
                    $"{coordinate} → esito diverso con e senza topologia: "
                    + $"\"{bare.Episode!.Title}\" vs \"{withTopology.Episode!.Title}\"");
                continue;
            }

            if (withTopology.Confidence < 0.8)
            {
                weak.Add(
                    $"{coordinate} → \"{withTopology.Episode!.Title}\" conf. {withTopology.Confidence:0.00} "
                    + $"[{withTopology.Strategy}]");
            }

            var key = shape + "::" + (withTopology.Episode!.ProviderId ?? withTopology.Episode.RawNumberLabel);
            collisions.TryAdd(key, []);
            collisions[key].Add(coordinate);

            if (verbose)
            {
                Console.WriteLine(
                    $"    {coordinate} → \"{withTopology.Episode.Title}\" "
                    + $"[{withTopology.Strategy}, conf. {withTopology.Confidence:0.00}]");
            }
        }

        if (topologyDependent.Count > 0)
        {
            report.Add(
                Severity.Warning,
                "dipende-da-topologia",
                $"{topologyDependent.Count} coordinate risolte solo grazie alla topologia della libreria: "
                + "se le stagioni in Jellyfin non sono complete e contigue questi episodi restano senza "
                + "metadati. Prime: " + string.Join(" | ", topologyDependent.Take(6)));
        }

        if (failures.Count > 0)
        {
            report.Add(
                Severity.Warning,
                "match-mancante",
                $"{failures.Count} coordinate senza corrispondenza. Prime: "
                + string.Join(" | ", failures.Take(6)));
        }

        if (weak.Count > 0)
        {
            report.Add(
                Severity.Warning,
                "match-debole",
                $"{weak.Count} coordinate accettate con confidenza sotto 0.80. Prime: "
                + string.Join(" | ", weak.Take(6)));
        }

        var duplicated = collisions.Where(pair => pair.Value.Count > 1).ToList();
        if (duplicated.Count > 0)
        {
            report.Add(
                Severity.Error,
                "match-collisione",
                $"{duplicated.Count} episodi AnimeClick assegnati a più coordinate Jellyfin diverse. Primi: "
                + string.Join(
                    " | ",
                    duplicated.Take(4).Select(pair => $"{pair.Key.Split("::")[1]} ← {string.Join(", ", pair.Value)}")));
        }
    }

    /// <summary>
    /// Fetches a few episode detail pages and checks that the description node is still where
    /// the parser expects it. This is the one drift signal the plugin itself only logs at Debug,
    /// so in a running server it is effectively invisible.
    /// </summary>
    private static AnimeClickEpisodeMatch Match(
        List<AnimeClickEpisode> episodes,
        int? season,
        int episode,
        int declaredSeasonsCount,
        AnimeClickEpisodeLibraryLayout? layout)
        => AnimeClickEpisodeMatcher.Match(
            episodes,
            new AnimeClickEpisodeMatchContext(season, episode)
            {
                DeclaredSeasonsCount = declaredSeasonsCount > 0 ? declaredSeasonsCount : null,
                LibraryLayout = layout
            });

    /// <summary>
    /// Episode counts per season as AnimeClick labels them. A leading block of unlabelled rows is
    /// taken as season 1, which is how AnimeClick actually renders several multi-season shows
    /// (season 1 with no prefix, later seasons with <c>S2 Ep. NN</c>).
    /// </summary>
    private static Dictionary<int, int> CountsBySeason(
        List<AnimeClickEpisode> regular,
        List<IGrouping<int, AnimeClickEpisode>> explicitSeasons)
    {
        var counts = new Dictionary<int, int>();
        if (explicitSeasons.Count == 0)
        {
            counts[1] = regular.Count;
            return counts;
        }

        foreach (var season in explicitSeasons)
        {
            counts[season.Key] = season.Count();
        }

        var unlabelled = regular.Count(e => !e.RawSeasonNumber.HasValue);
        if (unlabelled > 0 && !counts.ContainsKey(1))
        {
            counts[1] = unlabelled;
        }

        return counts;
    }

    private static AnimeClickEpisodeLibraryLayout BuildLayout(Dictionary<int, int> countsBySeason)
        => new(
            Guid.NewGuid(),
            countsBySeason.ToDictionary(
                pair => pair.Key,
                pair => new AnimeClickEpisodeSeasonLayout(
                    SeasonNumber: pair.Key,
                    MaximumEpisodeNumber: pair.Value,
                    KnownEpisodeCount: pair.Value,
                    StartsAtOne: true,
                    IsContiguous: true)));

    private static void DumpRows(List<AnimeClickEpisode> episodes)
    {
        Console.WriteLine();
        Console.WriteLine("   righe come le vede il parser:");
        Console.WriteLine("     ord  etichetta            rawS rawE  glob seas spec  titolo");
        foreach (var e in episodes.OrderBy(e => e.SourceOrder))
        {
            Console.WriteLine(
                $"     {e.SourceOrder,3}  {Truncate(e.RawNumberLabel, 20),-20} "
                + $"{e.RawSeasonNumber?.ToString() ?? "-",4} {e.RawEpisodeNumber?.ToString() ?? "-",4}  "
                + $"{e.GlobalOrdinal,4} {e.SeasonOrdinalNumber,4} {(e.IsSpecial ? "sp" : "  "),4}  "
                + Truncate(e.Title ?? "(nessun titolo)", 48));
        }
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static async Task SampleOverviewsAsync(
        Fetcher fetcher,
        AnimeClickHtmlParser parser,
        List<AnimeClickEpisode> episodes,
        int samples,
        AnimeReport report,
        CancellationToken cancellationToken)
    {
        if (samples <= 0)
        {
            return;
        }

        var candidates = episodes
            .Where(e => !string.IsNullOrWhiteSpace(e.ProviderId))
            .Take(samples)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var nodeMissing = new List<string>();
        var empty = new List<string>();
        var found = 0;

        foreach (var episode in candidates)
        {
            var url = $"{BaseUrl}/episodio/{episode.ProviderId}";
            var html = await fetcher.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                nodeMissing.Add($"{episode.RawNumberLabel} (pagina non raggiungibile)");
                continue;
            }

            // TryParseEpisodeOverviewPage, not ParseEpisodeOverviewPage: the public one returns
            // null both when the node is missing (the site changed) and when it is present but
            // empty (this episode simply has no synopsis). Only the bool tells them apart, and
            // conflating them turns "no synopsis" into a false drift alarm.
            var nodeFound = parser.TryParseEpisodeOverviewPage(html, out var overview);
            if (!nodeFound)
            {
                nodeMissing.Add(episode.RawNumberLabel);
            }
            else if (string.IsNullOrWhiteSpace(overview))
            {
                empty.Add(episode.RawNumberLabel);
            }
            else
            {
                found++;
            }
        }

        if (nodeMissing.Count > 0)
        {
            report.Add(
                Severity.Error,
                "sinossi-nodo-assente",
                $"su {candidates.Count} pagine episodio campionate, {nodeMissing.Count} non espongono il nodo "
                + $"della descrizione atteso dal parser ({string.Join(", ", nodeMissing)}): struttura del sito cambiata");
        }

        if (empty.Count > 0)
        {
            report.Add(
                Severity.Note,
                "sinossi-vuota",
                $"{empty.Count} episodi campionati espongono il nodo ma senza testo utile "
                + $"({string.Join(", ", empty)}): AnimeClick non ha la sinossi, si passerà ai fallback");
        }

        if (found > 0)
        {
            report.Add(
                Severity.Note,
                "sinossi-ok",
                $"{found}/{candidates.Count} pagine episodio campionate espongono una sinossi italiana");
        }
    }

    private static async Task SearchAsync(
        Fetcher fetcher,
        AnimeClickHtmlParser parser,
        string query,
        CancellationToken cancellationToken)
    {
        // Exactly the URL the search provider builds: /cerca?name=<escaped query>.
        var url = $"{BaseUrl}/cerca?name={Uri.EscapeDataString(query)}";
        var html = await fetcher.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            Console.Error.WriteLine("ricerca non raggiungibile");
            return;
        }

        var results = parser.ParseSearchResults(html, BaseUrl);
        if (results.Count == 0)
        {
            Console.WriteLine($"nessun risultato per \"{query}\" (o il selettore dei risultati non matcha più)");
            return;
        }

        Console.WriteLine($"risultati per \"{query}\":");
        foreach (var result in results.Take(15))
        {
            Console.WriteLine(
                $"  {result.Id,-40} {result.ProductionYear?.ToString() ?? "----"}  {result.Format ?? "-",-12} {result.Title}");
        }
    }

    private static void Print(AnimeReport report, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"── {report.AnimeClickId} — {report.Title ?? "(nessun titolo)"} "
                          + $"({report.Year?.ToString() ?? "anno ignoto"})");
        Console.WriteLine(
            $"   pagine {report.PagesFetched} | righe {report.RowsParsed} | "
            + $"regolari {report.RegularEpisodes} | speciali {report.Specials} | "
            + $"dichiarati {report.DeclaredEpisodeCount?.ToString() ?? "-"} | "
            + $"stagioni dichiarate {report.DeclaredSeasonsCount}");

        var shown = report.Findings
            .Where(f => verbose || f.Severity != Severity.Note)
            .OrderBy(f => f.Severity)
            .ToList();

        if (shown.Count == 0)
        {
            Console.WriteLine("   nessuna anomalia");
            return;
        }

        foreach (var finding in shown)
        {
            Console.WriteLine(finding);
        }
    }

    private static void PrintSummary(List<AnimeReport> reports, Fetcher fetcher, string cacheDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("═══ riepilogo ═══");
        Console.WriteLine($"anime analizzati : {reports.Count}");
        Console.WriteLine($"con errori       : {reports.Count(r => r.HasErrors)}");
        Console.WriteLine($"con avvisi       : {reports.Count(r => !r.HasErrors && r.HasWarnings)}");
        Console.WriteLine($"puliti           : {reports.Count(r => !r.HasErrors && !r.HasWarnings)}");
        Console.WriteLine($"richieste di rete: {fetcher.NetworkRequests} (cache: {fetcher.CacheHits})");
        Console.WriteLine($"cache pagine     : {cacheDirectory}");

        var byCode = reports
            .SelectMany(r => r.Findings)
            .Where(f => f.Severity != Severity.Note)
            .GroupBy(f => f.Code)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (byCode.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("anomalie per tipo:");
            foreach (var group in byCode)
            {
                Console.WriteLine($"  {group.Count(),4}  {group.Key}");
            }
        }

        var worst = reports.Where(r => r.HasErrors).Select(r => r.AnimeClickId).ToList();
        if (worst.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("da guardare per primi:");
            foreach (var id in worst)
            {
                Console.WriteLine($"  {id}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Audit di quello che il plugin scriverebbe in libreria, senza Jellyfin.

            Scarica le pagine reali di AnimeClick e ci esegue il parser e il matcher di
            produzione, poi segnala le anomalie. Le pagine vengono messe in cache su disco:
            rilanciare non ricarica il sito.

              --id <id>              id AnimeClick, es. 44780/boku-no-kokoro-no-yabai-yatsu
                                     (ripetibile)
              --file <path>          file con un id per riga (# per i commenti)
              --search "<titolo>"    cerca il titolo e stampa gli id, poi esce
              --overview-samples <n> pagine episodio da campionare per la sinossi (default 3, 0 disattiva)
              --delay <secondi>      pausa fra richieste di rete (default 1.5)
              --dump-rows            stampa le righe come le vede il parser
              --refresh              ignora la cache e riscarica
              --verbose              mostra anche le note e ogni singolo match

            Esempi:
              AnimeClick.Harness --search "dangers in my heart"
              AnimeClick.Harness --id 44780/boku-no-kokoro-no-yabai-yatsu --verbose
              AnimeClick.Harness --file miei-anime.txt --overview-samples 5

            Esce 1 se almeno un anime presenta errori.
            """);
    }
}
