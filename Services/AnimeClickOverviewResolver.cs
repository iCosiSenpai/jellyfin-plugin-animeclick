using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// The Overview a repair may write, plus why nothing was produced when it is empty. The reason is
/// what lets the audit stop re-offering an item no source can fill.
/// </summary>
public sealed record AnimeClickOverviewResolution(
    string? Overview,
    AnimeClickRepairOutcome Outcome,
    string Detail)
{
    public static AnimeClickOverviewResolution Found(string overview, string detail)
        => new(overview, AnimeClickRepairOutcome.Available, detail);

    public static AnimeClickOverviewResolution None(AnimeClickRepairOutcome outcome, string detail)
        => new(null, outcome, detail);
}

public interface IAnimeClickOverviewResolver
{
    Task<AnimeClickOverviewResolution> ResolveAsync(BaseItem item, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves only the Overview value needed by an administrative repair. It deliberately bypasses
/// the broad metadata merge so names, genres, studios and every other field remain untouched.
/// </summary>
public sealed class AnimeClickOverviewResolver : IAnimeClickOverviewResolver
{
    private readonly AnimeClickClient _client;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickMetadataFallbackService _fallbackService;
    private readonly ILogger<AnimeClickOverviewResolver> _logger;

    public AnimeClickOverviewResolver(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        AnimeClickMetadataFallbackService fallbackService,
        ILogger<AnimeClickOverviewResolver> logger)
    {
        _client = client;
        _cache = cache;
        _parser = parser;
        _fallbackService = fallbackService;
        _logger = logger;
    }

    public async Task<AnimeClickOverviewResolution> ResolveAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        try
        {
            return item switch
            {
                Episode episode => await ResolveEpisodeAsync(episode, configuration, cancellationToken)
                    .ConfigureAwait(false),
                Series or Movie => await ResolveAnimeAsync(item, configuration, cancellationToken)
                    .ConfigureAwait(false),
                _ => AnimeClickOverviewResolution.None(
                    AnimeClickRepairOutcome.Disabled,
                    "unsupported-item-type")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AnimeClick Overview-only repair failed for item={ItemId} type={ItemType}",
                item.Id,
                item.GetType().Name);
            return AnimeClickOverviewResolution.None(AnimeClickRepairOutcome.Error, "exception");
        }
    }

    /// <summary>
    /// Translates one of the fallback chain's outcomes into the state the audit stores. Anything
    /// that is not an explicit wait, an explicit switch-off or a thrown error means the chain ran to
    /// the end and found nothing.
    /// </summary>
    internal static AnimeClickRepairOutcome MapFallbackOutcome(string outcome)
        => outcome switch
        {
            "ai-deferred" => AnimeClickRepairOutcome.WaitingTranslation,
            "disabled" => AnimeClickRepairOutcome.Disabled,
            "error" => AnimeClickRepairOutcome.Error,
            _ => AnimeClickRepairOutcome.NoSource
        };

    private async Task<AnimeClickOverviewResolution> ResolveAnimeAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var animeClickId = item.GetProviderId("AnimeClick");
        if (!configuration.EnablePlot)
        {
            return AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.Disabled,
                "plot-disabled");
        }

        if (!AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var animeUrl))
        {
            return AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.NoSource,
                "invalid-animeclick-id");
        }

        var cacheKey = $"anime::{animeUrl}";
        var anime = await _cache
            .GetAsync<AnimeClickAnime>(cacheKey, configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (anime is null)
        {
            var html = await _client.GetStringAsync(animeUrl, configuration, cancellationToken)
                .ConfigureAwait(false);
            anime = _parser.ParseAnimePage(animeUrl, html);
            await _cache.SetAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(anime.Overview)
            ? AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.NoSource,
                "animeclick-card-has-no-plot")
            : AnimeClickOverviewResolution.Found(anime.Overview.Trim(), "native-animeclick");
    }

    private async Task<AnimeClickOverviewResolution> ResolveEpisodeAsync(
        Episode episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableEpisodeSynopsisTranslation)
        {
            return AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.Disabled,
                "episode-synopsis-disabled");
        }

        if (episode.ParentIndexNumber is null or < 0 || episode.IndexNumber is null or < 0)
        {
            return AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.NoSource,
                "episode-has-no-season-or-number");
        }

        var identity = AnimeClickEpisodeIdentity.Resolve(
            episode.Series?.GetProviderId("AnimeClick"),
            episode.Season?.GetProviderId("AnimeClick"));
        var animeClickId = identity.ExternalSourceId ?? identity.MatchingId;
        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return AnimeClickOverviewResolution.None(
                AnimeClickRepairOutcome.NoSource,
                "series-not-identified");
        }

        var seasonNumber = identity.ExternalNumbersRestartAtOne
            ? 1
            : episode.ParentIndexNumber.Value;
        var resolution = await _fallbackService.ResolveEpisodeOverviewDetailedAsync(
                animeClickId,
                seasonNumber,
                episode.IndexNumber.Value,
                episode.GetProviderId("AnimeClick"),
                configuration,
                cancellationToken,
                allowSynchronousTranslation: false,
                episode.Path)
            .ConfigureAwait(false);

        var value = resolution.Result?.Value;
        return string.IsNullOrWhiteSpace(value)
            ? AnimeClickOverviewResolution.None(
                MapFallbackOutcome(resolution.Outcome),
                resolution.Outcome)
            : AnimeClickOverviewResolution.Found(value.Trim(), resolution.Outcome);
    }
}
