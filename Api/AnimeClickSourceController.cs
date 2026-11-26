using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Api;

/// <summary>
/// Whether AnimeClick answers the plugin, for the banner of the configuration page, and a manual
/// check that does not wait for the next automatic one.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public sealed class AnimeClickSourceController(
    AnimeClickClient client,
    ILogger<AnimeClickSourceController> logger) : ControllerBase
{
    [HttpGet("SourceStatus")]
    public ActionResult<AnimeClickSourceStatus> SourceStatus()
        => Ok(AnimeClickSourceStatus.From(client.Availability.Snapshot()));

    /// <summary>
    /// One request to AnimeClick's home page, now, even while requests are suspended. The answer
    /// is the new state: reachable again, or still behind the verification with the next
    /// automatic check pushed further.
    /// </summary>
    [HttpPost("SourceStatus/Check")]
    public async Task<ActionResult<AnimeClickSourceStatus>> CheckSource(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        client.Availability.ProbeNow();
        try
        {
            await client.GetStringAsync(configuration.BaseUrl.TrimEnd('/') + "/", configuration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AnimeClickUnavailableException)
        {
            // The state below says it.
        }
        catch (Exception ex)
        {
            // Not the verification: a network fault or an error page. The state stays what it was.
            logger.LogDebug(ex, "AnimeClick manual availability check failed");
            return Ok(AnimeClickSourceStatus.From(
                client.Availability.Snapshot(),
                "AnimeClick non ha risposto alla verifica: " + ex.Message,
                reachable: false));
        }

        var snapshot = client.Availability.Snapshot();
        return Ok(AnimeClickSourceStatus.From(snapshot, reachable: !snapshot.Blocked));
    }
}

/// <summary>What the page shows about AnimeClick.</summary>
public sealed class AnimeClickSourceStatus
{
    internal const string BlockedMessage =
        "AnimeClick sta rifiutando le richieste automatiche con una verifica anti-bot di Cloudflare. "
        + "Il plugin non la supera: finché resta attiva usa le schede già in cache e le altre fonti configurate, e riprova da solo.";

    public bool Blocked { get; set; }

    /// <summary>AnimeClick answered with a real page: the last check, or the last request, reached it.</summary>
    public bool Reachable { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset? BlockedSinceUtc { get; set; }

    public DateTimeOffset? RetryAtUtc { get; set; }

    public DateTimeOffset? LastReachedUtc { get; set; }

    public int? LastBlockedStatus { get; set; }

    internal static AnimeClickSourceStatus From(AnimeClickAvailabilitySnapshot snapshot, string? message = null, bool? reachable = null)
        => new()
        {
            Blocked = snapshot.Blocked,
            Reachable = reachable ?? (!snapshot.Blocked && snapshot.LastReachedUtc is not null),
            Message = message ?? (snapshot.Blocked ? BlockedMessage : "AnimeClick risponde normalmente."),
            BlockedSinceUtc = snapshot.BlockedSinceUtc,
            RetryAtUtc = snapshot.RetryAtUtc,
            LastReachedUtc = snapshot.LastReachedUtc,
            LastBlockedStatus = snapshot.LastBlockedStatus
        };
}

/// <summary>
/// An action that lets <see cref="AnimeClickUnavailableException"/> escape answers 503 with the
/// reason, instead of a bare 500 the page can only call "errore del server".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AnimeClickUnavailableResultAttribute : Microsoft.AspNetCore.Mvc.Filters.ExceptionFilterAttribute
{
    public override void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
    {
        if (context.Exception is not AnimeClickUnavailableException)
        {
            return;
        }

        context.Result = new ObjectResult(new { error = AnimeClickSourceStatus.BlockedMessage, blocked = true })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
        context.ExceptionHandled = true;
    }
}
