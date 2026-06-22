using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Minimal TheTVDB v4 client used to fetch episode overviews directly in Italian
/// (or another configured language). TheTVDB exposes per-episode translations via
/// <c>GET /series/{id}/episodes/default/{lang}</c>, so when a translation exists we
/// can fill the Italian synopsis <b>without</b> any Ollama translation (zero compute
/// on the NAS). When TVDB has no translation for an episode, the caller falls back
/// to the TMDB EN + Ollama IT pipeline.
///
/// All metadata-path methods are best-effort: they return null on any failure
/// (network, non-2xx, parse error, 404) so the metadata pipeline is never crashed.
/// Results are cached via <see cref="AnimeClickCacheService"/>. The
/// <see cref="TestConnectionAsync"/> method is the exception: it does NOT silent-catch,
/// so the diagnostics UI can surface a detailed error.
/// </summary>
public class AnimeClickTvdbClient
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private const string TestQuery = "Naruto";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickTvdbClient> _logger;

    public AnimeClickTvdbClient(
        IHttpClientFactory httpClientFactory,
        AnimeClickCacheService cache,
        ILogger<AnimeClickTvdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Logs in to TheTVDB v4 and returns a bearer token. Cached for 24h under
    /// <c>tvdbToken::</c>. Returns null on any failure.
    /// </summary>
    public async Task<string?> LoginAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
        {
            return null;
        }

        const string tokenCacheKey = "tvdbToken::";
        var cached = await _cache.GetAsync<string?>(tokenCacheKey, 24, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        try
        {
            var client = BuildClient(configuration);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
            {
                Content = new StringContent(BuildLoginBody(configuration.TvdbApiKey), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TvdbClient: login returned {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = ParseLoginToken(json);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            await _cache.SetAsync(tokenCacheKey, token!, cancellationToken).ConfigureAwait(false);
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TvdbClient: login failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Resolves a TheTVDB series id for a title. Tries the original (romaji) title first,
    /// then the fallback (Italian) title. Prefers a result whose air year matches.
    /// Returns null if no match. Cached under <paramref name="cacheKey"/> (0 = miss cached).
    /// </summary>
    public async Task<int?> ResolveTvdbSeriesIdAsync(
        string? originalTitle,
        string? fallbackTitle,
        int? year,
        PluginConfiguration configuration,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
        {
            return null;
        }

        var cached = await _cache.GetAsync<int?>(cacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
        if (cached.HasValue)
        {
            return cached.Value > 0 ? cached : null;
        }

        var token = await LoginAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        int? resolved = null;
        if (!string.IsNullOrWhiteSpace(originalTitle))
        {
            resolved = await SearchSeriesAsync(originalTitle, year, token!, configuration, cancellationToken).ConfigureAwait(false);
        }

        if (!resolved.HasValue
            && !string.IsNullOrWhiteSpace(fallbackTitle)
            && !string.Equals(originalTitle, fallbackTitle, StringComparison.OrdinalIgnoreCase))
        {
            resolved = await SearchSeriesAsync(fallbackTitle, year, token!, configuration, cancellationToken).ConfigureAwait(false);
        }

        await _cache.SetAsync(cacheKey, resolved ?? 0, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Fetches the configured-language overview of a single episode. Pulls the whole
    /// paginated episode list for the series (cached) and matches by season/episode
    /// number. Returns null when no match or the overview is empty (caller falls back).
    /// </summary>
    public async Task<string?> GetEpisodeItalianOverviewAsync(
        int tvdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
        {
            return null;
        }

        var lang = string.IsNullOrWhiteSpace(configuration.TvdbLanguage) ? "ita" : configuration.TvdbLanguage!;
        var listCacheKey = $"tvdbEpisodes::{tvdbId}::{lang}";

        var episodes = await _cache.GetAsync<List<TvdbEpisodeRecord>>(listCacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);

        if (episodes is null || episodes.Count == 0)
        {
            var token = await LoginAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            episodes = await FetchAllEpisodesAsync(tvdbId, lang, token!, configuration, cancellationToken).ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                return null;
            }

            await _cache.SetAsync(listCacheKey, episodes, cancellationToken).ConfigureAwait(false);
        }

        return episodes.FirstOrDefault(r => r.SeasonNumber == season && r.Number == episode)?.Overview;
    }

    /// <summary>
    /// Diagnostics-only: validates the TVDB API key by logging in and running a search.
    /// Does NOT silent-catch — returns a detailed DTO so the UI can show the real error.
    /// </summary>
    public async Task<TvdbTestResult> TestConnectionAsync(
        string apiKey,
        string language,
        CancellationToken cancellationToken)
    {
        var result = new TvdbTestResult { Endpoint = BaseUrl };

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.ErrorMessage = "TVDB API key is empty.";
            return result;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // 1) Login
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
            {
                Content = new StringContent(BuildLoginBody(apiKey), Encoding.UTF8, "application/json")
            };
            using var loginResponse = await client.SendAsync(loginRequest, cancellationToken).ConfigureAwait(false);
            var loginBody = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            result.StatusCode = (int)loginResponse.StatusCode;

            if (!loginResponse.IsSuccessStatusCode)
            {
                result.ResponseBody = loginBody;
                result.ErrorMessage = $"Login failed: HTTP {(int)loginResponse.StatusCode} {loginResponse.ReasonPhrase}";
                return result;
            }

            var token = ParseLoginToken(loginBody);
            result.TokenObtained = !string.IsNullOrWhiteSpace(token);
            result.ResponseBody = loginBody;

            if (!result.TokenObtained)
            {
                result.ErrorMessage = "Login succeeded but no token was returned in the response.";
                return result;
            }

            // 2) Search a known series to confirm the token works end-to-end
            var searchUrl = BuildSearchUrl(TestQuery);
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var searchResponse = await client.SendAsync(searchRequest, cancellationToken).ConfigureAwait(false);
            var searchBody = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            result.StatusCode = (int)searchResponse.StatusCode;

            if (!searchResponse.IsSuccessStatusCode)
            {
                result.ResponseBody = searchBody;
                result.ErrorMessage = $"Search failed: HTTP {(int)searchResponse.StatusCode} {searchResponse.ReasonPhrase}";
                return result;
            }

            result.ResponseBody = searchBody;
            result.SampleSeriesId = ParseFirstSeriesId(searchBody, null);
            result.Success = result.SampleSeriesId.HasValue;
            if (!result.Success)
            {
                result.ErrorMessage = "Login OK but search returned no series (unexpected).";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private async Task<int?> SearchSeriesAsync(
        string title, int? year, string token,
        PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var client = BuildClient(configuration);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSearchUrl(title));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseFirstSeriesId(json, year);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TvdbClient: search failed for \"{Title}\": {Message}", title, ex.Message);
            return null;
        }
    }

    private async Task<List<TvdbEpisodeRecord>> FetchAllEpisodesAsync(
        int tvdbId, string lang, string token,
        PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var all = new List<TvdbEpisodeRecord>();
        try
        {
            var client = BuildClient(configuration);
            for (var page = 0; page < 100; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildEpisodesUrl(tvdbId, lang, page));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    break;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                var pageRecords = ParseEpisodesFromPage(json);
                if (pageRecords.Count == 0)
                {
                    break;
                }

                all.AddRange(pageRecords);

                var nextLink = ParseNextLink(json);
                if (string.IsNullOrWhiteSpace(nextLink))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TvdbClient: episode list fetch failed for tvdb={Tvdb} lang={Lang}: {Message}",
                tvdbId, lang, ex.Message);
        }

        return all;
    }

    private HttpClient BuildClient(PluginConfiguration configuration)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, configuration.EpisodeTranslationTimeoutSec));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(configuration.UserAgent);
        return client;
    }

    /// <summary>Builds the TVDB /login JSON body (testable, no network).</summary>
    internal static string BuildLoginBody(string apiKey)
    {
        var payload = new { apikey = apiKey };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>Builds the TVDB /search series URL (testable, no network).</summary>
    internal static string BuildSearchUrl(string query)
        => $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&type=series";

    /// <summary>Builds the TVDB /series/{id}/episodes/default/{lang} URL (testable, no network).</summary>
    internal static string BuildEpisodesUrl(int tvdbId, string lang, int page)
        => $"{BaseUrl}/series/{tvdbId}/episodes/default/{Uri.EscapeDataString(lang)}?page={page.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Parses the bearer token from a /login response (testable, no network).</summary>
    internal static string? ParseLoginToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("token", out var tokenEl)
                && tokenEl.ValueKind == JsonValueKind.String)
            {
                return tokenEl.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses the first TVDB series id from a /search response, preferring a year match (testable).</summary>
    internal static int? ParseFirstSeriesId(string json, int? preferredYear)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int? fallback = null;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("tvdb_id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var id = idEl.GetInt32();
                if (!preferredYear.HasValue)
                {
                    return id;
                }

                var itemYear = ExtractYear(item);
                if (itemYear.HasValue && itemYear.Value == preferredYear.Value)
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

    /// <summary>Parses the episodes from a single /series/{id}/episodes page (testable, no network).</summary>
    internal static List<TvdbEpisodeRecord> ParseEpisodesFromPage(string json)
    {
        var list = new List<TvdbEpisodeRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in data.EnumerateArray())
            {
                var season = item.TryGetProperty("seasonNumber", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? sEl.GetInt32() : 0;
                var number = item.TryGetProperty("number", out var nEl) && nEl.ValueKind == JsonValueKind.Number
                    ? nEl.GetInt32() : 0;
                var overview = item.TryGetProperty("overview", out var oEl) && oEl.ValueKind == JsonValueKind.String
                    ? oEl.GetString() : null;

                list.Add(new TvdbEpisodeRecord { SeasonNumber = season, Number = number, Overview = overview });
            }
        }
        catch
        {
            // best effort
        }

        return list;
    }

    /// <summary>Extracts the overview of a specific episode from a single page JSON (testable, no network).</summary>
    internal static string? ParseEpisodeOverview(string json, int season, int episode)
        => ParseEpisodesFromPage(json)
            .FirstOrDefault(r => r.SeasonNumber == season && r.Number == episode)?.Overview;

    /// <summary>Parses the <c>links.next</c> value from a paginated response (testable, no network).</summary>
    internal static string? ParseNextLink(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("links", out var links)
                && links.TryGetProperty("next", out var nextEl)
                && nextEl.ValueKind == JsonValueKind.String)
            {
                return nextEl.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ExtractYear(JsonElement item)
    {
        // TheTVDB search results expose either "first_air_time" (ISO date) or "year" (string/number).
        if (item.TryGetProperty("first_air_time", out var fat) && fat.ValueKind == JsonValueKind.String
            && DateTime.TryParse(fat.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
        {
            return d.Year;
        }

        if (item.TryGetProperty("year", out var yEl))
        {
            if (yEl.ValueKind == JsonValueKind.String && int.TryParse(yEl.GetString(), out var y))
            {
                return y;
            }

            if (yEl.ValueKind == JsonValueKind.Number)
            {
                return yEl.GetInt32();
            }
        }

        return null;
    }
}

/// <summary>A single episode record parsed from the TVDB episodes list (season, number, overview).</summary>
internal sealed class TvdbEpisodeRecord
{
    public int SeasonNumber { get; set; }
    public int Number { get; set; }
    public string? Overview { get; set; }
}

/// <summary>Detailed result of a TVDB connection test (used by the diagnostics UI).</summary>
public sealed class TvdbTestResult
{
    public bool Success { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? ErrorMessage { get; set; }
    public bool TokenObtained { get; set; }
    public int? SampleSeriesId { get; set; }
}