using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Resolves a Jellyfin season by following one unambiguous, explicit AnimeClick
/// sequel edge for each season step. Positional ordering of all franchise
/// relations is intentionally never used.
/// </summary>
public sealed class AnimeClickSeasonResolver
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "no", "to", "and", "season", "stagione"
    };

    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickSeasonResolver> _logger;

    public AnimeClickSeasonResolver(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickSeasonResolver> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// Returns a season traversal only when the provider has already proved and cached it. This is
    /// the audit-safe counterpart of <see cref="ResolveAsync"/>: it never contacts AnimeClick and
    /// therefore lets whole-library diagnostics replay the same card choice without network I/O.
    /// </summary>
    public async Task<string?> ResolveCachedAsync(
        string mainId,
        int? seasonNumber,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<int, int>? seasonAirYears = null)
    {
        if (!seasonNumber.HasValue
            || seasonNumber.Value <= 1
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainId, out var normalizedMainId))
        {
            return null;
        }

        var expectedYear = seasonAirYears?.GetValueOrDefault(seasonNumber.Value);
        var yearKey = expectedYear is > 0
            ? expectedYear.Value.ToString(CultureInfo.InvariantCulture)
            : "na";
        var cacheKey = $"seasonMap:v6::{normalizedMainId}::{seasonNumber.Value}::{yearKey}";
        var cached = await _cache
            .GetAsync<string>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            _logger.LogDebug("AnimeClick season map cache-only hit: {Key}", cacheKey);
        }

        return string.IsNullOrWhiteSpace(cached) ? null : cached;
    }

    public async Task<string?> ResolveAsync(
        string mainId,
        int? seasonNumber,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<int, int>? seasonAirYears = null)
    {
        if (!seasonNumber.HasValue
            || seasonNumber.Value <= 1
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainId, out var normalizedMainId))
        {
            return null;
        }

        // The expected year is part of the identity of the answer: the same card resolved without
        // it is a weaker statement than one corroborated by when the season actually aired.
        var expectedYear = seasonAirYears?.GetValueOrDefault(seasonNumber.Value);
        var yearKey = expectedYear is > 0
            ? expectedYear.Value.ToString(CultureInfo.InvariantCulture)
            : "na";
        var cacheKey = $"seasonMap:v6::{normalizedMainId}::{seasonNumber.Value}::{yearKey}";
        var missCacheKey = cacheKey + "::miss";
        var cached = await _cache
            .GetAsync<string>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            _logger.LogDebug("AnimeClick season map cache hit: {Key}", cacheKey);
            return cached;
        }

        var cachedMiss = await _cache
            .GetAsync<string>(missCacheKey, configuration.NegativeCacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(cachedMiss, "miss", StringComparison.Ordinal))
        {
            _logger.LogDebug("AnimeClick season map negative cache hit: {Key}", missCacheKey);
            return null;
        }

        try
        {
            var outcome = await ResolveCoreAsync(
                    normalizedMainId,
                    seasonNumber.Value,
                    seasonAirYears,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            switch (outcome.Status)
            {
                case SeasonResolutionStatus.Resolved when outcome.ResolvedId is not null:
                    await _cache.SetAsync(cacheKey, outcome.ResolvedId, cancellationToken).ConfigureAwait(false);
                    return outcome.ResolvedId;
                case SeasonResolutionStatus.ConfirmedAbsent:
                    await _cache.SetAsync(missCacheKey, "miss", cancellationToken).ConfigureAwait(false);
                    return null;
                case SeasonResolutionStatus.Ambiguous:
                    _logger.LogInformation(
                        "AnimeClick: sequel traversal for {Id} S{Season} is ambiguous; result not cached",
                        normalizedMainId,
                        seasonNumber.Value);
                    return null;
                default:
                    return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A network/parser failure is not evidence that this season shares the
            // main page. Do not cache it as a miss.
            _logger.LogDebug(
                ex,
                "AnimeClick: sequel traversal failed for {Id} S{Season}; result not cached",
                normalizedMainId,
                seasonNumber.Value);
            return null;
        }
    }

    private async Task<SeasonResolutionOutcome> ResolveCoreAsync(
        string mainId,
        int seasonNumber,
        IReadOnlyDictionary<int, int>? seasonAirYears,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, mainId, out var mainUrl))
        {
            return SeasonResolutionOutcome.Incomplete;
        }

        var animeCacheKey = $"anime::{mainUrl}";
        var mainAnime = await _cache
            .GetAsync<AnimeClickAnime>(animeCacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (mainAnime is null)
        {
            var mainHtml = await _client
                .GetStringAsync(mainUrl, configuration, cancellationToken)
                .ConfigureAwait(false);
            mainAnime = _parser.ParseAnimePage(mainUrl, mainHtml);
            if (string.IsNullOrWhiteSpace(mainAnime.Title))
            {
                return SeasonResolutionOutcome.Incomplete;
            }

            await _cache.SetAsync(animeCacheKey, mainAnime, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(mainAnime.Title))
        {
            return SeasonResolutionOutcome.Incomplete;
        }

        var rootTitle = mainAnime.Title;
        var currentTitle = mainAnime.Title;
        var currentYear = mainAnime.ProductionYear;
        var currentId = mainId;
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            GetStableIdentity(mainId)
        };

        for (var targetSeason = 2; targetSeason <= seasonNumber; targetSeason++)
        {
            if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, currentId, out var currentUrl))
            {
                return SeasonResolutionOutcome.Incomplete;
            }

            var relationsHtml = await _client
                .GetStringAsync(currentUrl + "/relazioni", configuration, cancellationToken)
                .ConfigureAwait(false);
            var relations = _parser.ParseRelationsPage(relationsHtml, configuration.BaseUrl);

            // An empty parse cannot prove that AnimeClick has no sequel: it may
            // be a valid empty page, an interstitial, or selector drift. Do not
            // turn that structural uncertainty into a negative cache entry.
            if (relations.Count == 0)
            {
                return SeasonResolutionOutcome.Incomplete;
            }

            var expectedYear = seasonAirYears?.GetValueOrDefault(targetSeason);
            var explicitSequels = relations.Where(IsExplicitSequel).ToList();
            var typed = explicitSequels.Count > 0;

            // Half of AnimeClick's older pages carry no relation type at all: Clannad lists
            // "After Story" next to the movie and the OVA with nothing saying which one continues
            // the story, and the same is true of Kaguya-sama, Kimi ni Todoke and Index. Those
            // pages are still usable when the library itself can corroborate the answer, because
            // the year a season aired is a fact the user's own episodes carry. So the untyped list
            // is considered only when that year is known, and only with every other filter on.
            if (!typed && expectedYear is not > 0)
            {
                return relations.All(IsRecognizedNonSequelRelation)
                    ? SeasonResolutionOutcome.ConfirmedAbsent
                    : SeasonResolutionOutcome.Ambiguous;
            }

            var candidates = (typed ? explicitSequels : relations)
                .Where(IsTelevisionSeries)
                .Where(relation => !IsExcludedTitle(relation.Title))
                .Select(relation => CreateCandidate(relation, rootTitle, currentTitle))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .Where(candidate => !visited.Contains(candidate.Identity))
                .Where(candidate => !RelationPredatesCurrent(candidate.Relation, currentYear))
                .Where(candidate => candidate.RootSimilarity >= 0.50
                    && candidate.CurrentSimilarity >= 0.50)
                .GroupBy(candidate => candidate.Identity, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (!typed)
            {
                // A remake carries the very same name as the work it remakes, while a sequel adds
                // something to it: "After Story", "II", "2nd Season". With no declared relation
                // type, that is the only thing that tells the two apart.
                candidates = candidates
                    .Where(candidate => !IsSameTitle(candidate.Relation.Title, currentTitle))
                    .ToList();
            }

            foreach (var candidate in candidates)
            {
                _logger.LogDebug(
                    "AnimeClick sequel candidate step=S{Season} typed={Typed} expectedYear={ExpectedYear} rootSimilarity={RootSimilarity:F2} currentSimilarity={CurrentSimilarity:F2} relation={Relation} title={Title} year={Year} id={Id}",
                    targetSeason,
                    typed,
                    expectedYear,
                    candidate.RootSimilarity,
                    candidate.CurrentSimilarity,
                    candidate.Relation.RelationType,
                    candidate.Relation.Title,
                    candidate.Relation.Year,
                    candidate.Id);
            }

            var chosen = SelectUniqueByAirYear(
                candidates.Select(candidate => candidate.Relation).ToList(),
                expectedYear,
                requireYearMatch: !typed);

            // Nothing on broadcast television: a modern continuation may have gone out on the web
            // instead. Those are admissible only on an exact year match, so a franchise's web
            // spin-off can never pass for the season beside it.
            if (chosen is null && !typed && expectedYear is > 0)
            {
                var webCandidates = relations
                    .Where(IsWebRelease)
                    .Where(relation => !IsExcludedTitle(relation.Title))
                    .Select(relation => CreateCandidate(relation, rootTitle, currentTitle))
                    .Where(candidate => candidate is not null)
                    .Select(candidate => candidate!)
                    .Where(candidate => !visited.Contains(candidate.Identity))
                    .Where(candidate => !RelationPredatesCurrent(candidate.Relation, currentYear))
                    .Where(candidate => candidate.RootSimilarity >= 0.50
                        && candidate.CurrentSimilarity >= 0.50)
                    .Where(candidate => !IsSameTitle(candidate.Relation.Title, currentTitle))
                    .GroupBy(candidate => candidate.Identity, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                chosen = SelectUniqueByAirYear(
                    webCandidates.Select(candidate => candidate.Relation).ToList(),
                    expectedYear,
                    requireYearMatch: true,
                    exactYearOnly: true);
                if (chosen is not null)
                {
                    candidates = webCandidates;
                }
            }

            var selected = chosen is null
                ? null
                : candidates.FirstOrDefault(candidate => ReferenceEquals(candidate.Relation, chosen));
            if (selected is null)
            {
                _logger.LogInformation(
                    "AnimeClick: Season {Season} sequel traversal stopped for {Title}: {Count} safe candidates from {ExplicitCount} explicit sequel relations, expected year {ExpectedYear}",
                    targetSeason,
                    currentTitle,
                    candidates.Count,
                    explicitSequels.Count,
                    expectedYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
                return SeasonResolutionOutcome.Ambiguous;
            }
            visited.Add(selected.Identity);
            currentId = selected.Id;
            currentTitle = selected.Relation.Title;
            currentYear = selected.Relation.Year ?? currentYear;
        }

        _logger.LogInformation(
            "AnimeClick: Season {Season} resolved by explicit sequel traversal {Title} → {Id}",
            seasonNumber,
            rootTitle,
            currentId);
        return new SeasonResolutionOutcome(currentId, SeasonResolutionStatus.Resolved);
    }

    /// <summary>
    /// How much two franchise titles agree, on a 0..1 scale, after stripping the words a sequel
    /// adds without changing the work.
    /// <para>
    /// A plain Jaccard punished exactly the pattern it had to accept: a sequel whose card carries
    /// a subtitle. "Clannad After Story" against "Clannad" scored 1/3 = 0.33 and was refused, and
    /// so were "Kaguya-sama wa Kokurasetai? Tensai-tachi no Renai Zunousen", "Fruits Basket 2nd
    /// Season" and a dozen other second cours — the single largest cause of seasons left without
    /// Italian titles. When one title's tokens are entirely contained in the other's, one work is
    /// naming the other and the score is 1; otherwise the Jaccard still decides, so titles that
    /// merely share a franchise word ("Toaru Kagaku no Railgun" against "Toaru Majutsu no Index",
    /// "Fate/Zero" against "Fate/kaleid liner") stay below the threshold as before.
    /// </para>
    /// </summary>
    internal static double FranchiseSimilarity(string? mainTitle, string? candidateTitle)
    {
        var mainTokens = NormalizeFranchiseTokens(mainTitle);
        var candidateTokens = NormalizeFranchiseTokens(candidateTitle);
        if (mainTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        if (mainTokens.IsSubsetOf(candidateTokens) || candidateTokens.IsSubsetOf(mainTokens))
        {
            return 1;
        }

        var intersection = mainTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = mainTokens.Union(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// Picks the one candidate that can be the next season, using the year the season actually
    /// aired in the library as the tie-breaker.
    /// <para>
    /// A page that lists several later seasons of the same franchise — Kimi ni Todoke offers its
    /// 2011 and its 2024 continuation side by side — used to be refused outright as ambiguous.
    /// The year the user's own episodes carry says which one is being asked for. When the relation
    /// type is missing entirely the year is not a tie-breaker but a requirement: it is the only
    /// evidence that the chosen card is a continuation and not some other work of the franchise.
    /// </para>
    /// </summary>
    /// <param name="candidates">Candidates that already passed every safety filter.</param>
    /// <param name="expectedYear">Year the target season aired, when the library knows it.</param>
    /// <param name="requireYearMatch">True when nothing but the year vouches for the candidate.</param>
    /// <returns>The single admissible candidate, or null when the step stays ambiguous.</returns>
    internal static AnimeClickRelation? SelectUniqueByAirYear(
        IReadOnlyList<AnimeClickRelation> candidates,
        int? expectedYear,
        bool requireYearMatch,
        bool exactYearOnly = false)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (expectedYear is > 0)
        {
            // The exact year first: a franchise often puts two consecutive seasons one year apart,
            // and Fruits Basket's 2020 second season and 2021 finale would otherwise cancel each
            // other out. Only if nothing lands on the year itself is one year of slack allowed,
            // for the cour that starts in October and ends in January.
            var exact = candidates
                .Where(candidate => candidate.Year == expectedYear.Value)
                .ToList();
            if (exact.Count == 1)
            {
                return exact[0];
            }

            if (exact.Count == 0 && !exactYearOnly)
            {
                var near = candidates
                    .Where(candidate => candidate.Year is int year
                        && Math.Abs(year - expectedYear.Value) == 1)
                    .ToList();
                if (near.Count == 1)
                {
                    return near[0];
                }
            }

            if (requireYearMatch || candidates.Count > 1)
            {
                return null;
            }
        }
        else if (requireYearMatch)
        {
            return null;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// A release AnimeClick files as web or ONA rather than broadcast television. Modern
    /// continuations arrive this way — "Arrivare a te" got its third season on Netflix in 2024 —
    /// so they can still be the next season, but only ever on an exact year match: that is what
    /// keeps a franchise's web spin-off from being read as the season next to it.
    /// </summary>
    private static bool IsWebRelease(AnimeClickRelation relation)
        => Regex.IsMatch(
            relation.Format ?? string.Empty,
            @"\b(Web|ONA)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsSameTitle(string? left, string? right)
        => string.Equals(
            AnimeClickSearchScorer.RemoveDiacritics(left ?? string.Empty).Trim(),
            AnimeClickSearchScorer.RemoveDiacritics(right ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static SeasonCandidate? CreateCandidate(
        AnimeClickRelation relation,
        string rootTitle,
        string currentTitle)
    {
        if (!AnimeClickClient.TryNormalizeAnimeClickId(relation.AnimeClickId, out var normalizedId))
        {
            return null;
        }

        return new SeasonCandidate(
            relation,
            normalizedId,
            GetStableIdentity(normalizedId),
            FranchiseSimilarity(rootTitle, relation.Title),
            FranchiseSimilarity(currentTitle, relation.Title));
    }

    private static HashSet<string> NormalizeFranchiseTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = AnimeClickSearchScorer.RemoveDiacritics(title).ToLowerInvariant();

        // Words a sequel adds without naming a different work: "2nd Season", "Part 2",
        // "Final Season", "Cour 2". Removing them lets the containment check above see that one
        // title is the other plus a marker.
        normalized = Regex.Replace(
            normalized,
            @"\b(?:final(?:e)?\s+(?:season|stagione|cour|arc)|season|stagione|cour|part|parte|\d+(?:st|nd|rd|th))\s*\d*\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\s+(?:\d+|ii|iii|iv|v|s|t)\s*$", " ", RegexOptions.IgnoreCase);

        return Regex.Split(normalized, @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length > 0 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the card declares a continuation of itself. Used to refuse reading a card as a
    /// later season: if a sequel exists, this card is not the franchise's last cour, and a library
    /// season numbered above one might well be that sequel rather than this card.
    /// </summary>
    internal static bool DeclaresExplicitSequel(IEnumerable<AnimeClickRelation>? relations)
        => relations is not null && relations.Any(IsExplicitSequel);

    private static bool IsExplicitSequel(AnimeClickRelation relation)
    {
        var relationType = AnimeClickSearchScorer
            .RemoveDiacritics(relation.RelationType ?? string.Empty)
            .ToLowerInvariant();
        return Regex.IsMatch(
            relationType,
            @"\b(sequel|seguito|continuazione)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool IsRecognizedNonSequelRelation(AnimeClickRelation relation)
    {
        var relationType = AnimeClickSearchScorer
            .RemoveDiacritics(relation.RelationType ?? string.Empty)
            .ToLowerInvariant();
        return Regex.IsMatch(
            relationType,
            @"\b(prequel|spin[\s-]?off|opera derivata|derivato|alternativa|remake|riassunto|adattamento|storia parallela|side story)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool IsTelevisionSeries(AnimeClickRelation relation)
    {
        var format = relation.Format ?? string.Empty;
        return format.Contains("Serie TV", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format.Trim(), "TV", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedTitle(string? title)
        => Regex.IsMatch(
            title ?? string.Empty,
            @"\b(Alternative|Gaiden|Spin[\s-]?[Oo]ff|Bangai[\s-]?[Hh]en|OVA|OAV|Special)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool RelationPredatesCurrent(AnimeClickRelation relation, int? currentYear)
        => relation.Year.HasValue && currentYear.HasValue && relation.Year.Value < currentYear.Value;

    private static string GetStableIdentity(string normalizedId)
    {
        var slash = normalizedId.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? normalizedId[..slash] : normalizedId;
    }

    private sealed record SeasonCandidate(
        AnimeClickRelation Relation,
        string Id,
        string Identity,
        double RootSimilarity,
        double CurrentSimilarity);

    private enum SeasonResolutionStatus
    {
        Resolved,
        ConfirmedAbsent,
        Ambiguous,
        Incomplete
    }

    private sealed record SeasonResolutionOutcome(
        string? ResolvedId,
        SeasonResolutionStatus Status)
    {
        public static SeasonResolutionOutcome ConfirmedAbsent { get; } =
            new(null, SeasonResolutionStatus.ConfirmedAbsent);
        public static SeasonResolutionOutcome Ambiguous { get; } =
            new(null, SeasonResolutionStatus.Ambiguous);
        public static SeasonResolutionOutcome Incomplete { get; } =
            new(null, SeasonResolutionStatus.Incomplete);
    }
}
