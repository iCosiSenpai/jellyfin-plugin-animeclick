using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Services;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Fallback image provider for anime Series and Movies: returns the Italian
/// poster (locandina) already parsed by <see cref="AnimeClickHtmlParser"/> from
/// the AnimeClick anime page (og:image / itemprop='image').
///
/// Priority is intentionally low (<see cref="Order"/> = 100) so AniList,
/// FanartTV and other image providers with a lower order win when they have
/// images; AnimeClick only fills the gap when nobody else delivered a poster.
/// This does NOT block other providers — it never calls SetImage and only
/// contributes a candidate via the normal IRemoteImageProvider flow.
/// </summary>
public class AnimeClickAnimeImageProvider : IRemoteImageProvider, IHasOrder
{
    // 4 KB was not enough: a JPEG with an EXIF (APP1) or ICC profile (APP2) segment pushes the
    // SOFn marker that carries the dimensions well past it, so the probe hit end-of-stream and
    // reported failure for a whole class of posters. 64 KB covers those and is still a range
    // request, not a full download.
    private const int ProbePrefixBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;

    public AnimeClickAnimeImageProvider(
        IHttpClientFactory httpClientFactory,
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser)
    {
        _httpClientFactory = httpClientFactory;
        _client = client;
        _cache = cache;
        _parser = parser;
    }

    public string Name => "AnimeClick";

    /// <summary>Low priority: run after AniList/Fanart/altro so we only fill gaps.</summary>
    public int Order => 100;

    public bool Supports(BaseItem item) => item is Series or Movie;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        // AnimeClick exposes only a single cover image (BannerUrl == ImageUrl today),
        // so we only contribute a Primary (locandina). If a real backdrop/logo becomes
        // available in the parser, add ImageType.Backdrop / Logo here.
        return [ImageType.Primary];
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var results = new List<RemoteImageInfo>();

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!configuration.EnableAnimeClickImages)
        {
            return results;
        }

        if (!item.ProviderIds.TryGetValue("AnimeClick", out var animeClickId) || string.IsNullOrWhiteSpace(animeClickId))
        {
            return results;
        }

        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var url))
        {
            return results;
        }

        var cacheKey = $"anime::{url}";

        AnimeClickAnime? anime;
        try
        {
            var cached = await _cache.GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken);
            if (cached is not null)
            {
                anime = cached;
            }
            else
            {
                var html = await _client.GetStringAsync(url, configuration, cancellationToken);
                anime = _parser.ParseAnimePage(url, html);
                await _cache.SetAsync(cacheKey, anime, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never crash the Jellyfin image pipeline on a fetch/parse error.
            return results;
        }

        if (anime is null || string.IsNullOrWhiteSpace(anime.ImageUrl))
        {
            return results;
        }

        // Resolve against the configured base and enforce HTTPS + allowed host so a scraped
        // og:image cannot turn the metadata scan into an arbitrary outbound request (SSRF).
        if (!AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, anime.ImageUrl, out var imageUri))
        {
            return results;
        }

        var imageUrl = imageUri.AbsoluteUri;

        // Skip generic placeholders
        if (imageUrl.Contains("not_found", StringComparison.OrdinalIgnoreCase))
        {
            return results;
        }

        // Cheap dimension probe (Range request + prefix only) + cache so we avoid
        // full-image downloads just to decide whether to advertise the poster.
        // If width known and below threshold we return empty list → other providers win.
        int width = 0;
        int height = 0;
        try
        {
            var dimCacheKey = $"imagedim::{imageUrl}";
            var cachedDim = await _cache.GetAsync<ImageDim>(dimCacheKey, 24 * 7, cancellationToken); // 7 days
            if (cachedDim is not null)
            {
                width = cachedDim.W;
                height = cachedDim.H;
            }
            else
            {
                (width, height) = await ProbePosterDimensionsAsync(imageUrl, configuration, cancellationToken);

                // Only a successful probe is cached. A 403, a timeout or an unrecognised
                // header used to be stored as (0,0) for a week, and since width == 0 skips
                // the MinPosterWidth check below, one transient failure disabled the
                // low-resolution filter for that poster until the entry lapsed.
                if (width > 0)
                {
                    await _cache.SetAsync(dimCacheKey, new ImageDim { W = width, H = height }, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // best-effort only
        }

        // Below the configured threshold → skip so the next image fetcher wins (Order = 100).
        // width == 0 means probe failed; in that case we still offer (conservative, same as before).
        if (configuration.MinPosterWidth > 0 && width > 0 && width < configuration.MinPosterWidth)
        {
            // Low-res AC poster deliberately not advertised so higher-priority providers (Fanart etc.) are preferred.
            return results;
        }

        results.Add(new RemoteImageInfo
        {
            ProviderName = Name,
            Type = ImageType.Primary,
            Url = imageUrl,
            Width = width > 0 ? width : null,
            Height = height > 0 ? height : null
        });

        return results;
    }

    private async Task<(int width, int height)> ProbePosterDimensionsAsync(string imageUrl, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(imageUrl));
        request.Headers.Range = new RangeHeaderValue(0, ProbePrefixBytes - 1); // header only
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            AnimeClickClient.GetEffectiveUserAgent(configuration));
        if (Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (0, 0);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Read a safe prefix into memory (handles non-seekable streams from HttpContent)
            var prefix = new byte[ProbePrefixBytes];
            int total = 0;
            while (total < prefix.Length)
            {
                int n = await stream.ReadAsync(prefix, total, prefix.Length - total, cancellationToken);
                if (n <= 0) break;
                total += n;
            }

            using var ms = new MemoryStream(prefix, 0, total, writable: false);
            if (ImageDimensions.TryRead(ms, out var w, out var h))
            {
                return (w, h);
            }
            return (0, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (0, 0);
        }
    }

    // Small DTO for caching dimensions (kept private to this provider)
    private sealed class ImageDim
    {
        public int W { get; set; }
        public int H { get; set; }
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Defense in depth: only ever fetch an allowed HTTPS host, mirroring GetImages.
        if (!AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, url, out var imageUri))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var client = _httpClientFactory.CreateClient();

        // AnimeClick's CDN rejects requests without a browser-like User-Agent (HTTP 403),
        // so mirror the headers used by AnimeClickClient for HTML fetches.
        var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            AnimeClickClient.GetEffectiveUserAgent(configuration));
        if (Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
        }

        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}