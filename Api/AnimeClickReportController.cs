using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnimeClick.Plugin.Api;

/// <summary>Read-only state for the maintenance tools of the page: the cache and the diagnostic report.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public sealed class AnimeClickReportController(
    AnimeClickCacheService cache,
    AnimeClickLibraryWorkspace workspace,
    AnimeClickActivityService activities,
    AnimeClickRepairLedger repairLedger,
    IApplicationHost applicationHost) : ControllerBase
{
    /// <summary>How much the plugin keeps on disk, so clearing it is an informed choice.</summary>
    [HttpGet("CacheStats")]
    public ActionResult<AnimeClickCacheStatistics> CacheStats() => Ok(cache.GetStatistics());

    /// <summary>
    /// A report safe to attach to a public issue: no secrets, titles, paths or addresses. See
    /// <see cref="AnimeClickDiagnosticReport"/> for what is redacted and why.
    /// </summary>
    [HttpGet("DiagnosticReport")]
    public async Task<ActionResult<AnimeClickDiagnosticReport>> DiagnosticReport(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        await repairLedger.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return Ok(AnimeClickDiagnosticReport.Create(
            configuration,
            applicationHost.ApplicationVersionString,
            workspace.GetLibraries(),
            workspace.CountIdentified(),
            [activities.Get(AnimeClickActivityService.Titles), activities.Get(AnimeClickActivityService.Synopses)],
            cache.GetStatistics(),
            repairLedger.Count));
    }
}
