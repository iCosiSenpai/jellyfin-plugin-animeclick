using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Loads the complete paginated AnimeClick table and stores only canonical raw rows.
/// Jellyfin-specific season mapping is intentionally evaluated later by the matcher.
/// </summary>
public sealed class AnimeClickEpisodeListLoader
{
    private const int MaxEpisodePages = 100;

    private readonly AnimeClickClient _client;
    private readonly AnimeClickHtmlParser _parser;
    private readonly ILogger<AnimeClickEpisodeListLoader> _logger;

    public AnimeClickEpisodeListLoader(
        AnimeClickClient client,
        AnimeClickHtmlParser parser,
        ILogger<AnimeClickEpisodeListLoader> logger)
    {
        _client = client;
        _parser = parser;
        _logger = logger;
    }

    public Task<AnimeClickEpisodeListLoadResult> LoadAsync(
        string episodesUrl,
        string baseUrl,
        int? seasonsCount,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => LoadAsync(
            episodesUrl,
            baseUrl,
            seasonsCount,
            declaredEpisodeCount: null,
            configuration,
            cancellationToken);

    public async Task<AnimeClickEpisodeListLoadResult> LoadAsync(
        string episodesUrl,
        string baseUrl,
        int? seasonsCount,
        int? declaredEpisodeCount,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("AnimeClick: Fetching episodes from {Url}", episodesUrl);
        var html = await _client.GetStringAsync(episodesUrl, configuration, cancellationToken);
        var episodes = _parser.ParseEpisodesPage(html, baseUrl, seasonsCount: null);
        _logger.LogInformation("AnimeClick: Parsed {Count} episodes from {Url}", episodes.Count, episodesUrl);

        var paginationComplete = false;
        for (var page = 2; page <= MaxEpisodePages; page++)
        {
            var nextUrl = episodesUrl + $"?page={page}";
            try
            {
                var nextHtml = await _client.GetStringAsync(nextUrl, configuration, cancellationToken);
                var nextEpisodes = _parser.ParseEpisodesPage(nextHtml, baseUrl, seasonsCount: null);
                if (nextEpisodes.Count == 0)
                {
                    paginationComplete = true;
                    break;
                }

                var added = MergeUniqueEpisodes(episodes, nextEpisodes);
                if (added == 0)
                {
                    // Out-of-range requests may redirect to the first or last page.
                    paginationComplete = true;
                    break;
                }

                _logger.LogInformation(
                    "AnimeClick: Added {Count} unique episodes from {Url}",
                    added,
                    nextUrl);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                paginationComplete = true;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AnimeClick: stopping episode pagination at {Url}", nextUrl);
                break;
            }
        }

        // Never persist inferred season boundaries. Canonical coordinates are cheap to
        // recompute and remain valid when Jellyfin changes from 1x24 to 13+11 or 12+12.
        AnimeClickHtmlParser.FinalizeEpisodeList(episodes, seasonsCount: null);
        var catalog = AnimeClickEpisodeCatalog.Create(
            episodes,
            declaredEpisodeCount,
            seasonsCount.GetValueOrDefault());
        return new AnimeClickEpisodeListLoadResult(catalog, paginationComplete);
    }

    internal static int MergeUniqueEpisodes(
        List<AnimeClickEpisode> target,
        IEnumerable<AnimeClickEpisode> candidates)
    {
        var added = 0;
        foreach (var candidate in candidates.OrderBy(episode => episode.SourceOrder))
        {
            var sameProviderId = !string.IsNullOrWhiteSpace(candidate.ProviderId)
                && target.Any(existing => string.Equals(
                    existing.ProviderId,
                    candidate.ProviderId,
                    StringComparison.OrdinalIgnoreCase));
            var sameRawRow = string.IsNullOrWhiteSpace(candidate.ProviderId)
                && target.Any(existing =>
                    existing.RawSeasonNumber == candidate.RawSeasonNumber
                    && string.Equals(
                        existing.RawNumberLabel,
                        candidate.RawNumberLabel,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Title, candidate.Title, StringComparison.OrdinalIgnoreCase));

            if (sameProviderId || sameRawRow)
            {
                continue;
            }

            candidate.SourceOrder = target.Count + 1;
            target.Add(candidate);
            added++;
        }

        return added;
    }
}

public sealed record AnimeClickEpisodeListLoadResult(
    AnimeClickEpisodeCatalog Catalog,
    bool PaginationComplete)
{
    public List<AnimeClickEpisode> Episodes => Catalog.Episodes;
}
