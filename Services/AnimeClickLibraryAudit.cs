using System;
using System.Collections.Generic;
using System.Linq;
using AnimeClick.Plugin.Models;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Why an episode in the library still has no authoritative Italian title.
/// A valid-looking downstream title can still be stale: the AnimeClick row is the
/// source of truth when episode titles are enabled, so the audit compares both the
/// durable row identity and the current title.
/// </summary>
public enum AnimeClickAuditReason
{
    /// <summary>The current title already matches the AnimeClick row.</summary>
    Ok,

    /// <summary>The series has no AnimeClick ID: nothing can be matched until it is identified.</summary>
    NotIdentified,

    /// <summary>No catalog in cache, so the reason cannot be established without a request.</summary>
    CatalogNotCached,

    /// <summary>The AnimeClick card lists the episodes but publishes no titles for them.</summary>
    CardHasNoTitles,

    /// <summary>Rows repeat the same coordinate, so the numbering carries no usable evidence.</summary>
    NumberingCollision,

    /// <summary>The row was matched; upstream still shows a placeholder instead of a title.</summary>
    TitleNotPublished,

    /// <summary>The authoritative title exists and differs from the library: only a refresh is missing.</summary>
    PendingRefresh,

    /// <summary>The stored numeric identity is absent from the current catalog.</summary>
    RowVanished,

    /// <summary>No identity was ever written and no other cause explains the missing title.</summary>
    NotMatched,

    /// <summary>The title needs attention but the item or Name field is locked.</summary>
    Locked,

    /// <summary>
    /// The season belongs to another AnimeClick card and the sequel traversal could not prove
    /// which one. This is the case the season-level ID field exists for.
    /// </summary>
    CardNotResolved
}

/// <summary>
/// Classifies episode titles from data already on disk. Every decision here is made from a cached
/// catalog and the library's own rows, so an audit of a whole library costs no requests.
/// </summary>
public static class AnimeClickLibraryAudit
{
    private static readonly AnimeClickAuditReason[] SeverityOrder =
    [
        AnimeClickAuditReason.NotIdentified,
        AnimeClickAuditReason.CardNotResolved,
        AnimeClickAuditReason.NumberingCollision,
        AnimeClickAuditReason.NotMatched,
        AnimeClickAuditReason.RowVanished,
        AnimeClickAuditReason.Locked,
        AnimeClickAuditReason.PendingRefresh,
        AnimeClickAuditReason.CatalogNotCached,
        AnimeClickAuditReason.TitleNotPublished,
        AnimeClickAuditReason.CardHasNoTitles,
        AnimeClickAuditReason.Ok
    ];

    /// <summary>Italian one-liners, shown as-is in the configuration page.</summary>
    public static string Describe(AnimeClickAuditReason reason) => reason switch
    {
        AnimeClickAuditReason.Ok => "Tutti gli episodi hanno il titolo AnimeClick aggiornato.",
        AnimeClickAuditReason.NotIdentified =>
            "La serie non è identificata su AnimeClick: identificala per abilitare i titoli.",
        AnimeClickAuditReason.CatalogNotCached =>
            "Nessuna scheda in cache: usa «Analizza» per leggerla e conoscere il motivo.",
        AnimeClickAuditReason.CardHasNoTitles =>
            "La scheda AnimeClick elenca gli episodi senza titolo: non c'è nulla da recuperare.",
        AnimeClickAuditReason.NumberingCollision =>
            "La scheda ripete gli stessi numeri (di solito uno spin-off nella stessa tabella).",
        AnimeClickAuditReason.TitleNotPublished =>
            "Episodio abbinato, ma su AnimeClick il titolo non è ancora stato pubblicato.",
        AnimeClickAuditReason.PendingRefresh =>
            "AnimeClick pubblica un titolo diverso: basta un ricontrollo per applicarlo.",
        AnimeClickAuditReason.RowVanished =>
            "L'identità numerica salvata non compare più nella scheda corrente: analizza la serie.",
        AnimeClickAuditReason.NotMatched =>
            "Nessuna identità AnimeClick scritta su questo episodio: prova il ricontrollo dei titoli. "
            + "Se resiste, la numerazione della scheda non coincide con quella della libreria.",
        AnimeClickAuditReason.Locked =>
            "Il titolo avrebbe bisogno di un ricontrollo, ma l'elemento o il campo Nome è bloccato.",
        AnimeClickAuditReason.CardNotResolved =>
            "La stagione sta su un'altra scheda AnimeClick e la traversata dei sequel non è riuscita "
            + "a dimostrare quale: scrivi l'ID di quel cour nel campo AnimeClick della stagione.",
        _ => string.Empty
    };

