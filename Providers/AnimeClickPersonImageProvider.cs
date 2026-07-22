using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Provides person images (profile photos) for actors/staff from AnimeClick.
/// This provider returns the image URL that was already captured during
/// cast/staff parsing (stored in PersonInfo.ImageUrl).
/// </summary>
public class AnimeClickPersonImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickClient _animeClickClient;

    public AnimeClickPersonImageProvider(IHttpClientFactory httpClientFactory, AnimeClickClient animeClickClient)
    {
        _httpClientFactory = httpClientFactory;
        _animeClickClient = animeClickClient;
    }

    public string Name => "AnimeClick";
    /// <summary>Low priority so AniList/TMDB/etc person photos (often higher res) are preferred.</summary>
    public int Order => 100;

    public bool Supports(BaseItem item) => item is Person;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return [ImageType.Primary];
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var results = new List<RemoteImageInfo>();

        if (item is not Person person) return results;

        // Retrieve the relative URL of the actor's page from ProviderIds
        if (!person.ProviderIds.TryGetValue("AnimeClick", out var actorId) || string.IsNullOrWhiteSpace(actorId))
        {
            return results;
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !Uri.TryCreate(baseUri, actorId, out var personUri)
            || !string.Equals(baseUri.Scheme, personUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseUri.Host, personUri.Host, StringComparison.OrdinalIgnoreCase)
            || baseUri.Port != personUri.Port
            || !personUri.AbsolutePath.StartsWith("/autore/", StringComparison.OrdinalIgnoreCase))
        {
            // Provider IDs are persisted local data: never let a malformed/absolute value
            // turn the metadata scanner into an arbitrary outbound request.
            return results;
        }

        try
        {
            // Ethical fetching via the process-wide AnimeClick rate gate.
            var response = await _animeClickClient.GetStringAsync(
                personUri.AbsoluteUri,
                configuration,
                cancellationToken);

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(response);

            // Fetch the image from the actor's page
            var imgNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'thumbnail')]//img[contains(@src, 'autore') or contains(@src, 'immagini')] | //img[contains(@class, 'img-autore')]");
            var imageUrl = imgNode?.GetAttributeValue("src", null);

            if (!string.IsNullOrWhiteSpace(imageUrl)
                && AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, imageUrl, out var resolvedImageUri))
            {
                imageUrl = resolvedImageUri.AbsoluteUri;

                // Skip generic placeholder
                if (!imageUrl.Contains("not_found", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Primary,
                        Url = imageUrl
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Ignore fetch errors to not crash Jellyfin Metadata pipeline
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!AnimeClickClient.TryResolveAllowedImageUri(configuration.BaseUrl, url, out var imageUri))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var client = _httpClientFactory.CreateClient();

        // AnimeClick's CDN rejects requests without a browser-like User-Agent (HTTP 403).
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
