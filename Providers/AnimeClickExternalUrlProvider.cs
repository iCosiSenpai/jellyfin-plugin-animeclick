using System.Collections.Generic;
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
        if (string.IsNullOrWhiteSpace(id))
        {
            yield break;
        }

        var baseUrl = Plugin.Instance?.Configuration?.BaseUrl ?? "https://www.animeclick.it";
        yield return $"{baseUrl}/anime/{id}";
    }
}
