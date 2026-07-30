using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public partial class AnimeClickClient
{
    private const int MinimumRequestDelayMilliseconds = 500;
    private const int MaximumRequestDelayMilliseconds = 60_000;
    private const int MaxAttempts = 2;

    // A Retry-After from the server is honoured but never trusted unbounded. The deadline is
    // persisted in the process-wide gate below, which only ever moves forward, so an absurd
    // value — from a misconfigured origin, an intermediate proxy, or a hostile host set as
    // BaseUrl — would otherwise park every later request behind a wait of arbitrary length
    // until Jellyfin restarts, with the requests hanging on the gate instead of failing.
    private const int MaximumServerBackoffMilliseconds = 15 * 60 * 1000;

    // The factory's default client has no timeout of its own, so without these two the plugin
    // inherited HttpClient's 100 s per request and an unbounded response body. An AnimeClick
    // page is a few hundred KB: a request that needs more than half a minute, or a body of
    // several MB, is a fault to surface rather than something to keep buffering into memory
    // on a NAS.
    private const int RequestTimeoutSeconds = 30;
    private const long MaximumResponseBytes = 8 * 1024 * 1024;

    // AnimeClickClient resolves the shared HttpClient through IHttpClientFactory (exactly like
    // the other network clients) instead of registering a typed client. This keeps the plugin
    // from adding a type-name-keyed HttpClient registration that would clash if two plugin
    // versions were ever loaded at once. The gate must stay process-wide, otherwise parallel
    // Jellyfin refreshes would bypass the configured delay.
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _nextRequestUtc = DateTime.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeClickClient> _logger;

    public AnimeClickClient(IHttpClientFactory httpClientFactory, ILogger<AnimeClickClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [GeneratedRegex(@"^\d+(?:/[\p{L}\p{Nd}_~-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AnimeClickIdRegex();

    [GeneratedRegex(@"^(\d+)-([\p{L}\p{Nd}_~-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAnimeClickIdRegex();

    [GeneratedRegex(@"AnimeClick-Jellyfin-Plugin/[^\s();]+", RegexOptions.CultureInvariant)]
    private static partial Regex PluginUserAgentRegex();

    /// <summary>Validates and normalizes a provider ID without performing network I/O.</summary>
    public static bool TryNormalizeAnimeClickId(string? value, out string normalized)
    {
        normalized = value?.Trim().Trim('/') ?? string.Empty;

        // AnimeClick slugs carry Italian accents, and the URL the id was captured from encodes
        // them: "216767/c%C3%A8-una-ragione-per-tutto". Without decoding first, the character
        // class below rejects '%' and the plugin refuses an id it wrote itself — and with it the
        // whole AnimeClick episode-synopsis path disappears silently for every episode whose
        // title has an accent. Measured on a real library: 224 of 1781 stored episode ids, 13%.
        //
        // Decoding before validating is also the safe order: validation still runs against the
        // same conservative class, so nothing structural survives it. "%2F" becomes a second
        // slash and "%2E%2E" becomes dots, and both are then rejected rather than reaching URL
        // composition.
        if (normalized.Contains('%', StringComparison.Ordinal))
        {
            normalized = Uri.UnescapeDataString(normalized);
        }

        if (AnimeClickIdRegex().IsMatch(normalized))
        {
            return true;
        }

        // v0.2.x briefly documented IDs as "2966-naruto". Accept and migrate that
        // persisted form even though AnimeClick's canonical provider ID is "2966/naruto".
        var legacyMatch = LegacyAnimeClickIdRegex().Match(normalized);
        if (!legacyMatch.Success)
        {
            return false;
        }

        normalized = legacyMatch.Groups[1].Value + "/" + legacyMatch.Groups[2].Value;
        return true;
    }

    /// <summary>
    /// Builds the full anime page URL from a provider ID.
    /// AnimeClick requires a slug after the numeric ID: /anime/72/naruto.
    /// If the ID is purely numeric (old format), appends "/x" as a placeholder
    /// which AnimeClick accepts and internally resolves.
    /// </summary>
    public static string BuildAnimeUrl(string baseUrl, string animeClickId)
    {
        if (TryBuildAnimeUrl(baseUrl, animeClickId, out var url))
        {
            return url;
        }

        throw new ArgumentException(
            "AnimeClick BaseUrl or provider ID is invalid; expected an absolute HTTP(S) URL and 'number/slug' ID.");
    }

    /// <summary>Attempts to build a canonical anime URL without throwing on stale local IDs.</summary>
    public static bool TryBuildAnimeUrl(string? baseUrl, string? animeClickId, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !TryNormalizeAnimeClickId(animeClickId, out var id))
        {
            return false;
        }

        if (!id.Contains('/', StringComparison.Ordinal))
        {
            id += "/x";
        }

        url = new Uri(baseUri, "anime/" + id).AbsoluteUri;
        return true;
    }

    /// <summary>Attempts to build a canonical episode detail URL from its provider ID.</summary>
    public static bool TryBuildEpisodeUrl(string? baseUrl, string? episodeProviderId, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !TryNormalizeAnimeClickId(episodeProviderId, out var id))
        {
            return false;
        }

        if (!id.Contains('/', StringComparison.Ordinal))
        {
            id += "/x";
        }

        url = new Uri(baseUri, "episodio/" + id).AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Resolves an image URL (absolute or relative to the configured base) and confirms it
    /// targets an allowed host over HTTPS. This prevents a scraped or persisted URL from
    /// turning the metadata scanner into an arbitrary outbound request (SSRF) or from
    /// downgrading to plain HTTP. Allowed = the exact configured host:port, or any
    /// *.animeclick.it host on 443 when the plugin is configured against AnimeClick.
    /// </summary>
    public static bool TryResolveAllowedImageUri(string? baseUrl, string? imageUrl, out Uri imageUri)
    {
        imageUri = null!;

        // The base is treated as a directory: with a configured base that carries a path, a
        // relative src must resolve underneath it rather than replacing its last segment.
        if (string.IsNullOrWhiteSpace(imageUrl)
            || string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !Uri.TryCreate(baseUri, imageUrl, out var resolved)
            || resolved.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var baseHost = baseUri.IdnHost.TrimEnd('.');
        var imageHost = resolved.IdnHost.TrimEnd('.');
        var exactConfiguredHost = string.Equals(baseHost, imageHost, StringComparison.OrdinalIgnoreCase)
            && baseUri.Port == resolved.Port;
        var configuredForAnimeClick = string.Equals(baseHost, "animeclick.it", StringComparison.OrdinalIgnoreCase)
            || baseHost.EndsWith(".animeclick.it", StringComparison.OrdinalIgnoreCase);
        var officialAnimeClickHost = string.Equals(imageHost, "animeclick.it", StringComparison.OrdinalIgnoreCase)
            || imageHost.EndsWith(".animeclick.it", StringComparison.OrdinalIgnoreCase);
        if (!exactConfiguredHost
            && !(configuredForAnimeClick && officialAnimeClickHost && resolved.Port == 443))
        {
            return false;
        }

        imageUri = resolved;
        return true;
    }

    /// <summary>
    /// Returns a UA string with the assembly version, replacing only the plugin token
    /// instead of relying on the configured version having a fixed string length.
    /// </summary>
    internal static string GetEffectiveUserAgent(PluginConfiguration configuration)
    {
        // "0.0.0.0" rather than the version of the day: this fallback only fires if the assembly
        // carries no version at all, and a literal here silently rots at every release.
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.0.0.0";
        var defaultUserAgent =
            $"AnimeClick-Jellyfin-Plugin/{assemblyVersion} (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";
        var configured = configuration?.UserAgent?.Trim();

        if (string.IsNullOrWhiteSpace(configured) || !PluginUserAgentRegex().IsMatch(configured))
        {
            return defaultUserAgent;
        }

        return PluginUserAgentRegex().Replace(
            configured,
            _ => $"AnimeClick-Jellyfin-Plugin/{assemblyVersion}",
            count: 1);
    }

    public async Task<string> GetStringAsync(
        string url,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri)
            || (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Request URL must be an absolute HTTP(S) URL.", nameof(url));
        }

        var configuredDelay = Math.Clamp(
            configuration.RequestDelayMilliseconds,
            MinimumRequestDelayMilliseconds,
            MaximumRequestDelayMilliseconds);
        Exception? lastException = null;
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);
        httpClient.MaxResponseContentBufferSize = MaximumResponseBytes;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var requestStarted = false;
            var suppressRetry = false;
            DateTime? serverBackoffUntilUtc = null;
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_nextRequestUtc > DateTime.UtcNow)
                {
                    await WaitUntilUtcAsync(_nextRequestUtc, cancellationToken).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("User-Agent", GetEffectiveUserAgent(configuration));
                if (Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var referrer))
                {
                    request.Headers.Referrer = referrer;
                }

                // AnimeClick serves an interstitial video-intro ad page on first visit.
                // Setting the ac_campaign cookie bypasses it and returns the real content.
                request.Headers.TryAddWithoutValidation("Cookie", "ac_campaign=show");

                requestStarted = true;
                _logger.LogDebug("AnimeClick HTTP fetch: {Url} (attempt {Attempt})", url, attempt + 1);
                using var response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (IsTransient(response.StatusCode))
                {
                    var retryDelay = GetRetryDelay(response, attempt);
                    serverBackoffUntilUtc = AddUtcDelaySafely(DateTime.UtcNow, retryDelay);

                    if (attempt < MaxAttempts - 1)
                    {
                        if (retryDelay > TimeSpan.FromSeconds(30))
                        {
                            // Never retry before the server's Retry-After deadline. The deadline
                            // is persisted in the process-wide gate, so it pauses every other
                            // AnimeClick request too: say so, and surface the failure now.
                            suppressRetry = true;
                            _logger.LogWarning(
                                "AnimeClick {Status} for {Url}: honouring Retry-After={Delay} (capped at {Cap}); all AnimeClick requests are paused until then",
                                response.StatusCode,
                                url,
                                retryDelay,
                                TimeSpan.FromMilliseconds(MaximumServerBackoffMilliseconds));
                        }
                        else
                        {
                            _logger.LogWarning(
                                "AnimeClick transient {Status} for {Url}; retrying in {DelayMs} ms",
                                response.StatusCode,
                                url,
                                retryDelay.TotalMilliseconds);
                            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("AnimeClick fetch cancelled for {Url}", url);
                throw;
            }
            catch (OperationCanceledException ex) when (attempt < MaxAttempts - 1)
            {
                lastException = ex;
                var retryDelay = TimeSpan.FromMilliseconds(250 + (attempt * 300));
                _logger.LogDebug(ex, "AnimeClick request timed out for {Url}; retrying", url);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                throw new HttpRequestException($"AnimeClick request timed out: {url}", ex);
            }
            catch (HttpRequestException ex) when (!suppressRetry && attempt < MaxAttempts - 1)
            {
                lastException = ex;
                var retryDelay = TimeSpan.FromMilliseconds(250 + (attempt * 300));
                _logger.LogDebug(ex, "AnimeClick network transient for {Url}; retrying", url);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore HTTP verso AnimeClick: {Url}", url);
                throw;
            }
            finally
            {
                if (requestStarted)
                {
                    var configuredNextRequest = DateTime.UtcNow.AddMilliseconds(configuredDelay);
                    var nextRequest = serverBackoffUntilUtc > configuredNextRequest
                        ? serverBackoffUntilUtc.Value
                        : configuredNextRequest;
                    if (nextRequest > _nextRequestUtc)
                    {
                        _nextRequestUtc = nextRequest;
                    }
                }

                RequestGate.Release();
            }
        }

        throw lastException ?? new HttpRequestException(
            $"AnimeClick request failed after {MaxAttempts} attempts: {url}");
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout
           || statusCode == HttpStatusCode.TooManyRequests
           || (int)statusCode >= 500;

    private static async Task WaitUntilUtcAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        var maximumDelayChunk = TimeSpan.FromHours(12);
        while (true)
        {
            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(
                    remaining > maximumDelayChunk ? maximumDelayChunk : remaining,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static DateTime AddUtcDelaySafely(DateTime utcNow, TimeSpan delay)
        => delay >= DateTime.MaxValue - utcNow
            ? DateTime.MaxValue
            : utcNow.Add(delay);

    /// <summary>
    /// Server-requested backoff, clamped to <see cref="MaximumServerBackoffMilliseconds"/>.
    /// Exposed internally so the clamp itself is covered by regression tests.
    /// </summary>
    internal static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        ArgumentNullException.ThrowIfNull(response);

        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (!delay.HasValue && retryAfter?.Date is { } retryDate)
        {
            delay = retryDate - DateTimeOffset.UtcNow;
        }

        if (!delay.HasValue || delay.Value <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMilliseconds(300 + (attempt * 400));
        }

        var maximum = TimeSpan.FromMilliseconds(MaximumServerBackoffMilliseconds);
        return delay.Value > maximum ? maximum : delay.Value;
    }
}
