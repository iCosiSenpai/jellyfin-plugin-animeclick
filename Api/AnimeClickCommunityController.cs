using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnimeClick.Plugin.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick/Community")]
public sealed class AnimeClickCommunityController(AnimeClickCommunityService community, ILibraryManager library) : ControllerBase
{
    [HttpGet("Status")]
    public async Task<IActionResult> Status(CancellationToken token) => Ok(await community.StatusAsync(token).ConfigureAwait(false));

    [HttpGet("Preview")]
    public IActionResult Preview([FromQuery] string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return BadRequest();
        var item = library.GetItemById(id);
        if (item is null) return NotFound();
        var mapping = AnimeClickCommunityService.BuildMapping(item);
        return mapping is null ? BadRequest(new { error = "Servono un film o una serie con ID AnimeClick e almeno un ID TMDB, TVDB o AniList." }) : Ok(mapping);
    }

    [HttpPost("Retry")]
    public async Task<IActionResult> Retry(CancellationToken token)
    {
        await community.RetryAsync(token).ConfigureAwait(false);
        return Ok(await community.StatusAsync(token).ConfigureAwait(false));
    }

    [HttpGet("Export")]
    public IActionResult Export()
    {
        var mappings = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie], Recursive = true, IsVirtualItem = false
        }).Select(AnimeClickCommunityService.BuildMapping).OfType<AnimeClickCommunityMapping>()
            .DistinctBy(mapping => mapping.Kind + ":" + mapping.AnimeClickId + ":"
                + string.Join(",", mapping.ProviderIds.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)))
            .Take(5000).ToList();
        return Ok(new AnimeClickCommunityDataset { Mappings = mappings });
    }
}
