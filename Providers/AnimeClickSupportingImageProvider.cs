using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AnimeClick.Plugin.Providers;

/// <summary>Season artwork and episode stills from the same AnimeClick provider name.</summary>
public sealed class AnimeClickSupportingImageProvider(AnimeClickArtwork artwork, IHttpClientFactory factory) : IRemoteImageProvider, IHasOrder
{
    public string Name => "AnimeClick";
    public int Order => 100;
    public bool Supports(BaseItem item) => item is Season or Episode;
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => item is Season ? [ImageType.Primary, ImageType.Banner, ImageType.Thumb] : [ImageType.Primary];
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        => await artwork.GetImagesAsync(item, Plugin.Instance?.Configuration ?? new PluginConfiguration(), cancellationToken).ConfigureAwait(false);
    public Task<System.Net.Http.HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => AnimeClickHttp.GetImageAsync(factory, url, Plugin.Instance?.Configuration ?? new PluginConfiguration(), cancellationToken);
}
