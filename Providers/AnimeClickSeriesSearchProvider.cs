using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

public class AnimeClickSeriesSearchProvider
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickSeriesSearchProvider> _logger;

    public AnimeClickSeriesSearchProvider(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickSeriesSearchProvider> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _logger = logger;
    }

    public async Task<IEnumerable<RemoteSearchResult>> SearchAsync(
        string name,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        int? productionYear = null,
        bool seriesRequest = true)
    {
        var trimmed = name.Trim();

        // ── Direct ID lookup ──
        // If the query looks like an AnimeClick ID (e.g. "72", "72/naruto"),
        // skip text search and fetch the anime page directly.
        if (Regex.IsMatch(trimmed, @"^\d+(/\S+)?$"))
        {
            return await DirectLookupAsync(trimmed, configuration, cancellationToken);
        }

        // ── Text search ──
        var cleanedQuery = CleanSearchQuery(trimmed);

        var cacheKey = $"search:v2::{cleanedQuery.ToLowerInvariant()}::{productionYear?.ToString() ?? "any"}::{(seriesRequest ? "series" : "any")}";
        var negativeCacheKey = $"search-empty:v1::{cleanedQuery.ToLowerInvariant()}::{productionYear?.ToString() ?? "any"}::{(seriesRequest ? "series" : "any")}";
        var cached = await _cache.GetAsync<List<RemoteSearchResult>>(cacheKey, configuration.CacheHours, cancellationToken);
        if (cached is not null)
        {
            _logger.LogDebug("AnimeClick search cache hit: {Key}", cacheKey);
            return cached;
        }

        var negativeCached = await _cache.GetAsync<string>(negativeCacheKey, configuration.NegativeCacheHours, cancellationToken);
        if (negativeCached == "empty")
        {
            _logger.LogDebug("AnimeClick negative search cache hit: {Key}", negativeCacheKey);
            return [];
        }

        var attemptsHadErrors = false;

        // Try original cleaned query first
        var attempt = await ExecuteSearchAsync(cleanedQuery, configuration, cancellationToken, productionYear, seriesRequest);
        attemptsHadErrors |= attempt.HadError;
        var results = attempt.Results;

        // If no results, try progressively simpler queries
        if (results.Count == 0 && cleanedQuery != trimmed)
        {
            _logger.LogInformation("AnimeClick: No results for '{Clean}', retrying with original '{Original}'",
                cleanedQuery, trimmed);
            attempt = await ExecuteSearchAsync(trimmed, configuration, cancellationToken, productionYear, seriesRequest);
            attemptsHadErrors |= attempt.HadError;
            results = attempt.Results;
        }

        // If still no results, try removing colons, special chars
        if (results.Count == 0)
        {
            var simplified = SimplifyQuery(cleanedQuery);
            if (simplified != cleanedQuery)
            {
                _logger.LogInformation("AnimeClick: Retrying with simplified '{Simplified}'", simplified);
                attempt = await ExecuteSearchAsync(simplified, configuration, cancellationToken, productionYear, seriesRequest);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
            }
        }

        // If still no results, try just the first 2-3 significant words
        if (results.Count == 0)
        {
            var shortQuery = GetShortQuery(cleanedQuery);
            if (shortQuery is not null)
            {
                _logger.LogInformation("AnimeClick: Retrying with short query '{Short}'", shortQuery);
                attempt = await ExecuteSearchAsync(shortQuery, configuration, cancellationToken, productionYear, seriesRequest);
                attemptsHadErrors |= attempt.HadError;
                results = attempt.Results;
            }
        }

        if (results.Count > 0)
        {
            await _cache.SetAsync(cacheKey, results, cancellationToken);
        }
        else if (!attemptsHadErrors)
        {
            await _cache.SetAsync(negativeCacheKey, "empty", cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Direct lookup by AnimeClick ID — fetches the page and returns it as a search result.
    /// </summary>
    private async Task<List<RemoteSearchResult>> DirectLookupAsync(string idOrSlug, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var url = AnimeClickClient.BuildAnimeUrl(configuration.BaseUrl, idOrSlug);
            _logger.LogInformation("AnimeClick: Direct ID lookup → {Url}", url);

            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var anime = _parser.ParseAnimePage(url, html);

            // Cache the full anime data since we already have it
            var cacheKey = $"anime::{url}";
            await _cache.SetAsync(cacheKey, anime, cancellationToken);

            return
            [
                new RemoteSearchResult
                {
                    Name = anime.Title,
                    ProductionYear = anime.ProductionYear,
                    SearchProviderName = "AnimeClick",
                    ImageUrl = anime.ImageUrl,
                    Overview = anime.Overview,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["AnimeClick"] = anime.Id
                    }
                }
            ];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Direct lookup failed for ID '{Id}'", idOrSlug);
            return [];
        }
    }

    private async Task<SearchAttempt> ExecuteSearchAsync(
        string query,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        int? productionYear,
        bool seriesRequest)
    {
        var slug = Uri.EscapeDataString(query);
        var url = $"{configuration.BaseUrl}/cerca?name={slug}";
        _logger.LogInformation("Ricerca AnimeClick: {Query} → {Url}", query, url);

        try
        {
            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var searchResults = _parser.ParseSearchResults(html, configuration.BaseUrl);
            _logger.LogInformation("AnimeClick: Parsed {Count} search candidates for '{Query}'", searchResults.Count, query);

            var maxResults = configuration.MaxSearchResults > 0 ? configuration.MaxSearchResults : 10;

            var ranked = searchResults
                .Select(r => new { Result = r, Score = AnimeClickSearchScorer.Score(r, query, productionYear, seriesRequest) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Result.ProductionYear ?? 9999)
                .ThenBy(x => x.Result.Title)
                .ToList();

            foreach (var candidate in ranked.Take(Math.Min(5, ranked.Count)))
            {
                _logger.LogDebug(
                    "AnimeClick search candidate score={Score} title={Title} year={Year} format={Format} id={Id}",
                    candidate.Score,
                    candidate.Result.Title,
                    candidate.Result.ProductionYear,
                    candidate.Result.Format,
                    candidate.Result.Id);
            }

            return SearchAttempt.Success(ranked
                .Take(maxResults)
                .Select(x => x.Result)
                .Select(r => new RemoteSearchResult
                {
                    Name = r.Title,
                    ProductionYear = r.ProductionYear,
                    SearchProviderName = "AnimeClick",
                    ImageUrl = r.ThumbnailUrl,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["AnimeClick"] = r.Id
                    }
                })
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Search failed for '{Query}'", query);
            return SearchAttempt.Error();
        }
    }

    // ── Query cleaning helpers ──

    /// <summary>
    /// Removes media type suffixes that Jellyfin or file naming conventions add
    /// but AnimeClick doesn't understand (TV, Movie, OVA, etc.).
    /// Also removes year suffixes like "(2024)".
    /// </summary>
    private static string CleanSearchQuery(string query)
    {
        // Remove common suffixes in parentheses: (TV), (Movie), (2024), (Serie TV), (OVA)
        var cleaned = Regex.Replace(query, @"\s*\((?:TV|Movie|Film|Serie\s*TV|OVA|OAV|Special|ONA|\d{4})\)\s*", " ",
            RegexOptions.IgnoreCase);

        // Remove standalone media type words at end
        cleaned = Regex.Replace(cleaned, @"\s+(?:TV|Movie|Film|the Animation|Season \d+)\s*$", "",
            RegexOptions.IgnoreCase);

        // Remove season indicators: S01, Season 1, etc.
        cleaned = Regex.Replace(cleaned, @"\s+(?:S\d+|Season\s*\d+|Stagione\s*\d+)\s*$", "",
            RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    /// <summary>
    /// Simplifies by removing special characters (colons, dashes, dots) that
    /// might cause AnimeClick search to fail.
    /// </summary>
    private static string SimplifyQuery(string query)
    {
        // Replace colons, dashes, dots with spaces
        var simplified = Regex.Replace(query, @"[:.\-–—]", " ");
        // Collapse multiple spaces
        simplified = Regex.Replace(simplified, @"\s{2,}", " ");
        return simplified.Trim();
    }

    /// <summary>
    /// Extracts the first 2-3 meaningful words for a "fuzzy" search fallback.
    /// </summary>
    private static string? GetShortQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1) // skip single-char words
            .Take(3)
            .ToArray();

        if (words.Length < 2) return null;
        var shortQuery = string.Join(' ', words);
        return shortQuery == query ? null : shortQuery;
    }

    private sealed class SearchAttempt
    {
        private SearchAttempt(List<RemoteSearchResult> results, bool hadError)
        {
            Results = results;
            HadError = hadError;
        }

        public List<RemoteSearchResult> Results { get; }

        public bool HadError { get; }

        public static SearchAttempt Success(List<RemoteSearchResult> results) => new(results, false);

        public static SearchAttempt Error() => new([], true);
    }
}
