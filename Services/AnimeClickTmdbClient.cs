using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Minimal TMDB v3 client used to fetch English episode overviews for the
/// optional EN→IT synopsis translation feature. AnimeClick publishes no
/// per-episode synopsis and AniList has no Episode.description, so TMDB is the
/// source of the English text that <see cref="AnimeClickOllamaTranslator"/>
/// then translates to Italian.
///
/// All methods are best-effort: they return null on any failure (network,
/// non-2xx, parse error, 404) so the metadata pipeline is never crashed.
/// Results are cached via <see cref="AnimeClickCacheService"/>.
/// </summary>
public class AnimeClickTmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickTmdbClient> _logger;

    public AnimeClickTmdbClient(
        IHttpClientFactory httpClientFactory,
        AnimeClickCacheService cache,
        ILogger<AnimeClickTmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a TMDB tv id for a title. Tries the original (romaji) title first
    /// (TMDB indexes anime well by romaji), then the provided fallback title.
    /// Returns null if no match.
    /// </summary>
    public async Task<int?> ResolveTmdbTvIdAsync(
        string? originalTitle,
        string? fallbackTitle,
        int? year,
        PluginConfiguration configuration,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TmdbApiKey))
        {
            return null;
        }

        var cached = await _cache.GetAsync<int?>(cacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
        if (cached.HasValue)
        {
            return cached.Value > 0 ? cached : null;
        }

        int? resolved = null;
        if (!string.IsNullOrWhiteSpace(originalTitle))
        {
            resolved = await SearchTvAsync(originalTitle, year, configuration, cancellationToken).ConfigureAwait(false);
        }

        if (!resolved.HasValue && !string.IsNullOrWhiteSpace(fallbackTitle)
            && !string.Equals(originalTitle, fallbackTitle, StringComparison.OrdinalIgnoreCase))
        {
            resolved = await SearchTvAsync(fallbackTitle, year, configuration, cancellationToken).ConfigureAwait(false);
        }

        // Cache both hits and misses (0 = miss) so we don't re-search every scan.
        await _cache.SetAsync(cacheKey, resolved ?? 0, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Fetches the English overview of a single episode. Returns null on 404 / error.
    /// </summary>
    public async Task<string?> GetEpisodeOverviewAsync(
        int tmdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TmdbApiKey))
        {
            return null;
        }

        var cacheKey = $"tmdbEp::{tmdbId}::{season}::{episode}";
        var cached = await _cache.GetAsync<string?>(cacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var url = BuildEpisodeUrl(configuration.TmdbApiKey, tmdbId, season, episode);

            var overview = await GetJsonAsync(url, configuration, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(overview))
            {
                return null;
            }

            await _cache.SetAsync(cacheKey, overview, cancellationToken).ConfigureAwait(false);
            return overview;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TmdbClient: episode overview fetch failed for tmdb={Tmdb} S{S}E{E}: {Message}",
                tmdbId, season, episode, ex.Message);
            return null;
        }
    }

    private async Task<int?> SearchTvAsync(string title, int? year, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildSearchTvUrl(configuration.TmdbApiKey, title, year);
            var json = await GetJsonStringAsync(url, configuration, cancellationToken).ConfigureAwait(false);
            return ParseFirstTvId(json, year);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TmdbClient: search/tv failed for \"{Title}\": {Message}", title, ex.Message);
            return null;
        }
    }

    private async Task<string?> GetJsonAsync(string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var json = await GetJsonStringAsync(url, configuration, cancellationToken).ConfigureAwait(false);
        return ParseEpisodeOverview(json);
    }

    private async Task<string> GetJsonStringAsync(string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(configuration.UserAgent);

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 = TMDB doesn't have this episode; treat as "no overview" without throwing.
            return "{}";
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the TMDB search/tv URL (testable, no network).</summary>
    internal static string BuildSearchTvUrl(string apiKey, string title, int? year)
        => $"{BaseUrl}/search/tv?api_key={Uri.EscapeDataString(apiKey)}"
           + $"&query={Uri.EscapeDataString(title)}"
           + "&language=en&include_adult=false"
           + (year.HasValue ? $"&first_air_date_year={year.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    /// <summary>Builds the TMDB tv/season/episode URL (testable, no network).</summary>
    internal static string BuildEpisodeUrl(string apiKey, int tmdbId, int season, int episode)
        => $"{BaseUrl}/tv/{tmdbId}/season/{season}/episode/{episode}"
           + $"?api_key={Uri.EscapeDataString(apiKey)}&language=en-US";

    /// <summary>Parses the first TMDB tv id from a search/tv response (testable, no network).</summary>
    internal static int? ParseFirstTvId(string json, int? preferredYear)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                return null;
            }

            int? fallback = null;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var id = idEl.GetInt32();
                if (!preferredYear.HasValue)
                {
                    return id;
                }

                if (item.TryGetProperty("first_air_date", out var dateEl)
                    && dateEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var airDate)
                    && airDate.Year == preferredYear.Value)
                {
                    return id;
                }

                fallback ??= id;
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses the name of the first result in a search/tv response (testable, no network).</summary>
    internal static string? ParseFirstTvName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            return first.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Diagnostics-only: validates the TMDB API key by running a search/tv with a known
    /// query. Does NOT silent-catch — returns a detailed DTO so the UI can show the real
    /// error (status code, response body, exception message).
    /// </summary>
    public async Task<TmdbTestResult> TestConnectionAsync(string apiKey, CancellationToken cancellationToken)
    {
        var result = new TmdbTestResult { Endpoint = BaseUrl };

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.ErrorMessage = "TMDB API key is empty.";
            return result;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Jellyfin-Plugin/diagnostics");

            var url = BuildSearchTvUrl(apiKey, "Boku no Kokoro", 2023);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            result.StatusCode = (int)response.StatusCode;
            result.ResponseBody = body;

            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"Search failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return result;
            }

            result.SampleId = ParseFirstTvId(body, 2023);
            result.SampleName = ParseFirstTvName(body);
            result.Success = result.SampleId.HasValue;

            if (!result.Success)
            {
                result.ErrorMessage = "Search returned no results (unexpected for a known query).";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>Parses the overview field from a tv/season/episode response (testable, no network).</summary>
    internal static string? ParseEpisodeOverview(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("overview", out var overviewEl)
                   && overviewEl.ValueKind == JsonValueKind.String
                ? overviewEl.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Detailed result of a TMDB connection test (used by the diagnostics UI).</summary>
public sealed class TmdbTestResult
{
    public bool Success { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? ErrorMessage { get; set; }
    public int? SampleId { get; set; }
    public string? SampleName { get; set; }
}