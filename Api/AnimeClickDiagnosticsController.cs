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
    private readonly AnimeClickEpisodeListLoader _episodeListLoader;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickTmdbClient _tmdbClient;
    private readonly AnimeClickOllamaTranslator _translator;
    private readonly AnimeClickTvdbClient _tvdbClient;
    private readonly AnimeClickMetadataFallbackService _fallbackService;
    private readonly AnimeClickTranslationQueue _translationQueue;

    public AnimeClickDiagnosticsController(
        AnimeClickSeriesSearchProvider searchProvider,
        AnimeClickEpisodeListLoader episodeListLoader,
        AnimeClickCacheService cache,
        AnimeClickTmdbClient tmdbClient,
        AnimeClickOllamaTranslator translator,
        AnimeClickTvdbClient tvdbClient,
        AnimeClickMetadataFallbackService fallbackService,
        AnimeClickTranslationQueue translationQueue)
    {
        _searchProvider = searchProvider;
        _episodeListLoader = episodeListLoader;
        _cache = cache;
        _tmdbClient = tmdbClient;
        _translator = translator;
        _tvdbClient = tvdbClient;
        _fallbackService = fallbackService;
        _translationQueue = translationQueue;
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
        if (!AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, animeClickId, out var animeUrl))
        {
            return BadRequest(new { error = "animeClickId or configured BaseUrl is invalid" });
        }

        var episodesUrl = animeUrl + "/episodi";

        // Reuse the production loader so diagnostics sees the same complete, deduplicated
        // list and applies synthetic seasons only after pagination.
        int? seasonsCount = null;
        var seriesCacheKey = $"anime::{animeUrl}";
        var series = await _cache
            .GetAsync<AnimeClickAnime>(seriesCacheKey, config.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (series is not null && series.SeasonsCount > 0)
        {
            seasonsCount = series.SeasonsCount;
        }

        var loaded = await _episodeListLoader.LoadAsync(
            episodesUrl,
            config.BaseUrl,
            seasonsCount,
            config,
            cancellationToken);
        var episodes = loaded.Episodes;

        AnimeClickEpisodeMatch? match = null;
        if (episode.HasValue)
        {
            match = AnimeClickEpisodeMatcher.Match(episodes, season, episode.Value);
        }

        return Ok(new EpisodesDiagnosticResponse
        {
            AnimeClickId = animeClickId,
            EpisodeCount = episodes.Count,
            PaginationComplete = loaded.PaginationComplete,
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

        var hasKey = !string.IsNullOrWhiteSpace(request.Key);
        var hasPrefix = !string.IsNullOrWhiteSpace(request.Prefix);
        var hasAnimeClickId = !string.IsNullOrWhiteSpace(request.AnimeClickId);
        string? normalizedId = null;
        string? canonicalAnimeUrl = null;

        // Validate every AnimeClick-specific input before deleting any requested key, so a
        // malformed ID/BaseUrl cannot leave a partially-cleared cache and then return 500.
        if (hasAnimeClickId)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!AnimeClickClient.TryNormalizeAnimeClickId(request.AnimeClickId, out normalizedId)
                || !AnimeClickClient.TryBuildAnimeUrl(config.BaseUrl, normalizedId, out canonicalAnimeUrl))
            {
                return BadRequest(new
                {
                    error = "animeClickId or configured BaseUrl is invalid; expected 'number' or 'number/slug'"
                });
            }
        }

        // Hold the queue publication gate for the complete administrative clear,
        // preventing a background translation from becoming visible afterward.
        using var translationInvalidation = _translationQueue.BeginInvalidation();

        if (!hasKey && !hasPrefix && !hasAnimeClickId)
        {
            return Ok(new ClearCacheResponse { Removed = _cache.ClearAll() });
        }

        var removed = 0;
        if (hasKey)
        {
            removed += _cache.ClearKey(request.Key!);
        }

        if (hasPrefix)
        {
            removed += _cache.ClearByPrefix(request.Prefix!);
        }

        if (normalizedId is not null && canonicalAnimeUrl is not null)
        {
            var episodePrefixes = new[] { "episodes:v4::", "episodes:v3::", "episodes:v2::", "episodes::" };
            foreach (var prefix in episodePrefixes)
            {
                removed += _cache.ClearKey(prefix + normalizedId);
                if (!normalizedId.Contains('/', StringComparison.Ordinal))
                {
                    removed += _cache.ClearByPrefix(prefix + normalizedId + "/");
                }
            }

            var seasonPrefixes = new[]
            {
                "seasonMap:v4::",
                "seasonMap:v3::",
                "seasonMap:v2::",
                "seasonMap::"
            };
            foreach (var prefix in seasonPrefixes)
            {
                removed += _cache.ClearByPrefix(prefix + normalizedId + "::");
                if (!normalizedId.Contains('/', StringComparison.Ordinal))
                {
                    removed += _cache.ClearByPrefix(prefix + normalizedId + "/");
                }
            }

            // Clear language-aware source resolution and content-addressed translations
            // associated with this AnimeClick entry. Numeric IDs also clear slug variants.
            var externalIdPrefixes = new[]
            {
                "tmdbTvId:v3::",
                "tvdbSeriesId:v3::",
                "tmdbTvId:v2::",
                "tvdbSeriesId:v2::",
                "tmdbId::",
                "tvdbSeriesId::"
            };
            foreach (var prefix in externalIdPrefixes)
            {
                var mappingKey = prefix + normalizedId;
                removed += _cache.ClearKey(mappingKey);
                removed += _cache.ClearKey(mappingKey + "::miss");
                if (!normalizedId.Contains('/', StringComparison.Ordinal))
                {
                    removed += _cache.ClearByPrefix(prefix + normalizedId + "/");
                }
            }

            var stableAnimeClickId = normalizedId.Split('/', 2)[0];
            removed += _cache.ClearByPrefix("anilistId:v3::" + stableAnimeClickId + "::");
            removed += _cache.ClearByPrefix("anilistId:v2::" + stableAnimeClickId + "::");

            foreach (var translationPrefix in new[] { "translation:v3::", "translation:v2::" })
            {
                removed += _cache.ClearByPrefix(translationPrefix + normalizedId + "::");
                if (!normalizedId.Contains('/', StringComparison.Ordinal))
                {
                    removed += _cache.ClearByPrefix(translationPrefix + normalizedId + "/");
                }
            }

            if (normalizedId.Contains('/', StringComparison.Ordinal))
            {
                removed += _cache.ClearKey("anime::" + canonicalAnimeUrl);
            }
            else
            {
                var lastSlash = canonicalAnimeUrl.LastIndexOf('/');
                var numericAnimePrefix = canonicalAnimeUrl[..(lastSlash + 1)];
                removed += _cache.ClearByPrefix("anime::" + numericAnimePrefix);
            }
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
        if (!TryResolveOllamaProfile(
                request.Endpoint,
                request.ApiKey,
                request.Model,
                config,
                out var endpoint,
                out var apiKey,
                out var model,
                out var profileError))
        {
            return BadRequest(new { error = profileError });
        }

        var timeoutSec = request.TimeoutSec is > 0 ? request.TimeoutSec.Value : config.EpisodeTranslationTimeoutSec;

        var result = await _translator.TestConnectionAsync(
                endpoint,
                apiKey,
                model,
                timeoutSec,
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Produces an EN→IT preview with the same model, prompt, cache and global
    /// concurrency gate used by metadata refreshes. Credentials can be tested before
    /// saving but are never echoed in the response.
    /// </summary>
    [HttpPost("PreviewTranslation")]
    public async Task<ActionResult<TranslationPreviewResponse>> PreviewTranslation(
        [FromBody] TranslationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SourceText))
        {
            return BadRequest(new { error = "sourceText is required" });
        }

        if (request.SourceText.Length > 8000)
        {
            return BadRequest(new { error = "sourceText must not exceed 8000 characters" });
        }

        var stored = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!TryResolveOllamaProfile(
                request.Endpoint,
                request.ApiKey,
                request.Model,
                stored,
                out var endpoint,
                out var apiKey,
                out var model,
                out var profileError))
        {
            return BadRequest(new { error = profileError });
        }

        var effective = new PluginConfiguration
        {
            OllamaCloudEndpoint = endpoint,
            OllamaCloudApiKey = apiKey,
            OllamaCloudModel = model,
            EpisodeTranslationTimeoutSec = request.TimeoutSec is > 0
                ? request.TimeoutSec.Value
                : stored.EpisodeTranslationTimeoutSec,
            TranslationCacheHours = stored.TranslationCacheHours
        };

        var sourceText = request.SourceText.Trim();
        var translated = await _translator.TranslateMetadataFieldAsync(
                sourceText,
                "diagnostics",
                "manual-preview",
                "episode.overview",
                "en",
                "it",
                effective,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(new TranslationPreviewResponse
        {
            Success = !string.IsNullOrWhiteSpace(translated),
            Translation = translated,
            Model = effective.OllamaCloudModel,
            SourceLanguage = "en",
            TargetLanguage = "it",
            SourceCharacterCount = sourceText.Length,
            ErrorMessage = string.IsNullOrWhiteSpace(translated)
                ? "No translation was produced. Run the Ollama connection test for details."
                : null
        });
    }

    /// <summary>
    /// Runs the production episode overview chain and reports the winning source.
    /// The response includes only configuration presence flags, never API keys.
    /// </summary>
    [HttpPost("PreviewEpisodeFallback")]
    public async Task<ActionResult<EpisodeFallbackPreviewResponse>> PreviewEpisodeFallback(
        [FromBody] EpisodeFallbackPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AnimeClickId))
        {
            return BadRequest(new { error = "animeClickId is required" });
        }

        if (request.Season < 0 || request.Episode <= 0)
        {
            return BadRequest(new { error = "season must be >= 0 and episode must be > 0" });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var tvdbConfigured = config.EnableTvdbSynopsis
            && !string.IsNullOrWhiteSpace(config.TvdbApiKey);
        var tmdbConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var ollamaConfigured = !string.IsNullOrWhiteSpace(config.OllamaCloudApiKey)
            && !string.IsNullOrWhiteSpace(config.OllamaCloudEndpoint)
            && !string.IsNullOrWhiteSpace(config.OllamaCloudModel);

        var fallback = await _fallbackService.ResolveEpisodeOverviewAsync(
                request.AnimeClickId,
                request.Season,
                request.Episode,
                config,
                cancellationToken,
                allowSynchronousTranslation: true)
            .ConfigureAwait(false);

        return Ok(new EpisodeFallbackPreviewResponse
        {
            Success = fallback is not null,
            Overview = fallback?.Value,
            Source = fallback?.Source,
            SourceLanguage = fallback?.SourceLanguage,
            UsedOllama = fallback?.UsedOllama ?? false,
            Model = fallback?.Model,
            Chain =
            [
                new FallbackChainStep("TheTVDB", "ita", false, tvdbConfigured),
                new FallbackChainStep("TMDB", "it-IT", false, tmdbConfigured),
                new FallbackChainStep("TMDB", "en-US", true, tmdbConfigured && ollamaConfigured),
                new FallbackChainStep("TheTVDB", "eng", true, tvdbConfigured && ollamaConfigured),
                new FallbackChainStep("Ollama Cloud", "en→it", true, ollamaConfigured)
            ],
            ErrorMessage = fallback is null
                ? "No configured source produced an Italian episode overview."
                : null
        });
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
        var apiKey = request.ApiKey ?? config.TvdbApiKey;

        // Production deliberately probes fixed ita/eng sources. Test the primary
        // production language instead of a UI-only custom value.
        var result = await _tvdbClient.TestConnectionAsync(apiKey, "ita", cancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }

    private static bool TryResolveOllamaProfile(
        string? requestedEndpoint,
        string? requestedApiKey,
        string? requestedModel,
        PluginConfiguration stored,
        out string endpoint,
        out string apiKey,
        out string model,
        out string error)
    {
        var storedEndpointIsValid = AnimeClickOllamaTranslator.TryNormalizeCloudEndpoint(
            stored.OllamaCloudEndpoint,
            out var storedEndpointUri);
        Uri endpointUri;
        if (requestedEndpoint is null)
        {
            if (!storedEndpointIsValid)
            {
                endpoint = string.Empty;
                apiKey = string.Empty;
                model = string.Empty;
                error = "Ollama endpoint must be an absolute HTTPS URL without credentials, query or fragment.";
                return false;
            }

            endpointUri = storedEndpointUri;
        }
        else if (!AnimeClickOllamaTranslator.TryNormalizeCloudEndpoint(requestedEndpoint, out endpointUri))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = "Ollama endpoint must be an absolute HTTPS URL without credentials, query or fragment.";
            return false;
        }

        var endpointChanged = requestedEndpoint is not null
            && (!storedEndpointIsValid || !IsSameOllamaDestination(storedEndpointUri, endpointUri));
        var explicitApiKey = requestedApiKey?.Trim() ?? string.Empty;
        var storedApiKey = stored.OllamaCloudApiKey?.Trim() ?? string.Empty;

        // Endpoint and key are one atomic security profile. A changed destination
        // requires a freshly supplied key and may not reuse the persisted secret.
        if (endpointChanged && string.IsNullOrWhiteSpace(explicitApiKey))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = "An explicit API key is required when changing the Ollama endpoint.";
            return false;
        }

        if (endpointChanged
            && !string.IsNullOrEmpty(storedApiKey)
            && string.Equals(explicitApiKey, storedApiKey, StringComparison.Ordinal))
        {
            endpoint = string.Empty;
            apiKey = string.Empty;
            model = string.Empty;
            error = "The persisted API key cannot be reused with a different Ollama endpoint.";
            return false;
        }

        endpoint = endpointUri.AbsoluteUri;
        apiKey = string.IsNullOrWhiteSpace(explicitApiKey) ? storedApiKey : explicitApiKey;
        model = requestedModel?.Trim() ?? stored.OllamaCloudModel;
        error = string.Empty;
        return true;
    }

    private static bool IsSameOllamaDestination(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal);
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
    public bool PaginationComplete { get; set; }
    public List<EpisodeDiagnosticItem> Episodes { get; set; } = [];
    public string? MatchStrategy { get; set; }
    public EpisodeDiagnosticItem? MatchedEpisode { get; set; }
}

public sealed class EpisodeDiagnosticItem
{
    public int? SeasonNumber { get; set; }
    public bool SeasonNumberIsSynthetic { get; set; }
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
            SeasonNumberIsSynthetic = episode.SeasonNumberIsSynthetic,
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
}


public sealed class TranslationPreviewRequest
{
    public string? SourceText { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSec { get; set; }
}

public sealed class TranslationPreviewResponse
{
    public bool Success { get; set; }
    public string? Translation { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "it";
    public int SourceCharacterCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class EpisodeFallbackPreviewRequest
{
    public string AnimeClickId { get; set; } = string.Empty;
    public int Season { get; set; } = 1;
    public int Episode { get; set; } = 1;
}

public sealed class EpisodeFallbackPreviewResponse
{
    public bool Success { get; set; }
    public string? Overview { get; set; }
    public string? Source { get; set; }
    public string? SourceLanguage { get; set; }
    public bool UsedOllama { get; set; }
    public string? Model { get; set; }
    public List<FallbackChainStep> Chain { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public sealed record FallbackChainStep(
    string Source,
    string Language,
    bool RequiresTranslation,
    bool Configured);