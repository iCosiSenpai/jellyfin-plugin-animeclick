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

    public async Task<IEnumerable<RemoteSearchResult>> SearchAsync(string name, PluginConfiguration configuration, CancellationToken cancellationToken)
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

        var cacheKey = $"search::{cleanedQuery.ToLowerInvariant()}";
        var cached = await _cache.GetAsync<List<RemoteSearchResult>>(cacheKey, configuration.CacheHours, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        // Try original cleaned query first
        var results = await ExecuteSearchAsync(cleanedQuery, configuration, cancellationToken);

        // If no results, try progressively simpler queries
        if (results.Count == 0 && cleanedQuery != trimmed)
        {
            _logger.LogInformation("AnimeClick: No results for '{Clean}', retrying with original '{Original}'",
                cleanedQuery, trimmed);
            results = await ExecuteSearchAsync(trimmed, configuration, cancellationToken);
        }

        // If still no results, try removing colons, special chars
        if (results.Count == 0)
        {
            var simplified = SimplifyQuery(cleanedQuery);
            if (simplified != cleanedQuery)
            {
                _logger.LogInformation("AnimeClick: Retrying with simplified '{Simplified}'", simplified);
                results = await ExecuteSearchAsync(simplified, configuration, cancellationToken);
            }
        }

        // If still no results, try just the first 2-3 significant words
        if (results.Count == 0)
        {
            var shortQuery = GetShortQuery(cleanedQuery);
            if (shortQuery is not null)
            {
                _logger.LogInformation("AnimeClick: Retrying with short query '{Short}'", shortQuery);
                results = await ExecuteSearchAsync(shortQuery, configuration, cancellationToken);
            }
        }

        await _cache.SetAsync(cacheKey, results, cancellationToken);
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

    private async Task<List<RemoteSearchResult>> ExecuteSearchAsync(string query, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var slug = Uri.EscapeDataString(query);
        var url = $"{configuration.BaseUrl}/cerca?name={slug}";
        _logger.LogInformation("Ricerca AnimeClick: {Query} → {Url}", query, url);

        try
        {
            var html = await _client.GetStringAsync(url, configuration, cancellationToken);
            var searchResults = _parser.ParseSearchResults(html, configuration.BaseUrl);

            var maxResults = configuration.MaxSearchResults > 0 ? configuration.MaxSearchResults : 10;

            return searchResults
                .Take(maxResults)
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
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnimeClick: Search failed for '{Query}'", query);
            return [];
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
}
