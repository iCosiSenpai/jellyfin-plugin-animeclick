using System;
using System.Collections.Generic;
using System.Linq;
using AnimeClick.Plugin.Models;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Why an episode in the library still has no Italian title.
/// <para>
/// A missing title looks identical from the outside whatever the cause, and the causes call for
/// opposite reactions: one is fixed by a button, one will fix itself next week, one is a gap in the
/// source that no amount of refreshing will close, and one is a real matching failure worth
/// reporting. Presenting them as one number is what makes a working plugin look broken.
/// </para>
/// </summary>
public enum AnimeClickAuditReason
{
    /// <summary>Every episode carries a real title.</summary>
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

    /// <summary>The title exists upstream and the identity is written: only a refresh is missing.</summary>
    PendingRefresh,

    /// <summary>The stored identity points at a row the current catalog no longer contains.</summary>
    RowVanished,

    /// <summary>No identity was ever written and no other cause explains it.</summary>
    NotMatched,

    /// <summary>
    /// The season belongs to another AnimeClick card and the sequel traversal could not prove
    /// which one. This is the case the season-level ID field exists for.
    /// </summary>
    CardNotResolved
}

/// <summary>
/// Classifies missing episode titles from data already on disk. Every decision here is made from a
/// cached catalog and the library's own rows, so an audit of a whole library costs no requests.
/// </summary>
public static class AnimeClickLibraryAudit
{
    /// <summary>
    /// Ranked by how much the user can do about it. When one series shows several causes the report
    /// leads with the most actionable one, because that is the one worth a click.
    /// </summary>
    private static readonly AnimeClickAuditReason[] SeverityOrder =
    [
        AnimeClickAuditReason.NotIdentified,
        AnimeClickAuditReason.CardNotResolved,
        AnimeClickAuditReason.NumberingCollision,
        AnimeClickAuditReason.NotMatched,
        AnimeClickAuditReason.RowVanished,
        AnimeClickAuditReason.PendingRefresh,
        AnimeClickAuditReason.CatalogNotCached,
        AnimeClickAuditReason.TitleNotPublished,
        AnimeClickAuditReason.CardHasNoTitles,
        AnimeClickAuditReason.Ok
    ];

    /// <summary>Italian one-liners, shown as-is in the configuration page.</summary>
    public static string Describe(AnimeClickAuditReason reason) => reason switch
    {
        AnimeClickAuditReason.Ok => "Tutti gli episodi hanno un titolo.",
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
            "Il titolo esiste su AnimeClick: basta un ricontrollo per scriverlo.",
        AnimeClickAuditReason.RowVanished =>
            "L'identità salvata punta a una riga che la scheda non contiene più: svuota la cache.",
        AnimeClickAuditReason.NotMatched =>
            "Nessuna identità AnimeClick scritta su questo episodio: prova il ricontrollo dei titoli. "
            + "Se resiste, la numerazione della scheda non coincide con quella della libreria.",
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

        var withTitle = catalog.Episodes.Count(episode =>
            !string.IsNullOrWhiteSpace(episode.Title)
            && !AnimeClickHtmlParser.IsPlaceholderEpisodeText(episode.Title));
        if (withTitle == 0)
        {
            return AnimeClickAuditReason.CardHasNoTitles;
        }

        // Only a collision among the regular rows hides titles: ambiguous specials are matched by
        // their own ordinal and stay reachable.
        if (catalog.Episodes.Any(episode => !episode.IsSpecial && episode.NumberIsAmbiguous))
        {
            return AnimeClickAuditReason.NumberingCollision;
        }

        return null;
    }

    /// <summary>
    /// Explains one untitled episode. <paramref name="episodeAnimeClickId"/> is the identity the
    /// provider wrote on it, which is what separates "matched but no title upstream" from "never
    /// matched" — the whole point of the report.
    /// </summary>
    public static AnimeClickAuditReason ClassifyEpisode(
        string? episodeAnimeClickId,
        AnimeClickEpisodeCatalog? catalog)
    {
        var catalogVerdict = ClassifyCatalog(catalog);
        if (catalogVerdict is not null)
        {
            return catalogVerdict.Value;
        }

        if (string.IsNullOrWhiteSpace(episodeAnimeClickId))
        {
            return AnimeClickAuditReason.NotMatched;
        }

        var row = catalog!.Episodes.FirstOrDefault(episode => string.Equals(
            episode.ProviderId,
            episodeAnimeClickId,
            StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return AnimeClickAuditReason.RowVanished;
        }

        return string.IsNullOrWhiteSpace(row.Title)
               || AnimeClickHtmlParser.IsPlaceholderEpisodeText(row.Title)
            ? AnimeClickAuditReason.TitleNotPublished
            : AnimeClickAuditReason.PendingRefresh;
    }

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
