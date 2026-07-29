using System.Security.Cryptography;
using System.Text;

namespace AnimeClick.Harness;

/// <summary>
/// Fetches AnimeClick pages the way the plugin does, and caches them on disk so a report can
/// be re-run, and the analysis iterated on, without touching the site again.
/// </summary>
internal sealed class Fetcher : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _delay;
    private readonly bool _refresh;
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public int NetworkRequests { get; private set; }

    public int CacheHits { get; private set; }

    public Fetcher(string cacheDirectory, TimeSpan delay, bool refresh)
    {
        _cacheDirectory = cacheDirectory;
        _delay = delay;
        _refresh = refresh;
        Directory.CreateDirectory(_cacheDirectory);

        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "AnimeClick-Jellyfin-Plugin/harness (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)");

        // Same trick the plugin uses: without this cookie AnimeClick serves a video-intro
        // interstitial on first visit instead of the real page, and every selector then misses.
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "ac_campaign=show");
    }

    /// <summary>Returns the page body, or null on 404 / any transport failure.</summary>
    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_cacheDirectory, CacheKey(url) + ".html");
        if (!_refresh && File.Exists(cachePath))
        {
            CacheHits++;
            return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }

        var wait = _nextRequestUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            NetworkRequests++;
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(cachePath, body, cancellationToken).ConfigureAwait(false);
            return body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  ! fetch fallito {url}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            _nextRequestUtc = DateTime.UtcNow.Add(_delay);
        }
    }

    private static string CacheKey(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];

    public void Dispose() => _client.Dispose();
}
