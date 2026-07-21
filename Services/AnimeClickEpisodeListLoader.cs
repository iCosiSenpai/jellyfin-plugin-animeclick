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
/// Loads and merges the complete paginated AnimeClick episode table. Both the metadata
/// provider and diagnostics use this service so page deduplication and season inference
/// cannot diverge.
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

    public async Task<AnimeClickEpisodeListLoadResult> LoadAsync(
        string episodesUrl,
        string baseUrl,
        int? seasonsCount,
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
                    // AnimeClick may redirect an out-of-range page to the first/last page.
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

        // Synthetic seasons are valid only for the complete table. A divisible
        // partial page count can otherwise fabricate boundaries and match a
        // different episode during this refresh.
        AnimeClickHtmlParser.FinalizeEpisodeList(
            episodes,
            paginationComplete ? seasonsCount : null);
        return new AnimeClickEpisodeListLoadResult(episodes, paginationComplete);
    }

    private static int MergeUniqueEpisodes(
        List<AnimeClickEpisode> target,
        IEnumerable<AnimeClickEpisode> candidates)
    {
        var added = 0;
        foreach (var candidate in candidates)
        {
            var sameProviderId = !string.IsNullOrWhiteSpace(candidate.ProviderId)
                && target.Any(existing => string.Equals(
                    existing.ProviderId,
                    candidate.ProviderId,
                    StringComparison.OrdinalIgnoreCase));
            var samePosition = target.Any(existing =>
                existing.SeasonNumber == candidate.SeasonNumber
                && existing.AbsoluteNumber == candidate.AbsoluteNumber);

            if (sameProviderId || samePosition)
            {
                continue;
            }

            target.Add(candidate);
            added++;
        }

        return added;
    }
}

public sealed record AnimeClickEpisodeListLoadResult(
    List<AnimeClickEpisode> Episodes,
    bool PaginationComplete);
