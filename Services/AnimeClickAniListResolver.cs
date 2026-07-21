using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Resolves a high-confidence AniList anime ID for downstream artwork providers.
/// Existing Jellyfin IDs always win; newly discovered mappings are validated by
/// title, year and media format, then cached by stable AnimeClick identity.
/// </summary>
public class AnimeClickAniListResolver
{
    private const double MinimumTitleSimilarity = 0.80;
    private const double AmbiguityMargin = 0.05;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickAniListResolver> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _singleFlight =
        new(StringComparer.Ordinal);

    public AnimeClickAniListResolver(
        IHttpClientFactory httpClientFactory,
        AnimeClickCacheService cache,
        ILogger<AnimeClickAniListResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves and caches only a unique, high-confidence mapping. A successful
    /// API response with no safe candidate is negatively cached; transport and
    /// parse failures are never cached as misses.
    /// </summary>
    public async Task<string?> ResolveAniListIdAsync(
        string animeClickId,
        string? primaryTitle,
        string? alternateTitle,
        int? productionYear,
        bool seriesRequest,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!AnimeClickClient.TryNormalizeAnimeClickId(animeClickId, out var normalizedId)
            || !productionYear.HasValue)
        {
            // A title/format-only match is not strong enough to persist a
            // cross-provider identity. Downstream providers can still fill it.
            return null;
        }

        var stableIdentity = GetStableIdentity(normalizedId);
        var mediaKind = seriesRequest ? "series" : "movie";
        var cacheKey = $"anilistId:v3::{stableIdentity}::{mediaKind}";
        var missCacheKey = cacheKey + "::miss";

        var cached = await _cache
            .GetAsync<string>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var cachedMiss = await _cache
            .GetAsync<string>(missCacheKey, configuration.NegativeCacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(cachedMiss, "miss", StringComparison.Ordinal))
        {
            return null;
        }

        var gate = _singleFlight.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await _cache
                .GetAsync<string>(cacheKey, configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            cachedMiss = await _cache
                .GetAsync<string>(missCacheKey, configuration.NegativeCacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(cachedMiss, "miss", StringComparison.Ordinal))
            {
                return null;
            }

            var requestedTitles = new[] { primaryTitle, alternateTitle }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requestedTitles.Length == 0)
            {
                return null;
            }

            var allQueriesCompleted = true;
            foreach (var queryTitle in requestedTitles)
            {
                var lookup = await QueryAsync(queryTitle, cancellationToken).ConfigureAwait(false);
                allQueriesCompleted &= lookup.Completed;
                if (!lookup.Completed)
                {
                    continue;
                }

                var selected = SelectCandidate(
                    lookup.Candidates,
                    requestedTitles,
                    productionYear,
                    seriesRequest);
                if (selected is null)
                {
                    continue;
                }

                var id = selected.Id.ToString(CultureInfo.InvariantCulture);
                await _cache.SetAsync(cacheKey, id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "AniListResolver: mapped AnimeClick {AnimeClickId} to AniList {AniListId} with title score {Score:F2}",
                    stableIdentity,
                    id,
                    selected.TitleSimilarity);
                return id;
            }

            if (allQueriesCompleted)
            {
                await _cache.SetAsync(missCacheKey, "miss", cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AniListQueryResult> QueryAsync(
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            const string graphQl = """
                query ($search: String!) {
                  Page(page: 1, perPage: 10) {
                    media(search: $search, type: ANIME, sort: SEARCH_MATCH) {
                      id
                      type
                      format
                      seasonYear
                      startDate { year }
                      title { romaji english native }
                      synonyms
                    }
                  }
                }
                """;
            var payload = JsonSerializer.Serialize(new
            {
                query = graphQl,
                variables = new { search = title }
            });

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("AniListResolver: AniList returned {Status} for {Title}", response.StatusCode, title);
                return AniListQueryResult.Incomplete;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TryParseCandidates(json, out var candidates)
                ? new AniListQueryResult(candidates, true)
                : AniListQueryResult.Incomplete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniListResolver: failed for {Title}", title);
            return AniListQueryResult.Incomplete;
        }
    }

    private static AniListCandidate? SelectCandidate(
        IReadOnlyList<AniListCandidate> candidates,
        IReadOnlyList<string> requestedTitles,
        int? productionYear,
        bool seriesRequest)
    {
        var ranked = candidates
            .Where(candidate => IsCompatibleFormat(candidate.Format, seriesRequest))
            .Where(candidate => IsCompatibleYear(candidate.Year, productionYear))
            .Select(candidate => candidate with
            {
                TitleSimilarity = candidate.Titles.Count == 0
                    ? 0
                    : candidate.Titles.Max(candidateTitle => requestedTitles.Max(
                        requestedTitle => TitleSimilarity(requestedTitle, candidateTitle)))
            })
            .Where(candidate => candidate.TitleSimilarity >= MinimumTitleSimilarity)
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.OrderByDescending(candidate => candidate.TitleSimilarity).First())
            .OrderByDescending(candidate => candidate.TitleSimilarity)
            .ThenBy(candidate => productionYear.HasValue && candidate.Year.HasValue
                ? Math.Abs(candidate.Year.Value - productionYear.Value)
                : int.MaxValue)
            .ToList();

        if (ranked.Count == 0)
        {
            return null;
        }

        if (ranked.Count > 1
            && ranked[0].TitleSimilarity - ranked[1].TitleSimilarity < AmbiguityMargin)
        {
            return null;
        }

        return ranked[0];
    }

    private static bool TryParseCandidates(string json, out List<AniListCandidate> candidates)
    {
        candidates = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                return false;
            }

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("Page", out var page)
                || page.ValueKind != JsonValueKind.Object
                || !page.TryGetProperty("media", out var media)
                || media.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in media.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement)
                    || !idElement.TryGetInt32(out var id))
                {
                    continue;
                }

                var format = item.TryGetProperty("format", out var formatElement)
                    && formatElement.ValueKind == JsonValueKind.String
                    ? formatElement.GetString()
                    : null;
                int? year = item.TryGetProperty("seasonYear", out var seasonYear)
                    && seasonYear.TryGetInt32(out var parsedYear)
                    ? parsedYear
                    : null;
                if (!year.HasValue
                    && item.TryGetProperty("startDate", out var startDate)
                    && startDate.ValueKind == JsonValueKind.Object
                    && startDate.TryGetProperty("year", out var startYear)
                    && startYear.TryGetInt32(out parsedYear))
                {
                    year = parsedYear;
                }

                var titles = new List<string>();
                if (item.TryGetProperty("title", out var titleObject)
                    && titleObject.ValueKind == JsonValueKind.Object)
                {
                    AddStringProperty(titleObject, "romaji", titles);
                    AddStringProperty(titleObject, "english", titles);
                    AddStringProperty(titleObject, "native", titles);
                }

                if (item.TryGetProperty("synonyms", out var synonyms)
                    && synonyms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var synonym in synonyms.EnumerateArray())
                    {
                        if (synonym.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(synonym.GetString()))
                        {
                            titles.Add(synonym.GetString()!);
                        }
                    }
                }

                candidates.Add(new AniListCandidate(
                    id,
                    format,
                    year,
                    titles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    0));
            }

            return true;
        }
        catch (JsonException)
        {
            candidates = [];
            return false;
        }
    }

    private static void AddStringProperty(
        JsonElement element,
        string propertyName,
        ICollection<string> values)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            values.Add(property.GetString()!);
        }
    }

    private static bool IsCompatibleFormat(string? format, bool seriesRequest)
        => seriesRequest
            ? string.Equals(format, "TV", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "TV_SHORT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "ONA", StringComparison.OrdinalIgnoreCase)
            : string.Equals(format, "MOVIE", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatibleYear(int? candidateYear, int? requestedYear)
        => requestedYear.HasValue
            && candidateYear.HasValue
            && Math.Abs(candidateYear.Value - requestedYear.Value) <= 1;

    private static double TitleSimilarity(string left, string right)
    {
        var normalizedLeft = NormalizeTitle(left);
        var normalizedRight = NormalizeTitle(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftTokens = normalizedLeft.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = normalizedRight.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        var jaccard = union == 0 ? 0 : (double)intersection / union;

        if (Math.Min(normalizedLeft.Length, normalizedRight.Length) >= 8
            && (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
                || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)))
        {
            return Math.Max(jaccard, 0.86);
        }

        return jaccard;
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = AnimeClickSearchScorer.RemoveDiacritics(title).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\b(?:season|stagione)\s*\d+\b", " ");
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string GetStableIdentity(string normalizedId)
    {
        var slash = normalizedId.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? normalizedId[..slash] : normalizedId;
    }

    internal static string EscapeGraphQL(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string? ParseAniListIdFromSearch(string json)
    {
        // Compatibility parser retained for the existing dependency-free harness.
        const string idMarker = "\"id\":";
        const string dataMarker = "\"data\"";
        const string mediaMarker = "\"Media\"";
        var dataIdx = json.IndexOf(dataMarker, StringComparison.Ordinal);
        if (dataIdx < 0) return null;
        var mediaIdx = json.IndexOf(mediaMarker, dataIdx, StringComparison.Ordinal);
        if (mediaIdx < 0) return null;
        var idIdx = json.IndexOf(idMarker, mediaIdx, StringComparison.Ordinal);
        if (idIdx < 0) return null;
        var after = idIdx + idMarker.Length;
        while (after < json.Length && (json[after] == ' ' || json[after] == '\t')) after++;
        var start = after;
        while (after < json.Length && char.IsDigit(json[after])) after++;
        return after == start ? null : json.Substring(start, after - start);
    }

    private sealed record AniListQueryResult(
        IReadOnlyList<AniListCandidate> Candidates,
        bool Completed)
    {
        public static AniListQueryResult Incomplete { get; } = new([], false);
    }

    private sealed record AniListCandidate(
        int Id,
        string? Format,
        int? Year,
        IReadOnlyList<string> Titles,
        double TitleSimilarity);
}
