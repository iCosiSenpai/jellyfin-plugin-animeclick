using System;
using System.Collections.Generic;
using System.Linq;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;

namespace AnimeClick.Plugin.Api;

// Requests and answers of AnimeClickDiagnosticsController: the shapes the configuration page reads.

public sealed class LookupDiagnosticResponse
{
    public string? Name { get; set; }
    public int? Year { get; set; }
    public string? ImageUrl { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class EpisodesDiagnosticResponse
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int EpisodeCount { get; set; }
    public int? DeclaredEpisodeCount { get; set; }
    public int DeclaredSeasonsCount { get; set; }
    public string LayoutFingerprint { get; set; } = string.Empty;
    public bool PaginationComplete { get; set; }
    public List<EpisodeDiagnosticItem> Episodes { get; set; } = [];
    public string? MatchStrategy { get; set; }
    public double? MatchConfidence { get; set; }
    public string? MatchReason { get; set; }
    public EpisodeDiagnosticItem? MatchedEpisode { get; set; }
}

public sealed class EpisodeDiagnosticItem
{
    public int? SeasonNumber { get; set; }
    public int? RawSeasonNumber { get; set; }
    public bool SeasonNumberIsSynthetic { get; set; }
    public string RawNumberLabel { get; set; } = string.Empty;
    public int Number { get; set; }
    public int? NumberEnd { get; set; }
    public int AbsoluteNumber { get; set; }
    public int GlobalOrdinal { get; set; }
    public int SeasonOrdinalNumber { get; set; }
    public int SpecialOrdinalNumber { get; set; }
    public bool IsSpecial { get; set; }
    public bool HasNonStandardNumber { get; set; }
    public bool NumberIsAmbiguous { get; set; }
    public string? Title { get; set; }
    public string? ProviderId { get; set; }
    public string? DetailUrl { get; set; }

