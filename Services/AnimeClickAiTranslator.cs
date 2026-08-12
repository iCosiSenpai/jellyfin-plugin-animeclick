using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
/// Translates English metadata into Italian through whichever AI service the user configured —
/// a cloud vendor with an API key, or a server on their own machine.
/// <para>
/// Best-effort by design: it returns null on any failure so the metadata pipeline never breaks over
/// a translation. Results are cached per source text, field, model and endpoint, so a synopsis is
/// translated once and not once per refresh. Every network-free helper is static and internal so the
/// prompt, the request shape and the reply parsing can be tested without a provider.
/// </para>
/// </summary>
public class AnimeClickAiTranslator
{
    internal const string SystemPrompt =
        "Sei un traduttore professionista da inglese a italiano specializzato in sinossi di anime. "
        + "Traduci la sinossi seguente in italiano naturale, fluido e adatto a un catalogo multimediale. "
        + "Restituisci SOLO la traduzione, senza commenti, note, virgolette aggiunte o testo prima/dopo. "
        + "Mantieni i nomi propri dei personaggi e dei luoghi. Non aggiungere informazioni non presenti nel testo.";

    internal const string PromptVersion = "metadata-it-v2";

    /// <summary>Upper bound on the source text sent to the model, in characters.</summary>
    internal const int MaximumSourceCharacters = 8000;

    private const long MaximumResponseBytes = 4 * 1024 * 1024;

