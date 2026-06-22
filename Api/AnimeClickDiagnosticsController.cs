using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnimeClick.Plugin.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AnimeClick")]
public class AnimeClickDiagnosticsController : ControllerBase
{
    private readonly AnimeClickSeriesSearchProvider _searchProvider;
    private readonly AnimeClickClient _client;
    private readonly AnimeClickHtmlParser _parser;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickTmdbClient _tmdbClient;
    private readonly AnimeClickOllamaTranslator _translator;
    private readonly AnimeClickTvdbClient _tvdbClient;

    public AnimeClickDiagnosticsController(
        AnimeClickSeriesSearchProvider searchProvider,
        AnimeClickClient client,
        AnimeClickHtmlParser parser,
        AnimeClickCacheService cache,
        AnimeClickTmdbClient tmdbClient,
        AnimeClickOllamaTranslator translator,
        AnimeClickTvdbClient tvdbClient)
    {
        _searchProvider = searchProvider;
        _client = client;
        _parser = parser;
        _cache = cache;
        _tmdbClient = tmdbClient;
        _translator = translator;
        _tvdbClient = tvdbClient;
    }

    [HttpGet("TestLookup")]
    public async Task<ActionResult<IEnumerable<LookupDiagnosticResponse>>> TestLookup(
        [FromQuery] string name,
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var results = await _searchProvider.SearchAsync(name, config, cancellationToken, year, seriesRequest: true);

        return Ok(results.Select(r => new LookupDiagnosticResponse
        {
            Name = r.Name,
            Year = r.ProductionYear,
            ImageUrl = r.ImageUrl,
            AnimeClickId = r.ProviderIds.TryGetValue("AnimeClick", out var id) ? id : null
        }).ToList());
    }

