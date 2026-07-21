using System;
using System.Net.Http;
using System.Security.Cryptography;
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

    internal const string PromptVersion = "metadata-it-v2";

    // Ollama Free allows one cloud model at a time. A process-wide gate prevents
    // scans and diagnostics from competing for the same account slot.
    private static readonly SemaphoreSlim TranslationGate = new(1, 1);

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
    /// Compatibility wrapper for episode synopsis translation.
    /// </summary>
    public Task<string?> TranslateSynopsisAsync(
        string englishText,
        int tmdbId,
        int season,
        int episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => TranslateMetadataFieldAsync(
            englishText,
            $"tmdb-{tmdbId}",
            $"tmdb:tv:{tmdbId}:s{season}:e{episode}",
            "episode.overview",
            "en",
            "it",
            configuration,
            cancellationToken);

    /// <summary>
    /// Translates one metadata field and caches it by source provider/id, field,
    /// languages, model, endpoint, prompt version and source-text hash.
    /// </summary>
    public Task<string?> TranslateMetadataFieldAsync(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => TranslateMetadataFieldCoreAsync(
            sourceText,
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            configuration,
            publishToCache: true,
            cancellationToken);

    internal Task<string?> TranslateMetadataFieldWithoutPublishingAsync(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
        => TranslateMetadataFieldCoreAsync(
            sourceText,
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            configuration,
            publishToCache: false,
            cancellationToken);

    private async Task<string?> TranslateMetadataFieldCoreAsync(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        bool publishToCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.OllamaCloudApiKey)
            || string.IsNullOrWhiteSpace(configuration.OllamaCloudModel)
            || string.IsNullOrWhiteSpace(configuration.OllamaCloudEndpoint)
            || string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        var plain = StripHtml(sourceText);
        if (string.IsNullOrWhiteSpace(plain)
            || !TryNormalizeCloudEndpoint(configuration.OllamaCloudEndpoint, out var endpointUri))
        {
            return null;
        }

        var cacheKey = BuildTranslationCacheKey(
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            configuration.OllamaCloudModel,
            endpointUri.AbsoluteUri,
            configuration.OllamaCloudApiKey,
            plain);
        var cacheHours = configuration.TranslationCacheHours <= 0
            ? int.MaxValue
            : configuration.TranslationCacheHours;
        var cached = await _cache
            .GetAsync<string?>(cacheKey, cacheHours, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await TranslationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after waiting: another refresh may have populated it.
            cached = await _cache
                .GetAsync<string?>(cacheKey, cacheHours, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var body = BuildRequestBody(configuration.OllamaCloudModel, SystemPrompt, plain);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(configuration.EpisodeTranslationTimeoutSec, 5, 120));

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + configuration.OllamaCloudApiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "OllamaTranslator: endpoint returned {Status} for field={Field} model={Model}",
                    response.StatusCode,
                    fieldName,
                    configuration.OllamaCloudModel);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var translated = ParseTranslatedContent(json)?.Trim();
            if (string.IsNullOrWhiteSpace(translated))
            {
                return null;
            }

            if (publishToCache)
            {
                await _cache.SetAsync(cacheKey, translated, cancellationToken).ConfigureAwait(false);
            }

            return translated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "OllamaTranslator: translation failed for field={Field} source={Source}: {Message}",
                fieldName,
                sourceIdentity,
                ex.Message);
            return null;
        }
        finally
        {
            TranslationGate.Release();
        }
    }

    /// <summary>
    /// Accepts only absolute HTTPS endpoints without embedded credentials, query
    /// parameters or fragments. Custom cloud hosts remain possible, but a key can
    /// never be hidden in the destination URL.
    /// </summary>
    internal static bool TryNormalizeCloudEndpoint(string? endpoint, out Uri endpointUri)
    {
        if (Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(parsed.Host)
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment))
        {
            endpointUri = parsed;
            return true;
        }

        endpointUri = null!;
        return false;
    }

    internal static string BuildTranslationCacheKey(
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        string model,
        string endpoint,
        string apiKey,
        string plainText)
        => $"translation:v3::{cacheScope}::{fieldName}::{sourceLanguage}-{targetLanguage}"
            + $"::{ShortHash(sourceIdentity)}::{ShortHash(model)}::{ShortHash(endpoint)}"
            + $"::{ShortHash(apiKey)}::{PromptVersion}::{ShortHash(plainText)}";

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    /// <summary>
    /// Diagnostics-only: validates the Ollama Cloud profile with a trivial
    /// prompt and returns only sanitized status and model information.
    /// </summary>
    public async Task<OllamaTestResult> TestConnectionAsync(
        string endpoint,
        string apiKey,
        string model,
        int timeoutSec,
        CancellationToken cancellationToken)
    {
        var result = new OllamaTestResult
        {
            Model = model
        };

        if (!TryNormalizeCloudEndpoint(endpoint, out var endpointUri))
        {
            result.ErrorMessage = "Ollama endpoint must be an absolute HTTPS URL without credentials, query or fragment.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.ErrorMessage = "Ollama API key is empty.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            result.ErrorMessage = "Ollama model is empty.";
            return result;
        }

        const string testSystemPrompt = "You are a test endpoint. Reply with exactly: ok";
        const string testUserContent = "traduci: hello";

        await TranslationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = BuildRequestBody(model, testSystemPrompt, testUserContent);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 30 : timeoutSec, 5, 120));

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            result.StatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"Request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return result;
            }

            result.Reply = ParseTranslatedContent(responseBody);
            result.Success = !string.IsNullOrWhiteSpace(result.Reply);

            if (!result.Success)
            {
                result.ErrorMessage = "Request succeeded but the response contained no message.content.";
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Connection test failed ({ex.GetType().Name}).";
            return result;
        }
        finally
        {
            TranslationGate.Release();
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
            think = false,
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
                if (next == 'u' && i + 5 < json.Length
                    && TryParseHex4(json.AsSpan(i + 2, 4), out var cp))
                {
                    if (cp >= 0xD800 && cp <= 0xDBFF
                        && i + 11 < json.Length
                        && json[i + 6] == '\\' && json[i + 7] == 'u'
                        && TryParseHex4(json.AsSpan(i + 8, 4), out var loSu)
                        && loSu >= 0xDC00 && loSu <= 0xDFFF)
                    {
                        var supplementary = 0x10000 + ((cp - 0xD800) << 10) + (loSu - 0xDC00);
                        sb.Append(char.ConvertFromUtf32(supplementary));
                        i += 12;
                        continue;
                    }

                    if (cp >= 0xD800 && cp <= 0xDFFF)
                    {
                        // lone surrogate — emit replacement char to avoid crashing
                        sb.Append('\uFFFD');
                    }
                    else
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                    }
                    i += 6;
                    continue;
                }

                if (next == 'U' && i + 9 < json.Length
                    && TryParseHex4(json.AsSpan(i + 2, 4), out var hi)
                    && TryParseHex4(json.AsSpan(i + 6, 4), out var lo)
                    && hi >= 0xD800 && hi <= 0xDBFF
                    && lo >= 0xDC00 && lo <= 0xDFFF)
                {
                    var supplementary = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                    sb.Append(char.ConvertFromUtf32(supplementary));
                    i += 10;
                    continue;
                }

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

    private static bool TryParseHex4(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        for (var k = 0; k < 4; k++)
        {
            var ch = s[k];
            int d;
            if (ch >= '0' && ch <= '9') { d = ch - '0'; }
            else if (ch >= 'a' && ch <= 'f') { d = ch - 'a' + 10; }
            else if (ch >= 'A' && ch <= 'F') { d = ch - 'A' + 10; }
            else { return false; }
            value = (value << 4) | d;
        }
        return true;
    }
}

/// <summary>Detailed result of an Ollama Cloud connection test (used by the diagnostics UI).</summary>
public sealed class OllamaTestResult
{
    public bool Success { get; set; }
    public string Model { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Reply { get; set; }
}