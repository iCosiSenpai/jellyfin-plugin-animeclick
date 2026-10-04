using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
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

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(CancellationToken token) => Ok(await community.SummaryAsync(token).ConfigureAwait(false));

    [HttpGet("Proposals")]
    public async Task<IActionResult> Proposals(CancellationToken token)
        => Ok((await community.ProposalsAsync(token).ConfigureAwait(false)).Take(50).Select(proposal => new
        {
            proposal.Fingerprint, proposal.Mapping, proposal.State, proposal.Issue, proposal.Url, proposal.CreatedAt, proposal.SentAt
        }));

    [HttpGet("Preview")]
    public IActionResult Preview([FromQuery] string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return BadRequest();
        var item = library.GetItemById(id);
        if (item is null) return NotFound();
        var mapping = community.CreateMapping(item);
        return mapping is null
            ? BadRequest(new { error = "Servono un film, una serie o una stagione con ID AnimeClick e almeno un ID TMDB o TheTVDB (AniList vale per serie e film)." })
            : Ok(mapping);
    }

    /// <summary>Queues one correction after an explicit «Condividi», whatever the automatic choice.</summary>
    [HttpPost("Share")]
    public async Task<IActionResult> Share([FromBody] AnimeClickCommunityShareRequest request, CancellationToken token)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var id)) return BadRequest(new { error = "Elemento non valido." });
        var item = library.GetItemById(id);
        if (item is null) return NotFound(new { error = "L’elemento non è più presente nella libreria." });
        return Ok(new { Message = await community.ShareAsync(item, token).ConfigureAwait(false) });
    }

    [HttpPost("Retry")]
    public async Task<IActionResult> Retry(CancellationToken token)
    {
        await community.RetryAsync(token).ConfigureAwait(false);
        return Ok(await community.StatusAsync(token).ConfigureAwait(false));
    }

    /// <summary>Every public mapping of the library, seasons included: the starting point to seed the dataset.</summary>
    [HttpGet("Export")]
    public IActionResult Export()
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Season], Recursive = true, IsVirtualItem = false
        });
        var mappings = items
            .Where(item => item is not Season season || !string.IsNullOrWhiteSpace(season.GetProviderId("AnimeClick")))
            .Select(community.CreateMapping)
            .OfType<AnimeClickCommunityMapping>()
            .DistinctBy(AnimeClickCommunityData.Fingerprint)
            .Take(AnimeClickCommunityData.MaximumMappings)
            .ToList();
        return Ok(new AnimeClickCommunityDataset { Mappings = mappings });
    }
}

public sealed class AnimeClickCommunityShareRequest
{
    public string ItemId { get; set; } = string.Empty;
}