    // Some services allow one model at a time on their free tier — Ollama Cloud is one — and a
    // library scan competing with the diagnostics page for that slot fails both. A process-wide
    // gate serialises every call, whoever makes it.
    private static readonly SemaphoreSlim TranslationGate = new(1, 1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickAiTranslator> _logger;

    public AnimeClickAiTranslator(
        IHttpClientFactory httpClientFactory,
        AnimeClickCacheService cache,
        ILogger<AnimeClickAiTranslator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

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
        if (!IsConfigured(configuration, out _)
            || string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        // Cap the prompt: the source is scraped or third-party text of unknown length, and it
        // is both billed and latency-bound at the model. Truncating before the cache key keeps
        // a truncated and an untruncated run from colliding. 8000 characters is the same limit
        // the diagnostics preview endpoint already applies.
        var plain = StripHtml(sourceText);
        if (plain is { Length: > MaximumSourceCharacters })
        {
            plain = plain[..MaximumSourceCharacters];
        }

        if (string.IsNullOrWhiteSpace(plain)
            || !IsConfigured(configuration, out var endpointUri))
        {
            return null;
        }

        var dialect = AnimeClickAiProviders.ResolveDialect(configuration.AiProvider, configuration.AiEndpoint);
        var model = configuration.AiModel.Trim();
        var apiKey = configuration.AiApiKey?.Trim() ?? string.Empty;
        var cacheKey = BuildTranslationCacheKey(
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            model,
            endpointUri.AbsoluteUri,
            apiKey,
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

            var body = AnimeClickAiProviders.BuildRequestBody(dialect, model, SystemPrompt, plain);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(configuration.EpisodeTranslationTimeoutSec, 5, 120));
            client.MaxResponseContentBufferSize = MaximumResponseBytes;

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            ApplyAuthHeaders(request, dialect, apiKey, endpointUri);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Wrong key, exhausted quota or an unknown model all land here, and at Debug
                // they were indistinguishable from "this episode has no synopsis".
                _logger.LogWarning(
                    "AiTranslator: endpoint returned {Status} for field={Field} model={Model}",
                    response.StatusCode,
                    fieldName,
                    model);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (IsResponseTruncated(dialect, json))
            {
                _logger.LogWarning(
                    "AiTranslator: endpoint truncated the translation at its output limit for field={Field} model={Model}",
                    fieldName,
                    model);
                return null;
            }

            var translated = ParseTranslatedContent(
                json,
                AnimeClickAiProviders.ResolveReplyMarker(dialect),
                AnimeClickAiProviders.ResolveReplyAnchor(dialect))?.Trim();
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
            _logger.LogWarning(
                ex,
                "AiTranslator: translation failed for field={Field} source={Source}",
                fieldName,
                sourceIdentity);
            return null;
        }
        finally
        {
            TranslationGate.Release();
        }
    }

    /// <summary>
    /// Accepts an absolute endpoint with no embedded credentials, query or fragment. HTTPS is
    /// required for anything reachable from the internet; plain HTTP is allowed only towards a
    /// machine on the user's own network, which is the whole point of running Ollama locally —
    /// `http://ollama:11434/api/chat` in a compose stack, or a NAS on 192.168.x.x. Demanding TLS
    /// there would have meant either a certificate for a LAN address or no local option at all.
    /// </summary>
    internal static bool TryNormalizeEndpoint(string? endpoint, out Uri endpointUri)
    {
        if (Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps
                || (parsed.Scheme == Uri.UriSchemeHttp && IsPrivateDestination(parsed)))
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

    /// <summary>
    /// True for a host that cannot be reached from outside the user's network: loopback, an
    /// RFC1918 or link-local address, or a name with no dot in it — a container or LAN hostname.
    /// A public name always needs TLS.
    /// </summary>
    internal static bool IsPrivateDestination(Uri endpoint)
    {
        if (endpoint.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(endpoint.IdnHost, out var address))
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
            {
                return true;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                127 => true,

                // 169.254/16 is deliberately excluded even though it is link-local: it is also where
                // cloud providers put their instance metadata service, and nothing about running a
                // model on your own machine needs to reach 169.254.169.254.
                172 => octets[1] is >= 16 and <= 31,
                192 => octets[1] == 168,
                _ => false
            };
        }

        var host = endpoint.IdnHost;
        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the configuration describes a destination that can actually be called: a model, a
    /// valid endpoint, and — for a service that authenticates — a key. A server on the user's own
    /// machine authenticates nothing, so requiring a key there would disable the one option with no
    /// quota and no cloud latency.
    /// </summary>
    internal static bool IsConfigured(PluginConfiguration configuration, out Uri endpointUri)
    {
        endpointUri = null!;
        if (configuration is null
            || string.IsNullOrWhiteSpace(configuration.AiModel)
            || !TryNormalizeEndpoint(configuration.AiEndpoint, out endpointUri))
        {
            return false;
        }

        var preset = AnimeClickAiProviders.Resolve(configuration.AiProvider);
        return !preset.RequiresApiKey
            || !string.IsNullOrWhiteSpace(configuration.AiApiKey);
    }

    /// <summary>
    /// Attaches the credential in the shape the destination expects, and never over plain HTTP: a
    /// token on an unencrypted connection is readable by anything on the same network, and a local
    /// server does not want one anyway.
    /// </summary>
    private void ApplyAuthHeaders(
        HttpRequestMessage request,
        AnimeClickAiDialect dialect,
        string? apiKey,
        Uri endpointUri)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        if (endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning(
                "AiTranslator: the API key was not sent because {Host} is contacted over plain HTTP",
                endpointUri.Host);
            return;
        }

        foreach (var header in AnimeClickAiProviders.BuildAuthHeaders(dialect, apiKey))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
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
        => $"translation:v4::{cacheScope}::{fieldName}::{sourceLanguage}-{targetLanguage}"
            + $"::{ShortHash(sourceIdentity)}::{ShortHash(model)}::{ShortHash(endpoint)}"
            + $"::{ShortHash(apiKey)}::{PromptVersion}::{ShortHash(plainText)}";

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    /// <summary>
    /// Diagnostics-only: sends a trivial prompt to the given profile and reports the sanitized
    /// outcome, so the configuration page can tell "wrong key" from "unknown model" from
    /// "unreachable" without the user reading server logs.
    /// </summary>
    public async Task<AnimeClickAiTestResult> TestConnectionAsync(
        string endpoint,
        string apiKey,
        string model,
        int timeoutSec,
        AnimeClickAiDialect dialect,
        CancellationToken cancellationToken)
    {
        var result = new AnimeClickAiTestResult
        {
            Model = model
        };

        if (!TryNormalizeEndpoint(endpoint, out var endpointUri))
        {
            result.ErrorMessage =
                "L'endpoint deve essere HTTPS — oppure HTTP verso un indirizzo della tua rete — "
                + "senza credenziali, query o frammenti.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            result.ErrorMessage = "Nessun modello indicato: usa «Elenca modelli» e scegline uno.";
            return result;
        }

        const string testSystemPrompt = "You are a test endpoint. Reply with exactly: ok";
        const string testUserContent = "traduci: hello";

        await TranslationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = AnimeClickAiProviders.BuildRequestBody(dialect, model, testSystemPrompt, testUserContent);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 90 : timeoutSec, 5, 120));

            // Same ceiling as the production path. Without it a misconfigured endpoint — or a URL
            // pasted by mistake that points at a large file — is read whole into a string inside the
            // Jellyfin process, and the connection test becomes a way to exhaust its memory.
            client.MaxResponseContentBufferSize = MaximumResponseBytes;

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            ApplyAuthHeaders(request, dialect, apiKey, endpointUri);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            result.StatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"Richiesta non riuscita: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return result;
            }

            if (IsResponseTruncated(dialect, responseBody))
            {
                result.ErrorMessage = "Il servizio ha interrotto la risposta al limite massimo di output.";
                return result;
            }

