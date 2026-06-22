using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Translates English episode synopses to Italian via Ollama Cloud
/// (POST {endpoint} with Authorization: Bearer {apiKey}, model = OllamaCloudModel).
///
/// Best-effort: returns null on any failure so the metadata pipeline never crashes.
/// Results are cached via <see cref="AnimeClickCacheService"/> per episode+model.
/// All network-free helpers (<see cref="StripHtml"/>, <see cref="BuildRequestBody"/>,
/// <see cref="ParseTranslatedContent"/>) are static/internal so they can be unit-tested.
/// </summary>
public class AnimeClickOllamaTranslator
{
    internal const string SystemPrompt =
        "Sei un traduttore professionista da inglese a italiano specializzato in sinossi di anime. "
        + "Traduci la sinossi seguente in italiano naturale, fluido e adatto a un catalogo multimediale. "
        + "Restituisci SOLO la traduzione, senza commenti, note, virgolette aggiunte o testo prima/dopo. "
        + "Mantieni i nomi propri dei personaggi e dei luoghi. Non aggiungere informazioni non presenti nel testo.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickOllamaTranslator> _logger;

    public AnimeClickOllamaTranslator(
        IHttpClientFactory httpClientFactory,
        AnimeClickCacheService cache,
        ILogger<AnimeClickOllamaTranslator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Translates an English synopsis to Italian. Returns null on any failure or empty input.
    /// Cached per (tmdbId, season, episode, model).
    /// </summary>
    public async Task<string?> TranslateSynopsisAsync(
        string englishText,
        int tmdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.OllamaCloudApiKey)
            || string.IsNullOrWhiteSpace(configuration.OllamaCloudModel)
            || string.IsNullOrWhiteSpace(englishText))
        {
            return null;
        }

        var cacheKey = $"episodeSynopsisIT::{tmdbId}::{season}::{episode}::{configuration.OllamaCloudModel}";
        var cached = await _cache.GetAsync<string?>(cacheKey, configuration.CacheHours, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var plain = StripHtml(englishText);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        try
        {
            var body = BuildRequestBody(configuration.OllamaCloudModel, SystemPrompt, plain);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, configuration.EpisodeTranslationTimeoutSec));

            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.OllamaCloudEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + configuration.OllamaCloudApiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("OllamaTranslator: {Endpoint} returned {Status}", configuration.OllamaCloudEndpoint, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var translated = ParseTranslatedContent(json);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return null;
            }

            translated = translated.Trim();
            await _cache.SetAsync(cacheKey, translated, cancellationToken).ConfigureAwait(false);
            return translated;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("OllamaTranslator: translation failed for tmdb={Tmdb} S{S}E{E}: {Message}",
                tmdbId, season, episode, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Strips HTML tags (AniList/TMDB overviews are HTML) and collapses whitespace.
    /// Converts &lt;br&gt; to newlines before stripping so paragraph breaks survive.
    /// </summary>
    internal static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = html
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        // Remove any remaining HTML tags.
        text = Regex.Replace(text, "<[^>]+>", string.Empty, RegexOptions.IgnoreCase);

        // Decode the few HTML entities TMDB/AniList commonly use.
        text = text
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase);

        // Collapse runs of whitespace, preserving single newlines.
        text = Regex.Replace(text, "[ \t\r\f\v]+", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Builds the Ollama /api/chat JSON request body (model, stream:false, system+user messages).
    /// </summary>
    internal static string BuildRequestBody(string model, string systemPrompt, string userContent)
    {
        var payload = new
        {
            model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// Parses the Ollama /api/chat response and extracts message.content. Dependency-free
    /// substring extraction (avoids conflicting System.Text.Json versions with the host).
    /// </summary>
    internal static string? ParseTranslatedContent(string json)
    {
        const string contentMarker = "\"content\":";
        var idx = json.IndexOf(contentMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var i = idx + contentMarker.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r'))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '"')
        {
            return null;
        }

        i++; // skip opening quote
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                sb.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    _ => next
                });
                i += 2;
                continue;
            }

            if (c == '"')
            {
                break;
            }

            sb.Append(c);
            i++;
        }

        var result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}