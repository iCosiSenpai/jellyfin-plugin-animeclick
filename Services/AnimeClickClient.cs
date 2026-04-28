using System;
using System.Net;
using System.Net.Http;
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

    public async Task<string> GetStringAsync(string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(configuration.UserAgent);
            request.Headers.Referrer = new Uri(configuration.BaseUrl);

            // AnimeClick serves an interstitial video-intro ad page on first visit.
            // Setting the ac_campaign cookie bypasses it and returns the real content.
            request.Headers.Add("Cookie", "ac_campaign=show");

            _logger.LogDebug("AnimeClick HTTP fetch: {Url}", url);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Respect ethical scraping: wait between requests as requested by AnimeClick staff.
            await Task.Delay(configuration.RequestDelayMilliseconds, cancellationToken);
            return content;
        }
        catch (OperationCanceledException)
        {
            // The task was cancelled by the user or a timeout occurred. Log as Info/Debug instead of Error.
            _logger.LogInformation("Fetch cancellato o andato in timeout per l'URL: {Url}", url);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore HTTP verso AnimeClick: {Url}", url);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
