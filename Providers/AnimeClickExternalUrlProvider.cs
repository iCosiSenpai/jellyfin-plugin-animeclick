using System.Collections.Generic;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Adds a direct link to the AnimeClick page in Jellyfin's external links sidebar.
/// </summary>
public class AnimeClickExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "AnimeClick";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        var id = item.GetProviderId("AnimeClick");
        if (string.IsNullOrWhiteSpace(id) || item is not (Movie or Series or Season or Episode))
        {
            yield break;
        }

        var baseUrl = Plugin.Instance?.Configuration?.BaseUrl ?? "https://www.animeclick.it";
        var valid = item is Episode
            ? AnimeClickClient.TryBuildEpisodeUrl(baseUrl, id, out var url)
            : AnimeClickClient.TryBuildAnimeUrl(baseUrl, id, out url);
        if (valid) yield return url;
    }
}
