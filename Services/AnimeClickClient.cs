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
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnimeClickClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AnimeClickClient(HttpClient httpClient, ILogger<AnimeClickClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Builds the full anime page URL from a provider ID.
    /// AnimeClick requires a slug after the numeric ID: /anime/72/naruto.
    /// If the ID is purely numeric (old format), appends "/x" as a placeholder
    /// which AnimeClick accepts and internally resolves.
    /// </summary>
    public static string BuildAnimeUrl(string baseUrl, string animeClickId)
    {
        // If the ID already contains a slash (e.g. "72/naruto"), use as-is
        var id = animeClickId.Trim('/');
        if (!id.Contains('/'))
        {
            // Numeric-only ID: append placeholder slug
            id = id + "/x";
        }

        return $"{baseUrl}/anime/{id}";
    }

    /// <summary>
    /// Returns a UA string with correct plugin version (prefers assembly version over the
    /// possibly stale value stored in configuration).
    /// </summary>
    internal static string GetEffectiveUserAgent(PluginConfiguration configuration)
    {
        var configured = configuration?.UserAgent ?? string.Empty;
        var asmVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.3.8.0";

        // If config contains an obvious stale version placeholder, or no version, rebuild.
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Contains("0.3.5.0") ||
            configured.Contains("0.3.0.0") ||
            !configured.Contains("AnimeClick"))
        {
            return $"AnimeClick-Jellyfin-Plugin/{asmVer} (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";
        }

        // If it has a version but it's not matching current asm, prefer asm (keeps +url)
        if (configured.Contains("AnimeClick-Jellyfin-Plugin/") && !configured.Contains(asmVer))
        {
            return configured.Replace(
                configured.Substring(configured.IndexOf("AnimeClick-Jellyfin-Plugin/"), "AnimeClick-Jellyfin-Plugin/0.3.x.0".Length),
                $"AnimeClick-Jellyfin-Plugin/{asmVer}");
        }

        return configured;
    }

    public async Task<string> GetStringAsync(string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        // Bounded retry for transient server / network errors only. 4xx and auth problems are not retried.
        const int maxAttempts = 2;
        Exception? lastEx = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(GetEffectiveUserAgent(configuration));
                request.Headers.Referrer = new Uri(configuration.BaseUrl);

                // AnimeClick serves an interstitial video-intro ad page on first visit.
                // Setting the ac_campaign cookie bypasses it and returns the real content.
                request.Headers.Add("Cookie", "ac_campaign=show");

                _logger.LogDebug("AnimeClick HTTP fetch: {Url} (attempt {Attempt})", url, attempt + 1);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if ((int)response.StatusCode >= 500 && attempt < maxAttempts - 1)
                {
                    // Transient server error — release gate, wait a bit, retry
                    _logger.LogWarning("AnimeClick transient {Status} for {Url}, will retry", response.StatusCode, url);
                    await Task.Delay(300 + (attempt * 400), cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                // Respect ethical scraping: wait between requests as requested by AnimeClick staff.
                await Task.Delay(configuration.RequestDelayMilliseconds, cancellationToken);
                return content;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Fetch cancellato o andato in timeout per l'URL: {Url}", url);
                throw;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts - 1)
            {
                // Network / connect / DNS transient — retry
                lastEx = ex;
                _logger.LogDebug(ex, "AnimeClick network transient for {Url}, retrying", url);
                await Task.Delay(250 + (attempt * 300), cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogError(ex, "Errore HTTP verso AnimeClick: {Url}", url);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        // If we exhausted retries, rethrow last
        if (lastEx != null) throw lastEx;
        throw new HttpRequestException($"AnimeClick request failed after {maxAttempts} attempts: {url}");
    }
}
