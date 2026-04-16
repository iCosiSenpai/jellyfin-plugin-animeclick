using System;
using System.Collections.Generic;
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
    public int Order => 0;

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
        var baseUrl = configuration.BaseUrl ?? "https://www.animeclick.it";
        var fullUrl = actorId.StartsWith("http") ? actorId : baseUrl + actorId;

        try
        {
            // Ethical fetching via synchronized semaphore client
            var response = await _animeClickClient.GetStringAsync(fullUrl, configuration, cancellationToken);

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(response);

            // Fetch the image from the actor's page
            var imgNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'thumbnail')]//img[contains(@src, 'autore') or contains(@src, 'immagini')] | //img[contains(@class, 'img-autore')]");
            var imageUrl = imgNode?.GetAttributeValue("src", null);

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                if (!imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    imageUrl = baseUrl + imageUrl;
                }

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
        catch (Exception)
        {
            // Ignore fetch errors to not crash Jellyfin Metadata pipeline
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }
}