    /// <summary>
    /// True when the cached catalog cannot produce titles for anyone, whatever the match does.
    /// </summary>
    public static AnimeClickAuditReason? ClassifyCatalog(AnimeClickEpisodeCatalog? catalog)
    {
        if (catalog is null || catalog.Episodes.Count == 0)
        {
            return AnimeClickAuditReason.CatalogNotCached;
        }

        var relevantRows = catalog.Episodes
            .Where(episode => !episode.IsForeignWork)
            .ToList();
        var withTitle = relevantRows.Count(episode =>
            !string.IsNullOrWhiteSpace(episode.Title)
            && !AnimeClickHtmlParser.IsPlaceholderEpisodeText(episode.Title));
        if (withTitle == 0)
        {
            return AnimeClickAuditReason.CardHasNoTitles;
        }

        if (relevantRows.Any(episode => !episode.IsSpecial && episode.NumberIsAmbiguous))
        {
            return AnimeClickAuditReason.NumberingCollision;
        }

        return null;
    }

    /// <summary>
    /// Historical overload retained for callers that are classifying an already-known missing
    /// title. New library paths should also supply the current title and its placeholder state.
    /// </summary>
    public static AnimeClickAuditReason ClassifyEpisode(
        string? episodeAnimeClickId,
        AnimeClickEpisodeCatalog? catalog)
        => ClassifyEpisode(episodeAnimeClickId, currentTitle: null, titleNeedsRepair: true, catalog);

    /// <summary>
    /// Compares one library episode with its cached AnimeClick row. A complete downstream title is
    /// still repairable when it differs from the now-published Italian title. A complete item with
    /// no usable catalog is left alone rather than being reported as a speculative problem.
    /// </summary>
    public static AnimeClickAuditReason ClassifyEpisode(
        string? episodeAnimeClickId,
        string? currentTitle,
        bool titleNeedsRepair,
        AnimeClickEpisodeCatalog? catalog)
    {
        var catalogVerdict = ClassifyCatalog(catalog);
        if (catalogVerdict is not null)
        {
            return titleNeedsRepair ? catalogVerdict.Value : AnimeClickAuditReason.Ok;
        }

        if (string.IsNullOrWhiteSpace(episodeAnimeClickId))
        {
            return titleNeedsRepair ? AnimeClickAuditReason.NotMatched : AnimeClickAuditReason.Ok;
        }

        var rows = catalog!.Episodes
            .Where(episode => !episode.IsForeignWork)
            .Where(episode => AnimeClickEpisodeProviderId.Equals(
                episode.ProviderId,
                episodeAnimeClickId))
            .ToList();
        if (rows.Count != 1)
        {
            return titleNeedsRepair ? AnimeClickAuditReason.RowVanished : AnimeClickAuditReason.Ok;
        }

        var rowTitle = rows[0].Title;
        if (string.IsNullOrWhiteSpace(rowTitle)
            || AnimeClickHtmlParser.IsPlaceholderEpisodeText(rowTitle))
        {
            return titleNeedsRepair
                ? AnimeClickAuditReason.TitleNotPublished
                : AnimeClickAuditReason.Ok;
        }

        return titleNeedsRepair || !AnimeClickEpisodeProviderId.TitlesEquivalent(currentTitle, rowTitle)
            ? AnimeClickAuditReason.PendingRefresh
            : AnimeClickAuditReason.Ok;
    }

    /// <summary>
    /// Marks an actionable title refresh as locked without hiding unrelated audit causes.
    /// </summary>
    public static AnimeClickAuditReason ApplyNameLock(
        AnimeClickAuditReason reason,
        bool isNameLocked)
        => reason == AnimeClickAuditReason.PendingRefresh && isNameLocked
            ? AnimeClickAuditReason.Locked
            : reason;

    /// <summary>
    /// True when a recheck can still produce the title: either the row already carries it, or the
    /// evidence has to be read before anything can be concluded.
    ///
    /// Separating this from the rest is what keeps the report honest. Counting every episode without
    /// a title as "to fix" put the cards that publish no titles at all — the majority of the backlog
    /// on a real library — permanently in the same bucket as real work, so the number never moved
    /// and the only reasonable conclusion was that the plugin did nothing.
    /// </summary>
    public static bool IsRecoverableByRecheck(AnimeClickAuditReason reason)
        => reason is AnimeClickAuditReason.PendingRefresh
            or AnimeClickAuditReason.RowVanished
            or AnimeClickAuditReason.CatalogNotCached
            or AnimeClickAuditReason.NotMatched;

    /// <summary>
    /// True when the row exists and AnimeClick has simply not published its title yet: nothing to
    /// do now, and nothing wrong either.
    /// </summary>
    public static bool IsWaitingForSource(AnimeClickAuditReason reason)
        => reason == AnimeClickAuditReason.TitleNotPublished;

    /// <summary>
    /// The headline cause for a series: the most actionable one among the episodes that need a
    /// title. Reporting the most frequent instead would bury a single real failure under a pile of
    /// episodes the source will never title.
    /// </summary>
    public static AnimeClickAuditReason Summarize(IEnumerable<AnimeClickAuditReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        var present = reasons.ToHashSet();
        foreach (var candidate in SeverityOrder)
        {
            if (present.Contains(candidate))
            {
                return candidate;
            }
        }

        return AnimeClickAuditReason.Ok;
    }
}
