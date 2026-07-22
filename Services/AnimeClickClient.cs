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

    // AnimeClickClient is registered as a typed HttpClient and can therefore have multiple
    // instances (one per singleton provider). The gate must be process-wide or those
    // instances would bypass the configured delay when Jellyfin refreshes items in parallel.
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _nextRequestUtc = DateTime.MinValue;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AnimeClickClient> _logger;

    public AnimeClickClient(HttpClient httpClient, ILogger<AnimeClickClient> logger)
    {
        _httpClient = httpClient;
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
    /// Returns a UA string with the assembly version, replacing only the plugin token
    /// instead of relying on the configured version having a fixed string length.
    /// </summary>
    internal static string GetEffectiveUserAgent(PluginConfiguration configuration)
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.4.2.0";
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
                using var response = await _httpClient
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
                            // Never retry before the server's Retry-After deadline. A very
                            // long deadline is persisted in the global gate and surfaced now.
                            suppressRetry = true;
                            _logger.LogWarning(
                                "AnimeClick {Status} for {Url} requested Retry-After={Delay}; not retrying",
                                response.StatusCode,
                                url,
                                retryDelay);
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

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
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

        return delay.Value;
    }
}
