using System;
using System.Collections.Generic;
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

    public async Task<string?> ResolveAsync(
        string mainId,
        int? seasonNumber,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!seasonNumber.HasValue
            || seasonNumber.Value <= 1
            || !AnimeClickClient.TryNormalizeAnimeClickId(mainId, out var normalizedMainId))
        {
            return null;
        }

        var cacheKey = $"seasonMap:v4::{normalizedMainId}::{seasonNumber.Value}";
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

            var explicitSequels = relations.Where(IsExplicitSequel).ToList();
            if (explicitSequels.Count == 0)
            {
                return relations.All(IsRecognizedNonSequelRelation)
                    ? SeasonResolutionOutcome.ConfirmedAbsent
                    : SeasonResolutionOutcome.Ambiguous;
            }

            var candidates = explicitSequels
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

            foreach (var candidate in candidates)
            {
                _logger.LogDebug(
                    "AnimeClick sequel candidate step=S{Season} rootSimilarity={RootSimilarity:F2} currentSimilarity={CurrentSimilarity:F2} relation={Relation} title={Title} year={Year} id={Id}",
                    targetSeason,
                    candidate.RootSimilarity,
                    candidate.CurrentSimilarity,
                    candidate.Relation.RelationType,
                    candidate.Relation.Title,
                    candidate.Relation.Year,
                    candidate.Id);
            }

            if (candidates.Count != 1)
            {
                _logger.LogInformation(
                    "AnimeClick: Season {Season} sequel traversal stopped for {Title}: {Count} safe candidates from {ExplicitCount} explicit sequel relations",
                    targetSeason,
                    currentTitle,
                    candidates.Count,
                    explicitSequels.Count);
                return SeasonResolutionOutcome.Ambiguous;
            }

            var selected = candidates[0];
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

    internal static double FranchiseSimilarity(string? mainTitle, string? candidateTitle)
    {
        var mainTokens = NormalizeFranchiseTokens(mainTitle);
        var candidateTokens = NormalizeFranchiseTokens(candidateTitle);
        if (mainTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        var intersection = mainTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = mainTokens.Union(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

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
        normalized = Regex.Replace(normalized, @"\b(?:season|stagione)\s*\d+\b", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+(?:\d+|ii|iii|iv|v|s|t)\s*$", " ", RegexOptions.IgnoreCase);

        return Regex.Split(normalized, @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length > 0 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

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
