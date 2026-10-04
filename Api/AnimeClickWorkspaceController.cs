using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnimeClick.Plugin.Api;

/// <summary>Library state for the configuration page: showcase, provider status and one-click enabling.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public sealed class AnimeClickWorkspaceController(AnimeClickLibraryWorkspace workspace) : ControllerBase
{
    /// <summary>The anime AnimeClick refreshed most recently, newest first.</summary>
    [HttpGet("Showcase")]
    public ActionResult<AnimeClickShowcase> Showcase([FromQuery] int? limit)
        => Ok(workspace.GetShowcase(limit ?? 24, Plugin.Instance?.Configuration ?? new PluginConfiguration()));

    /// <summary>Every identified series and film, for the library grid.</summary>
    [HttpGet("Catalog")]
    public ActionResult<IEnumerable<AnimeClickCatalogItem>> Catalog() => Ok(workspace.GetCatalog());

    /// <summary>Video libraries with AnimeClick's place among their metadata and image providers.</summary>
    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<AnimeClickLibraryStatus>> Libraries() => Ok(workspace.GetLibraries());

    /// <summary>Adds AnimeClick to one library's providers. Called only after the administrator confirms.</summary>
    [HttpPost("Libraries/Enable")]
    public ActionResult<AnimeClickLibraryEnableResult> EnableLibrary([FromBody] EnableLibraryRequest request)
    {
        var result = workspace.Enable(request?.LibraryId, Plugin.Instance?.Configuration ?? new PluginConfiguration());
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    public sealed class EnableLibraryRequest
    {
        public string? LibraryId { get; set; }
    }
}