    public static EpisodeDiagnosticItem From(AnimeClickEpisode episode)
        => new()
        {
            SeasonNumber = episode.SeasonNumber,
            RawSeasonNumber = episode.RawSeasonNumber,
            SeasonNumberIsSynthetic = episode.SeasonNumberIsSynthetic,
            RawNumberLabel = episode.RawNumberLabel,
            Number = episode.Number,
            NumberEnd = episode.NumberEnd,
            AbsoluteNumber = episode.AbsoluteNumber,
            GlobalOrdinal = episode.GlobalOrdinal,
            SeasonOrdinalNumber = episode.SeasonOrdinalNumber,
            SpecialOrdinalNumber = episode.SpecialOrdinalNumber,
            IsSpecial = episode.IsSpecial,
            HasNonStandardNumber = episode.HasNonStandardNumber,
            NumberIsAmbiguous = episode.NumberIsAmbiguous,
            Title = episode.Title,
            ProviderId = episode.ProviderId,
            DetailUrl = episode.DetailUrl
        };
}

public sealed class ClearCacheRequest
{
    public string? Key { get; set; }
    public string? Prefix { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class ClearCacheResponse
{
    public int Removed { get; set; }
}

public sealed class TestFanartRequest
{
    public string? PersonalApiKey { get; set; }
    public string? ProjectApiKey { get; set; }
}

public sealed class TestTmdbRequest
{
    public string? ApiKey { get; set; }
}

public sealed class TestAiRequest
{
    /// <summary>Identifier of the selected service, empty to use the saved one.</summary>
    public string? Provider { get; set; }

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TestTvdbRequest
{
    public string? ApiKey { get; set; }
}


public sealed class TranslationPreviewRequest
{
    public string? SourceText { get; set; }
    public string? Provider { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TranslationPreviewResponse
{
    public bool Success { get; set; }
    public string? Translation { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "it";
    public int SourceCharacterCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class EpisodeFallbackPreviewRequest
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int Season { get; set; } = 1;
    public int Episode { get; set; } = 1;
}

public sealed class EpisodeFallbackPreviewResponse
{
    public bool Success { get; set; }
    public string? Overview { get; set; }
    public string? EpisodeTitle { get; set; }
    public string? Source { get; set; }
    public string? SourceLanguage { get; set; }
    public bool UsedAi { get; set; }
    public string? Model { get; set; }
    public bool AnimeClickMatchConclusive { get; set; }
    public string? AnimeClickMatchStrategy { get; set; }
    public double? AnimeClickMatchConfidence { get; set; }
    public string? AnimeClickMatchReason { get; set; }
    public List<FallbackChainStep> Chain { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public sealed record FallbackChainStep(
    string Source,
    string Language,
    bool RequiresTranslation,
    bool Configured);


public sealed class LibraryAuditResponse
{
    public bool AlternativeTitleLookupEnabled { get; set; }
    /// <summary>False when the whole feature is off, which explains every missing title at once.</summary>
    public bool EpisodeTitlesEnabled { get; set; }

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }

    public int MissingTitleCount { get; set; }

    /// <summary>Episodes a recheck can still fix.</summary>
    public int RecoverableTitleCount { get; set; }

    /// <summary>Episodes "Sistema tutti i titoli" will inspect: not locked and with a source to ask.</summary>
    public int CheckableTitleCount { get; set; }

    /// <summary>Episodes whose row exists but whose title AnimeClick has not published yet.</summary>
    public int WaitingTitleCount { get; set; }

    /// <summary>
    /// Episodes no request can fix: the card lists them without titles, the numbering is ambiguous,
    /// the field is locked, or the season needs its own card ID.
    /// </summary>
    public int UnavailableTitleCount { get; set; }

    public List<LibraryAuditReasonCount> Totals { get; set; } = [];

    public List<LibraryAuditSeriesItem> Series { get; set; } = [];
}

public sealed class LibraryAuditReasonCount
{
    public string Reason { get; set; } = string.Empty;

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }
}

public sealed class LibraryAuditSeriesItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? AnimeClickId { get; set; }

    public int EpisodeCount { get; set; }

    public int MissingTitleCount { get; set; }

    /// <summary>Of the missing ones, those a recheck can still fix.</summary>
    public int RecoverableTitleCount { get; set; }

    /// <summary>Of the missing ones, those "Sistema tutti i titoli" will inspect.</summary>
    public int CheckableTitleCount { get; set; }

    /// <summary>Of the missing ones, those only waiting for AnimeClick to publish the title.</summary>
    public int WaitingTitleCount { get; set; }

    /// <summary>Of the missing ones, those no request can change.</summary>
    public int UnavailableTitleCount { get; set; }

    /// <summary>Rows read from the card. Only filled by the single-series, network-allowed audit.</summary>
    public int? CardRowCount { get; set; }

    public string Reason { get; set; } = nameof(AnimeClickAuditReason.Ok);

    public string ReasonLabel { get; set; } = string.Empty;

    public List<LibraryAuditSeasonItem> Seasons { get; set; } = [];

    /// <summary>
    /// Fills the counts and the headline cause from the episodes that still need a title. Shared by
    /// the library-wide report and the single-series re-read: the latter used to set only the total,
    /// so re-reading a series made its recoverable, waiting and unavailable counts drop to zero.
    /// </summary>
    internal void ApplyReasons(IReadOnlyCollection<AnimeClickAuditReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        // Three separate numbers, because they mean three different things to the user: what a
        // recheck can still fix, what is only waiting for AnimeClick to publish, and what no
        // request will ever change.
        MissingTitleCount = reasons.Count;
        RecoverableTitleCount = reasons.Count(AnimeClickLibraryAudit.IsRecoverableByRecheck);
        WaitingTitleCount = reasons.Count(AnimeClickLibraryAudit.IsWaitingForSource);
        UnavailableTitleCount = MissingTitleCount - RecoverableTitleCount - WaitingTitleCount;
        var reason = reasons.Count == 0 ? AnimeClickAuditReason.Ok : AnimeClickLibraryAudit.Summarize(reasons);
        Reason = reason.ToString();
        ReasonLabel = AnimeClickLibraryAudit.Describe(reason);
    }
}

public sealed class LibraryAuditSeasonItem
{
    public string SeasonId { get; set; } = string.Empty;

    public int? SeasonNumber { get; set; }

    public int MissingTitleCount { get; set; }

    public string? AnimeClickId { get; set; }

    /// <summary>True when this card came from the traversal or from an ID stored on the season.</summary>
    public bool CardIsResolved { get; set; }

    /// <summary>Rows read from the card used for this season.</summary>
    public int? CardRowCount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string ReasonLabel { get; set; } = string.Empty;
}

public sealed class LibraryAuditSeriesRequest
{
    public string? ItemId { get; set; }
}

public sealed class RunTaskResponse
{
    public bool Queued { get; set; }

    public string Message { get; set; } = string.Empty;
}


/// <summary>One selectable AI service, as the configuration page needs to render it.</summary>
public sealed class AiProviderInfo
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ChatEndpoint { get; set; } = string.Empty;

    public bool RequiresApiKey { get; set; }

    public bool SupportsModelListing { get; set; }

    public string CredentialUrl { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}
