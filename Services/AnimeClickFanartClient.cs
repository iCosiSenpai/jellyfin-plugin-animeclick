using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>Fanart v3.2 supports a personal key alone. Credentials travel in headers, never image URLs.</summary>
public sealed class AnimeClickFanartClient(IHttpClientFactory factory, AnimeClickCacheService cache, ILogger<AnimeClickFanartClient> logger)
{
    private static readonly RequestThrottle Throttle = new("Fanart", TimeSpan.FromMilliseconds(500));
    private readonly SemaphoreStripe _gates = new();

    internal async Task<JsonElement?> GetArtworkAsync(int id, bool movie, PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id <= 0 || !configuration.EnableFanartImages || (string.IsNullOrWhiteSpace(configuration.FanartPersonalApiKey)
            && string.IsNullOrWhiteSpace(configuration.FanartProjectApiKey))) return null;
        var kind = movie ? "movies" : "tv";
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.FanartPersonalApiKey + "\n" + configuration.FanartProjectApiKey)));
        var key = $"fanart:v3.2::{kind}::{id}::{profile}";
        var gate = _gates.Get(key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var json = await cache.GetAsync<string>(key, configuration.CacheHours, token).ConfigureAwait(false);
            if (json is null)
            {
                if (await cache.GetAsync<string>(key + "::404", configuration.NegativeCacheHours, token).ConfigureAwait(false) is not null) return null;
                using var client = factory.CreateClient(AnimeClickHttp.ClientName);
                client.Timeout = TimeSpan.FromSeconds(15);
                client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://webservice.fanart.tv/v3.2/{kind}/{id}");
                if (!string.IsNullOrWhiteSpace(configuration.FanartPersonalApiKey)) request.Headers.Add("client-key", configuration.FanartPersonalApiKey.Trim());
                if (!string.IsNullOrWhiteSpace(configuration.FanartProjectApiKey)) request.Headers.Add("api-key", configuration.FanartProjectApiKey.Trim());
                request.Headers.TryAddWithoutValidation("User-Agent", AnimeClickClient.GetEffectiveUserAgent(configuration));
                await Throttle.WaitAsync(token).ConfigureAwait(false);
                using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                if (RequestThrottle.IsRateLimited(response.StatusCode)) { Throttle.NoticeRateLimit(response); return null; }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    await cache.SetAsync(key + "::404", "404", token).ConfigureAwait(false);
                    return null;
                }
                if (!response.IsSuccessStatusCode) return null;
                json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var validated = JsonDocument.Parse(json);
                if (!ValidIdentity(validated.RootElement, id, movie)) return null;
                await cache.SetAsync(key, json, token).ConfigureAwait(false);
            }
            using var document = JsonDocument.Parse(json);
            return ValidIdentity(document.RootElement, id, movie) ? document.RootElement.Clone() : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { logger.LogWarning("Fanart source unavailable; trying other configured artwork sources"); return null; }
        finally { gate.Release(); }
    }

    internal static bool ValidIdentity(JsonElement root, int id, bool movie) => root.ValueKind == JsonValueKind.Object
        && AnimeClickTmdbClient.Object(root, movie ? "tmdb_id" : "thetvdb_id").ToString() == id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