            result.Reply = ParseTranslatedContent(
                responseBody,
                AnimeClickAiProviders.ResolveReplyMarker(dialect),
                AnimeClickAiProviders.ResolveReplyAnchor(dialect));
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
            result.ErrorMessage = $"Prova di connessione non riuscita ({ex.GetType().Name}).";
            return result;
        }
        finally
        {
            TranslationGate.Release();
        }
    }

    /// <summary>
    /// Asks the service which models the account can actually use.
    /// <para>
    /// This exists because hardcoding model names does not survive contact with reality: vendors
    /// retire and rename them between one release of this plugin and the next, and a stale default
    /// turns into "translation silently stopped working". Asking costs one request and the answer is
    /// always current.
    /// </para>
    /// </summary>
    public async Task<AnimeClickAiModelsResult> ListModelsAsync(
        string modelsEndpoint,
        string apiKey,
        AnimeClickAiDialect dialect,
        int timeoutSec,
        CancellationToken cancellationToken)
    {
        var result = new AnimeClickAiModelsResult();
        if (!TryNormalizeEndpoint(modelsEndpoint, out var endpointUri))
        {
            result.ErrorMessage =
                "Questo servizio non espone un elenco di modelli: scrivi il nome del modello a mano.";
            return result;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 30 : timeoutSec, 5, 120));
            client.MaxResponseContentBufferSize = MaximumResponseBytes;

            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
            ApplyAuthHeaders(request, dialect, apiKey, endpointUri);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            result.StatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"Elenco non disponibile: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            result.Models = ExtractModelNames(json, AnimeClickAiProviders.ResolveModelNameMarker(dialect));
            result.Success = result.Models.Count > 0;
            if (!result.Success)
            {
                result.ErrorMessage = "Il servizio ha risposto senza elencare modelli.";
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Lettura dell'elenco non riuscita ({ex.GetType().Name}).";
            return result;
        }
    }

    /// <summary>
    /// Collects every string value stored under the given key, in order, without duplicates. Kept to
    /// deliberate substring scanning like the reply parser, to avoid deserializing with a
    /// System.Text.Json version that may differ from the host's.
    /// </summary>
    internal static List<string> ExtractModelNames(string json, string marker)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(json))
        {
            return names;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < json.Length)
        {
            var found = json.IndexOf(marker, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            index = found + marker.Length;
            var value = ReadJsonString(json, index);
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                names.Add(value);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
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
    /// Extracts the reply from a response, given the JSON key that holds it: "content" for the
    /// OpenAI and Ollama shapes, "text" for Anthropic's. Deliberately substring-based rather than
    /// deserialized, to avoid pulling in a System.Text.Json version that may conflict with the
    /// host's.
    /// <para>
    /// The search starts after <paramref name="anchor"/> when the response contains it. Taking the
    /// first "content" in the whole document is fragile: a compatible gateway that echoes the request
    /// back alongside its answer puts the system prompt there, and the plugin would write the Italian
    /// translation instructions into the episode's synopsis — a plausible-looking wrong value, the
    /// one outcome this project refuses.
    /// </para>
    /// </summary>
    /// <summary>
    /// Anthropic reports a response cut at the requested ceiling with stop_reason=max_tokens.
    /// Such text can look grammatical while ending mid-synopsis, so it must never enter cache.
    /// </summary>
    internal static bool IsResponseTruncated(AnimeClickAiDialect dialect, string json)
    {
        if (dialect != AnimeClickAiDialect.Anthropic || string.IsNullOrEmpty(json))
        {
            return false;
        }

        const string marker = "\"stop_reason\":";
        var markerAt = json.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0)
        {
            return false;
        }

        var reason = ReadJsonString(json, markerAt + marker.Length);
        return string.Equals(reason, "max_tokens", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ParseTranslatedContent(
        string json,
        string marker = "\"content\":",
        string? anchor = null)
    {
        var from = 0;
        if (!string.IsNullOrEmpty(anchor))
        {
            var anchorAt = json.IndexOf(anchor, StringComparison.Ordinal);
            if (anchorAt >= 0)
            {
                from = anchorAt + anchor.Length;
            }
        }

        var idx = json.IndexOf(marker, from, StringComparison.Ordinal);
        return idx < 0 ? null : ReadJsonString(json, idx + marker.Length);
    }

    /// <summary>
    /// Reads one JSON string starting at <paramref name="start"/>, honouring the escapes a model
    /// reply actually contains — including surrogate pairs, because an emoji or a Japanese title in
    /// a synopsis arrives as \uD83D\uDE00.
    /// </summary>
    private static string? ReadJsonString(string json, int start)
    {
        var i = start;
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

/// <summary>Sanitized outcome of a connection test, shown by the configuration page.</summary>
public sealed class AnimeClickAiTestResult
{
    public bool Success { get; set; }
    public string Model { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Reply { get; set; }
}

/// <summary>The models a service reports for the configured credential.</summary>
public sealed class AnimeClickAiModelsResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Models { get; set; } = [];
}