    [HttpGet("TestEpisodes")]
    public async Task<ActionResult<EpisodesDiagnosticResponse>> TestEpisodes(
        [FromQuery] string animeClickId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animeClickId))
        {
            return BadRequest(new { error = "animeClickId is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var animeUrl = AnimeClickClient.BuildAnimeUrl(config.BaseUrl, animeClickId);
        var episodesUrl = animeUrl + "/episodi";
        var html = await _client.GetStringAsync(episodesUrl, config, cancellationToken);
        var episodes = _parser.ParseEpisodesPage(html, config.BaseUrl);

        AnimeClickEpisodeMatch? match = null;
        if (episode.HasValue)
        {
            match = AnimeClickEpisodeMatcher.Match(episodes, season, episode.Value);
        }

        return Ok(new EpisodesDiagnosticResponse
        {
            AnimeClickId = animeClickId,
            EpisodeCount = episodes.Count,
            Episodes = episodes.Select(EpisodeDiagnosticItem.From).ToList(),
            MatchStrategy = match?.Strategy,
            MatchedEpisode = match?.Episode is null ? null : EpisodeDiagnosticItem.From(match.Episode)
        });
    }

    [HttpPost("ClearCache")]
    public ActionResult<ClearCacheResponse> ClearCache([FromBody] ClearCacheRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        // Corpo vuoto (nessun Key/Prefix/AnimeClickId) = svuota l'intera cache metadati,
        // usato dal pulsante "Svuota cache metadati" della config page.
        var removed = 0;
        var hasKey = !string.IsNullOrWhiteSpace(request.Key);
        var hasPrefix = !string.IsNullOrWhiteSpace(request.Prefix);
        var hasAnimeClickId = !string.IsNullOrWhiteSpace(request.AnimeClickId);

        if (!hasKey && !hasPrefix && !hasAnimeClickId)
        {
            removed += _cache.ClearAll();
            return Ok(new ClearCacheResponse { Removed = removed });
        }

        if (hasKey)
        {
            removed += _cache.ClearKey(request.Key!);
        }

        if (hasPrefix)
        {
            removed += _cache.ClearByPrefix(request.Prefix!);
        }

        if (hasAnimeClickId)
        {
            removed += _cache.ClearByPrefix($"episodes:v2::{request.AnimeClickId}");
            removed += _cache.ClearByPrefix($"episodes::{request.AnimeClickId}");
            removed += _cache.ClearByPrefix($"seasonMap:v2::{request.AnimeClickId}");
            removed += _cache.ClearByPrefix($"seasonMap::{request.AnimeClickId}");
            removed += _cache.ClearByPrefix($"anime::{AnimeClickClient.BuildAnimeUrl((Plugin.Instance?.Configuration ?? new PluginConfiguration()).BaseUrl, request.AnimeClickId!)}");
        }

        return Ok(new ClearCacheResponse { Removed = removed });
    }

    /// <summary>
    /// Validates the TMDB API key (as currently entered in the form) by running a
    /// search/tv with a known query. Returns a detailed result for the diagnostics UI.
    /// </summary>
    [HttpPost("TestTmdb")]
    public async Task<ActionResult<TmdbTestResult>> TestTmdb(
        [FromBody] TestTmdbRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var apiKey = request.ApiKey ?? (Plugin.Instance?.Configuration ?? new PluginConfiguration()).TmdbApiKey;
        var result = await _tmdbClient.TestConnectionAsync(apiKey, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Validates the Ollama Cloud endpoint + key + model (as currently entered in the
    /// form) by sending a trivial test prompt. Returns a detailed result.
    /// </summary>
    [HttpPost("TestOllama")]
    public async Task<ActionResult<OllamaTestResult>> TestOllama(
        [FromBody] TestOllamaRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? config.OllamaCloudEndpoint : request.Endpoint;
        var apiKey = string.IsNullOrEmpty(request.ApiKey) ? config.OllamaCloudApiKey : request.ApiKey;
        var model = string.IsNullOrWhiteSpace(request.Model) ? config.OllamaCloudModel : request.Model;
        var timeoutSec = request.TimeoutSec > 0 ? request.TimeoutSec.Value : config.EpisodeTranslationTimeoutSec;

        var result = await _translator.TestConnectionAsync(endpoint!, apiKey, model, timeoutSec, cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Validates the TheTVDB API key (as currently entered in the form) by logging in
    /// and running a series search. Returns a detailed result.
    /// </summary>
    [HttpPost("TestTvdb")]
    public async Task<ActionResult<TvdbTestResult>> TestTvdb(
        [FromBody] TestTvdbRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "request body is required" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var apiKey = string.IsNullOrEmpty(request.ApiKey) ? config.TvdbApiKey : request.ApiKey;
        var language = string.IsNullOrWhiteSpace(request.Language) ? config.TvdbLanguage : request.Language;

        var result = await _tvdbClient.TestConnectionAsync(apiKey, language!, cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }
}

public sealed class LookupDiagnosticResponse
{
    public string? Name { get; set; }
    public int? Year { get; set; }
    public string? ImageUrl { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class EpisodesDiagnosticResponse
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int EpisodeCount { get; set; }
    public List<EpisodeDiagnosticItem> Episodes { get; set; } = [];
    public string? MatchStrategy { get; set; }
    public EpisodeDiagnosticItem? MatchedEpisode { get; set; }
}

public sealed class EpisodeDiagnosticItem
{
    public int? SeasonNumber { get; set; }
    public int Number { get; set; }
    public int AbsoluteNumber { get; set; }
    public int SeasonOrdinalNumber { get; set; }
    public string? Title { get; set; }
    public string? ProviderId { get; set; }
    public string? DetailUrl { get; set; }

    public static EpisodeDiagnosticItem From(AnimeClickEpisode episode)
        => new()
        {
            SeasonNumber = episode.SeasonNumber,
            Number = episode.Number,
            AbsoluteNumber = episode.AbsoluteNumber,
            SeasonOrdinalNumber = episode.SeasonOrdinalNumber,
            Title = episode.Title,
            ProviderId = episode.ProviderId,
            DetailUrl = episode.DetailUrl
        };
}

public sealed class ClearCacheRequest
{
    public string? Key { get; set; }
    public string? Prefix { get; set; }
    public string? AnimeClickId { get; set; }
}

public sealed class ClearCacheResponse
{
    public int Removed { get; set; }
}

public sealed class TestTmdbRequest
{
    public string? ApiKey { get; set; }
}

public sealed class TestOllamaRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TestTvdbRequest
{
    public string? ApiKey { get; set; }
    public string? Language { get; set; }
}
