using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
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
    private const int ApiTimeoutSeconds = 15;
    private const int DiagnosticsTimeoutSeconds = 30;

    // TMDB returns small JSON documents. Without a cap the plugin would buffer whatever the
    // endpoint sends into memory, which on a NAS is the difference between a failed lookup
    // and a dead Jellyfin process.
    private const long MaximumResponseBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickTmdbClient> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _singleFlight =
        new(StringComparer.Ordinal);

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

        var gate = GetSingleFlight("resolve::" + cacheKey);
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

            var titles = new[] { originalTitle, fallbackTitle }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var allSearchesCompleted = titles.Length > 0;
            foreach (var title in titles)
            {
                var lookup = await SearchTvAsync(title, year, configuration, cancellationToken)
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
    /// Fetches an episode overview in the requested TMDB language. Empty results
    /// are negatively cached so untranslated episodes do not generate repeated calls.
    /// </summary>
    public Task<string?> GetEpisodeOverviewAsync(
        int tmdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => GetEpisodeOverviewAsync(
            tmdbId,
            season,
            episode,
            "en-US",
            configuration,
            cancellationToken);

    public async Task<string?> GetEpisodeOverviewAsync(
        int tmdbId,
        int season,
        int episode,
        string language,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.TmdbApiKey))
        {
            return null;
        }

        var normalizedLanguage = NormalizeLanguage(language);
        var cacheKey = $"tmdbEpisodeTranslations:v2::{tmdbId}::{season}::{episode}";
        var emptyCacheKey = cacheKey + "::empty";
        var translationsJson = await _cache
            .GetAsync<string?>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (translationsJson is null)
        {
            var emptyCached = await _cache
                .GetAsync<string>(emptyCacheKey, configuration.NegativeCacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(emptyCached, "empty", StringComparison.Ordinal))
            {
                return null;
            }
        }

        try
        {
            if (translationsJson is null)
            {
                var gate = GetSingleFlight("episode::" + cacheKey);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    translationsJson = await _cache
                        .GetAsync<string?>(cacheKey, configuration.CacheHours, cancellationToken)
                        .ConfigureAwait(false);
                    if (translationsJson is null)
                    {
                        var emptyCached = await _cache
                            .GetAsync<string>(
                                emptyCacheKey,
                                configuration.NegativeCacheHours,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (string.Equals(emptyCached, "empty", StringComparison.Ordinal))
                        {
                            return null;
                        }

                        var fetched = await FetchEpisodeTranslationsAsync(
                                tmdbId,
                                season,
                                episode,
                                configuration,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!fetched.Completed)
                        {
                            return null;
                        }

                        if (!fetched.HasTranslations || string.IsNullOrWhiteSpace(fetched.Json))
                        {
                            await _cache.SetAsync(emptyCacheKey, "empty", cancellationToken).ConfigureAwait(false);
                            return null;
                        }

                        translationsJson = fetched.Json;
                        await _cache.SetAsync(cacheKey, translationsJson, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            // The translations endpoint proves the language of each overview. The
            // localized episode endpoint may silently fall back to original text.
            return ParseEpisodeTranslation(translationsJson, normalizedLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TmdbClient: explicit episode translation fetch failed for tmdb={Tmdb} S{Season}E{Episode} lang={Language}",
                tmdbId,
                season,
                episode,
                normalizedLanguage);
            return null;
        }
    }

    private async Task<ExternalIdLookupResult> SearchTvAsync(
        string title,
        int? year,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = BuildClient(configuration);

            using var response = await client
                .GetAsync(BuildSearchTvUrl(configuration.TmdbApiKey, title, year), cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ExternalIdLookupResult.ConfirmedMiss;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "TmdbClient: TMDB rejected the API key (401); check it in the plugin settings");
                return ExternalIdLookupResult.Incomplete;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TryParseFirstTvId(json, year, out var id)
                ? new ExternalIdLookupResult(id, true)
                : ExternalIdLookupResult.Incomplete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TmdbClient: search/tv failed for \"{Title}\"", title);
            return ExternalIdLookupResult.Incomplete;
        }
    }

    private async Task<EpisodeTranslationsFetchResult> FetchEpisodeTranslationsAsync(
        int tmdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = BuildClient(configuration);

            using var response = await client
                .GetAsync(
                    BuildEpisodeTranslationsUrl(
                        configuration.TmdbApiKey,
                        tmdbId,
                        season,
                        episode),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return EpisodeTranslationsFetchResult.ConfirmedEmpty;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "TmdbClient: TMDB rejected the API key (401); check it in the plugin settings");
                return EpisodeTranslationsFetchResult.Incomplete;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!TryValidateEpisodeTranslationsPayload(json, out var hasTranslations))
            {
                return EpisodeTranslationsFetchResult.Incomplete;
            }

            return hasTranslations
                ? new EpisodeTranslationsFetchResult(json, true, true)
                : EpisodeTranslationsFetchResult.ConfirmedEmpty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TmdbClient: episode translations request failed for tmdb={Tmdb} S{Season}E{Episode}",
                tmdbId,
                season,
                episode);
            return EpisodeTranslationsFetchResult.Incomplete;
        }
    }

    private HttpClient BuildClient(PluginConfiguration configuration)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(ApiTimeoutSeconds);
        client.MaxResponseContentBufferSize = MaximumResponseBytes;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            AnimeClickClient.GetEffectiveUserAgent(configuration));
        return client;
    }

    private static bool TryValidateEpisodeTranslationsPayload(
        string json,
        out bool hasTranslations)
    {
        hasTranslations = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            if (translations.GetArrayLength() == 0)
            {
                return true;
            }

            var sawStructurallyValidRecord = false;
            foreach (var translation in translations.EnumerateArray())
            {
                if (translation.ValueKind != JsonValueKind.Object
                    || !translation.TryGetProperty("iso_639_1", out var language)
                    || language.ValueKind != JsonValueKind.String
                    || !translation.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                sawStructurallyValidRecord = true;
                if (data.TryGetProperty("overview", out var overview)
                    && overview.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(overview.GetString()))
                {
                    hasTranslations = true;
                }
            }

            return sawStructurallyValidRecord;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Builds the TMDB search/tv URL (testable, no network).</summary>
    internal static string BuildSearchTvUrl(string apiKey, string title, int? year)
        => $"{BaseUrl}/search/tv?api_key={Uri.EscapeDataString(apiKey)}"
           + $"&query={Uri.EscapeDataString(title)}"
           + "&language=en&include_adult=false"
           + (year.HasValue ? $"&first_air_date_year={year.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    /// <summary>Builds the TMDB tv/season/episode URL (testable, no network).</summary>
    internal static string BuildEpisodeUrl(string apiKey, int tmdbId, int season, int episode)
        => BuildEpisodeUrl(apiKey, tmdbId, season, episode, "en-US");

    internal static string BuildEpisodeUrl(
        string apiKey,
        int tmdbId,
        int season,
        int episode,
        string language)
        => $"{BaseUrl}/tv/{tmdbId}/season/{season}/episode/{episode}"
           + $"?api_key={Uri.EscapeDataString(apiKey)}&language={Uri.EscapeDataString(NormalizeLanguage(language))}";

    internal static string BuildEpisodeTranslationsUrl(
        string apiKey,
        int tmdbId,
        int season,
        int episode)
        => $"{BaseUrl}/tv/{tmdbId}/season/{season}/episode/{episode}/translations"
           + $"?api_key={Uri.EscapeDataString(apiKey)}";

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en-US";
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).Name;
        }
        catch (CultureNotFoundException)
        {
            return "en-US";
        }
    }

    /// <summary>Parses the first TMDB tv id from a search/tv response (testable, no network).</summary>
    internal static int? ParseFirstTvId(string json, int? preferredYear)
        => TryParseFirstTvId(json, preferredYear, out var id) ? id : null;

    private static bool TryParseFirstTvId(string json, int? preferredYear, out int? id)
    {
        id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            int? fallback = null;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var parsedId))
                {
                    continue;
                }

                if (!preferredYear.HasValue)
                {
                    id = parsedId;
                    return true;
                }

                if (item.TryGetProperty("first_air_date", out var dateEl)
                    && dateEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(
                        dateEl.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var airDate)
                    && airDate.Year == preferredYear.Value)
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
    /// Diagnostics-only: validates the TMDB API key with a known search and
    /// returns only sanitized status and sample metadata.
    /// </summary>
    public async Task<TmdbTestResult> TestConnectionAsync(string apiKey, CancellationToken cancellationToken)
    {
        var result = new TmdbTestResult();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.ErrorMessage = "TMDB API key is empty.";
            return result;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(DiagnosticsTimeoutSeconds);
            client.MaxResponseContentBufferSize = MaximumResponseBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Jellyfin-Plugin/diagnostics");

            var url = BuildSearchTvUrl(apiKey, "Boku no Kokoro", 2023);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            result.StatusCode = (int)response.StatusCode;

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

    /// <summary>
    /// Reads an overview only from a translation record whose declared language
    /// matches the request. This prevents TMDB's localized endpoint fallback from
    /// being mislabeled as native Italian metadata.
    /// </summary>
    internal static string? ParseEpisodeTranslation(string json, string language)
    {
        try
        {
            var normalized = NormalizeLanguage(language);
            var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var requestedLanguage = parts[0];
            var requestedRegion = parts.Length > 1 ? parts[1] : null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? languageFallback = null;
            foreach (var translation in translations.EnumerateArray())
            {
                if (!translation.TryGetProperty("iso_639_1", out var languageElement)
                    || languageElement.ValueKind != JsonValueKind.String
                    || !string.Equals(
                        languageElement.GetString(),
                        requestedLanguage,
                        StringComparison.OrdinalIgnoreCase)
                    || !translation.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("overview", out var overviewElement)
                    || overviewElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(overviewElement.GetString()))
                {
                    continue;
                }

                var overview = overviewElement.GetString()!.Trim();
                var region = translation.TryGetProperty("iso_3166_1", out var regionElement)
                    && regionElement.ValueKind == JsonValueKind.String
                    ? regionElement.GetString()
                    : null;
                if (requestedRegion is not null
                    && string.Equals(region, requestedRegion, StringComparison.OrdinalIgnoreCase))
                {
                    return overview;
                }

                languageFallback ??= overview;
            }

            return languageFallback;
        }
        catch
        {
            return null;
        }
    }

    private SemaphoreSlim GetSingleFlight(string key)
        => _singleFlight.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private sealed record EpisodeTranslationsFetchResult(
        string? Json,
        bool HasTranslations,
        bool Completed)
    {
        public static EpisodeTranslationsFetchResult ConfirmedEmpty { get; } =
            new(null, false, true);
        public static EpisodeTranslationsFetchResult Incomplete { get; } =
            new(null, false, false);
    }

    private sealed record ExternalIdLookupResult(int? Id, bool Completed)
    {
        public static ExternalIdLookupResult ConfirmedMiss { get; } = new(null, true);
        public static ExternalIdLookupResult Incomplete { get; } = new(null, false);
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
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? SampleId { get; set; }
    public string? SampleName { get; set; }
}