using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Minimal TheTVDB v4 client used to fetch episode overviews in an explicit
/// language. Production deliberately requests <c>ita</c> first and <c>eng</c>
/// only as an English fallback. TheTVDB exposes per-episode translations via
/// <c>GET /series/{id}/episodes/default/{lang}</c>, so when an Italian translation
/// exists we can fill the synopsis <b>without</b> Ollama (zero compute on the NAS). When TVDB has no translation for an episode, the caller falls back
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
    private const int ApiTimeoutSeconds = 30;
    private const long MaximumResponseBytes = 8 * 1024 * 1024;

    // Whole-operation budget for one episode-list fetch. Timeouts are per request, so the
    // pagination loop below could otherwise spend PageLimit x ApiTimeoutSeconds — nearly an
    // hour — inside a single GetEpisodeOverviewAsync while holding its single-flight gate.
    private const int EpisodeFetchBudgetSeconds = 180;
    private const int PageLimit = 100;

    // Prima di questo, TheTVDB veniva interrogato alla velocita' con cui la scansione produceva
    // richieste, e con il fix del parser della pagina episodi ora le sue risposte vengono
    // davvero usate, quindi il traffico reale sale.
    private static readonly RequestThrottle Throttle = new("TheTVDB", TimeSpan.FromMilliseconds(200));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickTvdbClient> _logger;

    // Bounded gate pools instead of a per-key dictionary, which added one SemaphoreSlim for
    // every distinct key ever seen and never removed or disposed any of them.
    //
    // Three separate pools on purpose, not one: the resolve and episode paths call LoginAsync
    // while already holding their own gate. With a single pool two unrelated keys can hash to
    // the same slot, and SemaphoreSlim is not reentrant, so that would deadlock — depending on
    // the hash of the configured API key, which is about as reproducible as it sounds.
    private readonly SemaphoreStripe _loginGates = new(8);
    private readonly SemaphoreStripe _resolveGates = new();
    private readonly SemaphoreStripe _episodeGates = new();

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

        var apiKeyHash = ApiKeyHash(configuration.TvdbApiKey);
        var tokenCacheKey = TokenCacheKey(configuration.TvdbApiKey);
        var cached = await _cache
            .GetAsync<string?>(tokenCacheKey, 24, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var gate = _loginGates.Get("login::" + apiKeyHash);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await _cache
                .GetAsync<string?>(tokenCacheKey, 24, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var client = BuildClient(configuration);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
            {
                Content = new StringContent(BuildLoginBody(configuration.TvdbApiKey), Encoding.UTF8, "application/json")
            };

            await Throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (RequestThrottle.IsRateLimited(response.StatusCode))
            {
                var pause = Throttle.NoticeRateLimit(response);
                _logger.LogWarning(
                    "TvdbClient: login ha risposto {Status}; pausa di {Pause} prima della prossima richiesta",
                    response.StatusCode,
                    pause);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // An unusable API key is a configuration problem the user must see: at Debug
                // it looked exactly like "AnimeClick has no synopsis for this episode".
                _logger.LogWarning(
                    "TvdbClient: login returned {Status}; check the TVDB API key in the plugin settings",
                    response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = ParseLoginToken(json);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            await _cache.SetAsync(tokenCacheKey, token, cancellationToken).ConfigureAwait(false);
            return token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvdbClient: login failed");
            return null;
        }
        finally
        {
            gate.Release();
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

        var missCacheKey = cacheKey + "::miss";
        var cached = await _cache
            .GetAsync<int?>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (cached is > 0)
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

        var gate = _resolveGates.Get("resolve::" + cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await _cache
                .GetAsync<int?>(cacheKey, configuration.CacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (cached is > 0)
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

            var token = await LoginAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var titles = new[] { originalTitle, fallbackTitle }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var allSearchesCompleted = titles.Length > 0;
            foreach (var title in titles)
            {
                var lookup = await SearchSeriesAsync(
                        title,
                        year,
                        token,
                        configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                allSearchesCompleted &= lookup.Completed;
                if (!lookup.Id.HasValue)
                {
                    continue;
                }

                await _cache.SetAsync(cacheKey, lookup.Id.Value, cancellationToken).ConfigureAwait(false);
                return lookup.Id.Value;
            }

            if (allSearchesCompleted)
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

    /// <summary>
    /// Compatibility wrapper for the direct Italian source.
    /// </summary>
    public Task<string?> GetEpisodeItalianOverviewAsync(
        int tvdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => GetEpisodeOverviewAsync(tvdbId, season, episode, "ita", configuration, cancellationToken);

    /// <summary>
    /// Fetches an episode overview in an explicit TVDB language. The language is
    /// part of the cache key, preventing an English fallback from being mistaken
    /// for native Italian metadata.
    /// </summary>
    public async Task<string?> GetEpisodeOverviewAsync(
        int tvdbId,
        int season,
        int episode,
        string language,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
        {
            return null;
        }

        var lang = SanitizeTvdbLanguage(language);
        var listCacheKey = $"tvdbEpisodes:v3::{tvdbId}::{lang}";
        var emptyCacheKey = listCacheKey + "::empty";
        var episodes = await _cache
            .GetAsync<List<TvdbEpisodeRecord>>(listCacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);

        if (episodes is null)
        {
            var emptyCached = await _cache
                .GetAsync<string>(emptyCacheKey, configuration.NegativeCacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (emptyCached == "empty")
            {
                return null;
            }

            var gate = _episodeGates.Get("episodes::" + listCacheKey);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                episodes = await _cache
                    .GetAsync<List<TvdbEpisodeRecord>>(
                        listCacheKey,
                        configuration.CacheHours,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (episodes is null)
                {
                    emptyCached = await _cache
                        .GetAsync<string>(
                            emptyCacheKey,
                            configuration.NegativeCacheHours,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (emptyCached == "empty")
                    {
                        return null;
                    }

                    var token = await LoginAsync(configuration, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        return null;
                    }

                    var fetched = await FetchAllEpisodesAsync(
                            tvdbId,
                            lang,
                            token,
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!fetched.Completed)
                    {
                        return null;
                    }

                    episodes = fetched.Episodes;
                    if (episodes.Count == 0)
                    {
                        await _cache.SetAsync(emptyCacheKey, "empty", cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    await _cache.SetAsync(listCacheKey, episodes, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return episodes.FirstOrDefault(record =>
            record.SeasonNumber == season && record.Number == episode)?.Overview;
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
        var result = new TvdbTestResult
        {
            EffectiveLanguage = SanitizeTvdbLanguage(language)
        };

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
                result.ErrorMessage = $"Login failed: HTTP {(int)loginResponse.StatusCode} {loginResponse.ReasonPhrase}";
                return result;
            }

            var token = ParseLoginToken(loginBody);
            result.TokenObtained = !string.IsNullOrWhiteSpace(token);

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
                result.ErrorMessage = $"Search failed: HTTP {(int)searchResponse.StatusCode} {searchResponse.ReasonPhrase}";
                return result;
            }

            result.SampleSeriesId = ParseFirstSeriesId(searchBody, null);
            if (!result.SampleSeriesId.HasValue)
            {
                result.ErrorMessage = "Login OK but search returned no series (unexpected).";
                return result;
            }

            // 3) Verify the translated-episodes endpoint used by the caller,
            // not just authentication/search.
            using var episodesRequest = new HttpRequestMessage(
                HttpMethod.Get,
                BuildEpisodesUrl(result.SampleSeriesId.Value, result.EffectiveLanguage, page: 0));
            episodesRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var episodesResponse = await client
                .SendAsync(episodesRequest, cancellationToken)
                .ConfigureAwait(false);
            result.StatusCode = (int)episodesResponse.StatusCode;

            if (!episodesResponse.IsSuccessStatusCode)
            {
                result.ErrorMessage =
                    $"Episodes ({result.EffectiveLanguage}) failed: HTTP {(int)episodesResponse.StatusCode} {episodesResponse.ReasonPhrase}";
                return result;
            }

            result.Success = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Connection test failed ({ex.GetType().Name}).";
            return result;
        }
    }

    private async Task<ExternalIdLookupResult> SearchSeriesAsync(
        string title,
        int? year,
        string token,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = BuildClient(configuration);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSearchUrl(title));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            await Throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ExternalIdLookupResult.ConfirmedMiss;
            }

            if (RequestThrottle.IsRateLimited(response.StatusCode))
            {
                var pause = Throttle.NoticeRateLimit(response);
                _logger.LogWarning(
                    "TvdbClient: ricerca ha risposto {Status}; pausa di {Pause} prima della prossima richiesta",
                    response.StatusCode,
                    pause);
                return ExternalIdLookupResult.Incomplete;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateToken(configuration);
                return ExternalIdLookupResult.Incomplete;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TryParseFirstSeriesId(json, year, out var id)
                ? new ExternalIdLookupResult(id, true)
                : ExternalIdLookupResult.Incomplete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvdbClient: search failed for \"{Title}\"", title);
            return ExternalIdLookupResult.Incomplete;
        }
    }

    private async Task<TvdbEpisodeFetchResult> FetchAllEpisodesAsync(
        int tvdbId, string lang, string token,
        PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var all = new List<TvdbEpisodeRecord>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(EpisodeFetchBudgetSeconds));
        var budgetToken = budget.Token;
        try
        {
            var client = BuildClient(configuration);
            for (var page = 0; page < PageLimit; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildEpisodesUrl(tvdbId, lang, page));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                await Throttle.WaitAsync(budgetToken).ConfigureAwait(false);
                using var response = await client.SendAsync(request, budgetToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new TvdbEpisodeFetchResult(all, true);
                }

                if (RequestThrottle.IsRateLimited(response.StatusCode))
                {
                    var pause = Throttle.NoticeRateLimit(response);
                    _logger.LogWarning(
                        "TvdbClient: lista episodi ha risposto {Status} per tvdb={Tvdb}; pausa di {Pause}",
                        response.StatusCode,
                        tvdbId,
                        pause);
                    return new TvdbEpisodeFetchResult(all, false);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateToken(configuration);
                    return new TvdbEpisodeFetchResult(all, false);
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(budgetToken).ConfigureAwait(false);
                if (!TryParseEpisodesFromPage(json, out var pageRecords))
                {
                    _logger.LogWarning(
                        "TvdbClient: invalid episode page for tvdb={Tvdb} lang={Lang} page={Page}; TVDB's response shape may have changed",
                        tvdbId,
                        lang,
                        page);
                    return new TvdbEpisodeFetchResult(all, false);
                }

                if (pageRecords.Count == 0)
                {
                    return new TvdbEpisodeFetchResult(all, true);
                }

                all.AddRange(pageRecords);
                if (string.IsNullOrWhiteSpace(ParseNextLink(json)))
                {
                    return new TvdbEpisodeFetchResult(all, true);
                }
            }

            // A next link after the safety limit means the list is partial and must
            // neither be used nor cached as a confirmed result.
            _logger.LogWarning(
                "TvdbClient: episode pagination exceeded the {Limit}-page safety limit for tvdb={Tvdb} lang={Lang}",
                PageLimit,
                tvdbId,
                lang);
            return new TvdbEpisodeFetchResult(all, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Either one request hit ApiTimeoutSeconds or the whole fetch hit its budget.
            // Partial pages are returned but never marked complete, so they are not cached.
            _logger.LogWarning(
                "TvdbClient: episode list for tvdb={Tvdb} lang={Lang} timed out after {Count} rows (per-request {Timeout}s, overall {Budget}s)",
                tvdbId,
                lang,
                all.Count,
                ApiTimeoutSeconds,
                EpisodeFetchBudgetSeconds);
            return new TvdbEpisodeFetchResult(all, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvdbClient: episode list fetch failed for tvdb={Tvdb} lang={Lang}", tvdbId, lang);
            return new TvdbEpisodeFetchResult(all, false);
        }
    }

    private sealed record TvdbEpisodeFetchResult(List<TvdbEpisodeRecord> Episodes, bool Completed);

    private static string ApiKeyHash(string apiKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16];

    private static string TokenCacheKey(string apiKey) => $"tvdbToken::{ApiKeyHash(apiKey)}";

    /// <summary>
    /// Discards the cached bearer token so the next call logs in again. Without this, a token
    /// TVDB has expired or revoked kept being replayed for the rest of its 24h cache window
    /// and every synopsis failed with nothing above Debug in the log to explain why.
    /// </summary>
    private void InvalidateToken(PluginConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey))
        {
            return;
        }

        _cache.ClearKey(TokenCacheKey(configuration.TvdbApiKey));
        _logger.LogWarning(
            "TvdbClient: TVDB rejected the cached token (401); discarded, the next request will log in again");
    }

    private sealed record ExternalIdLookupResult(int? Id, bool Completed)
    {
        public static ExternalIdLookupResult ConfirmedMiss { get; } = new(null, true);
        public static ExternalIdLookupResult Incomplete { get; } = new(null, false);
    }

    private HttpClient BuildClient(PluginConfiguration configuration)
    {
        var client = _httpClientFactory.CreateClient();

        // Deliberately not EpisodeTranslationTimeoutSec: that setting is the budget for one
        // Ollama translation call, and using it here meant a user who raised it for a slow
        // model also allowed a single TVDB request to hang for up to two minutes. TVDB is a
        // plain JSON API; 30 s matches that setting's own default, so behaviour is unchanged
        // for anyone who left it alone.
        client.Timeout = TimeSpan.FromSeconds(ApiTimeoutSeconds);
        client.MaxResponseContentBufferSize = MaximumResponseBytes;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            AnimeClickClient.GetEffectiveUserAgent(configuration));
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
        => $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}";

    /// <summary>Builds the TVDB /series/{id}/episodes/default/{lang} URL (testable, no network).</summary>
    internal static string BuildEpisodesUrl(int tvdbId, string lang, int page)
        => $"{BaseUrl}/series/{tvdbId}/episodes/default/{Uri.EscapeDataString(lang)}?page={page.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Sanitizes user-provided language to a single 3-char code (handles "ita, eng", spaces, etc).
    /// </summary>
    internal static string SanitizeTvdbLanguage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "ita";
        // take first token that looks like 3 letters
        var token = raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim().ToLowerInvariant())
                       .FirstOrDefault(t => t.Length == 3 && t.All(char.IsLetter));
        return token ?? "ita";
    }

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
        => TryParseFirstSeriesId(json, preferredYear, out var id) ? id : null;

    private static bool TryParseFirstSeriesId(string json, int? preferredYear, out int? id)
    {
        id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            int? fallback = null;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String
                    || !string.Equals(typeEl.GetString(), "series", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryReadIntField(item, "tvdb_id", out var parsedId)
                    && !TryReadIntField(item, "id", out parsedId))
                {
                    continue;
                }

                if (!preferredYear.HasValue)
                {
                    id = parsedId;
                    return true;
                }

                var itemYear = ExtractYear(item);
                if (itemYear.HasValue && itemYear.Value == preferredYear.Value)
                {
                    id = parsedId;
                    return true;
                }

                fallback ??= parsedId;
            }

            id = fallback;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadIntField(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
        {
            return true;
        }

        return el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parses the episodes from a single /series/{id}/episodes page (testable, no network).</summary>
    internal static List<TvdbEpisodeRecord> ParseEpisodesFromPage(string json)
        => TryParseEpisodesFromPage(json, out var records) ? records : [];

    private static bool TryParseEpisodesFromPage(string json, out List<TvdbEpisodeRecord> records)
    {
        records = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return false;
            }

            // TheTVDB v4 answers /series/{id}/episodes/default/{lang} with "data" as an *object*
            // describing the series, whose "episodes" property holds the array. This parser used
            // to require "data" itself to be an array, so every page was classified as invalid and
            // the entire TVDB synopsis path never produced anything — silently, because the
            // failure was logged at Debug until 0.4.4.0 raised it. Verified against the live API:
            // data is a dict with 21 keys, data.episodes is the list.
            // An array is still accepted, so a response in the older shape keeps working.
            JsonElement episodes;
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (!data.TryGetProperty("episodes", out episodes)
                    || episodes.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
            }
            else if (data.ValueKind == JsonValueKind.Array)
            {
                episodes = data;
            }
            else
            {
                return false;
            }

            foreach (var item in episodes.EnumerateArray())
            {
                var season = item.TryGetProperty("seasonNumber", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? sEl.GetInt32() : 0;
                var number = item.TryGetProperty("number", out var nEl) && nEl.ValueKind == JsonValueKind.Number
                    ? nEl.GetInt32() : 0;
                var overview = item.TryGetProperty("overview", out var oEl) && oEl.ValueKind == JsonValueKind.String
                    ? oEl.GetString() : null;

                records.Add(new TvdbEpisodeRecord { SeasonNumber = season, Number = number, Overview = overview });
            }

            return true;
        }
        catch (JsonException)
        {
            records = [];
            return false;
        }
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
            if (yEl.ValueKind == JsonValueKind.String
                && int.TryParse(yEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                return y;
            }

            if (yEl.ValueKind == JsonValueKind.Number && yEl.TryGetInt32(out var numericYear))
            {
                return numericYear;
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
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public bool TokenObtained { get; set; }
    public int? SampleSeriesId { get; set; }
    public string EffectiveLanguage { get; set; } = "ita";
}