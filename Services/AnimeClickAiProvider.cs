using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// The request and response shape a destination speaks. Three cover the whole market: nearly every
/// vendor exposes an OpenAI-compatible chat endpoint, Anthropic keeps its own Messages format, and
/// Ollama has its native one.
/// </summary>
public enum AnimeClickAiDialect
{
    /// <summary>POST /v1/chat/completions, bearer token, reply in choices[0].message.content.</summary>
    OpenAi,

    /// <summary>POST /v1/messages, x-api-key header, reply in content[0].text.</summary>
    Anthropic,

    /// <summary>POST /api/chat, bearer token, reply in message.content.</summary>
    Ollama
}

/// <summary>
/// One selectable destination: where to send the request, how to speak to it, and where its model
/// list lives.
/// <para>
/// Model names are deliberately not hardcoded. Between one release of this plugin and the next the
/// vendors retire and rename models, and a stale default becomes a support ticket — so the plugin
/// asks the provider for the list instead of pretending to know it.
/// </para>
/// </summary>
public sealed record AnimeClickAiPreset(
    string Id,
    string DisplayName,
    string ChatEndpoint,
    string ModelsEndpoint,
    AnimeClickAiDialect Dialect,
    bool RequiresApiKey,
    string CredentialUrl,
    string Note);

/// <summary>
/// The catalogue of destinations the translation layer can use, and the format-specific bits that
/// differ between them. Adding a vendor is a row in <see cref="Presets"/> whenever it speaks one of
/// the three dialects, which nearly all of them do.
/// </summary>
public static class AnimeClickAiProviders
{
    /// <summary>Identifier of the free-form entry, kept stable because it is persisted.</summary>
    public const string CustomId = "custom";

    /// <summary>Header version Anthropic requires on every Messages call.</summary>
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// Anthropic requires an explicit output ceiling. The input is capped at eight thousand
    /// characters, so 4096 tokens leave ample room for an Italian translation while remaining
    /// compatible with older/lower-cap models that can still appear in the selectable model list.
    /// Responses stopped at this limit are rejected by the translator rather than cached truncated.
    /// </summary>
    private const int MaxOutputTokens = 4096;

    private static readonly ReadOnlyCollection<AnimeClickAiPreset> PresetList = new(
    [
        new AnimeClickAiPreset(
            "ollama-cloud",
            "Ollama Cloud",
            "https://ollama.com/api/chat",
            "https://ollama.com/api/tags",
            AnimeClickAiDialect.Ollama,
            RequiresApiKey: true,
            "https://ollama.com/settings/keys",
            "Modelli con il suffisso -cloud. Un solo modello per volta sul piano gratuito."),
        new AnimeClickAiPreset(
            "ollama-local",
            "Ollama in casa",
            "http://127.0.0.1:11434/api/chat",
            "http://127.0.0.1:11434/api/tags",
            AnimeClickAiDialect.Ollama,
            RequiresApiKey: false,
            "https://ollama.com/download",
            "Nessuna chiave e nessuna quota. Se il demone ha già fatto «ollama signin», usa lui "
            + "l'abbonamento e Jellyfin non vede alcuna credenziale."),
        new AnimeClickAiPreset(
            "openai",
            "OpenAI",
            "https://api.openai.com/v1/chat/completions",
            "https://api.openai.com/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://platform.openai.com/api-keys",
            "Chiave della piattaforma API, a consumo. L'abbonamento ChatGPT non la include."),
        new AnimeClickAiPreset(
            "anthropic",
            "Anthropic Claude",
            "https://api.anthropic.com/v1/messages",
            "https://api.anthropic.com/v1/models",
            AnimeClickAiDialect.Anthropic,
            RequiresApiKey: true,
            "https://platform.claude.com/settings/keys",
            "Chiave della console, a consumo. L'abbonamento Claude non è utilizzabile qui."),
        new AnimeClickAiPreset(
            "gemini",
            "Google Gemini",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            "https://generativelanguage.googleapis.com/v1beta/openai/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://aistudio.google.com/apikey",
            "Endpoint compatibile OpenAI ufficiale di Google. Ha un piano gratuito con limiti."),
        new AnimeClickAiPreset(
            "mistral",
            "Mistral",
            "https://api.mistral.ai/v1/chat/completions",
            "https://api.mistral.ai/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://console.mistral.ai/api-keys",
            "Buon rapporto qualità/prezzo sulle lingue europee."),
        new AnimeClickAiPreset(
            "groq",
            "Groq",
            "https://api.groq.com/openai/v1/chat/completions",
            "https://api.groq.com/openai/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://console.groq.com/keys",
            "Molto rapido: utile quando le sinossi da tradurre sono tante."),
        new AnimeClickAiPreset(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com/chat/completions",
            "https://api.deepseek.com/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://platform.deepseek.com/api_keys",
            "Tra i più economici a parità di risultato su testi brevi."),
        new AnimeClickAiPreset(
            "openrouter",
            "OpenRouter",
            "https://openrouter.ai/api/v1/chat/completions",
            "https://openrouter.ai/api/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://openrouter.ai/keys",
            "Una sola chiave per i modelli di molti fornitori, anche gratuiti."),
        new AnimeClickAiPreset(
            "together",
            "Together AI",
            "https://api.together.xyz/v1/chat/completions",
            "https://api.together.xyz/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://api.together.ai/settings/api-keys",
            "Modelli aperti ospitati, a consumo."),
        new AnimeClickAiPreset(
            "xai",
            "xAI Grok",
            "https://api.x.ai/v1/chat/completions",
            "https://api.x.ai/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: true,
            "https://console.x.ai/",
            "Chiave della console xAI, a consumo."),
        new AnimeClickAiPreset(
            "lmstudio",
            "LM Studio in casa",
            "http://127.0.0.1:1234/v1/chat/completions",
            "http://127.0.0.1:1234/v1/models",
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: false,
            "https://lmstudio.ai/",
            "Server locale di LM Studio, da avviare con l'opzione server attiva."),
        new AnimeClickAiPreset(
            CustomId,
            "Personalizzato (compatibile OpenAI)",
            string.Empty,
            string.Empty,
            AnimeClickAiDialect.OpenAi,
            RequiresApiKey: false,
            string.Empty,
            "Per qualunque altro servizio o gateway compatibile OpenAI: LiteLLM, vLLM, llama.cpp, "
            + "Cerebras, Fireworks, Perplexity, un proxy aziendale.")
    ]);

    /// <summary>Every selectable destination, in the order the interface shows them.</summary>
    public static IReadOnlyList<AnimeClickAiPreset> Presets => PresetList;

    /// <summary>
    /// The preset with this identifier, or the free-form one. Never null: an unknown value in a
    /// persisted configuration must not disable translation.
    /// </summary>
    public static AnimeClickAiPreset Resolve(string? id)
        => PresetList.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? PresetList.First(preset => preset.Id == CustomId);

    /// <summary>
    /// The dialect to speak to an endpoint. The stored preset decides, except for the free-form
    /// entry, where the path is the only clue — and the two non-OpenAI shapes have recognisable
    /// ones, so a user who types an Ollama or Anthropic URL under "Personalizzato" still works.
    /// </summary>
    public static AnimeClickAiDialect ResolveDialect(string? presetId, string? endpoint)
    {
        var preset = Resolve(presetId);
        if (preset.Id != CustomId)
        {
            return preset.Dialect;
        }

        var path = endpoint ?? string.Empty;
        if (path.Contains("/api/chat", StringComparison.OrdinalIgnoreCase))
        {
            return AnimeClickAiDialect.Ollama;
        }

        return path.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase)
            ? AnimeClickAiDialect.Anthropic
            : AnimeClickAiDialect.OpenAi;
    }

    /// <summary>
    /// Where to ask for the available models. Presets carry their own; a custom endpoint is derived
    /// from its chat path, which is a stable convention across compatible providers.
    /// </summary>
    public static string ResolveModelsEndpoint(string? presetId, string? chatEndpoint)
    {
        var preset = Resolve(presetId);
        if (!string.IsNullOrWhiteSpace(preset.ModelsEndpoint))
        {
            return preset.ModelsEndpoint;
        }

        var endpoint = (chatEndpoint ?? string.Empty).Trim();
        if (endpoint.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint[..^"/api/chat".Length] + "/api/tags";
        }

        var marker = endpoint.LastIndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? endpoint[..marker] + "/models" : string.Empty;
    }

    /// <summary>
    /// The authentication headers for a destination. Anthropic uses its own header pair; everyone
    /// else takes a bearer token. An empty key yields no header, which is what a local server wants.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> BuildAuthHeaders(
        AnimeClickAiDialect dialect,
        string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield break;
        }

        if (dialect == AnimeClickAiDialect.Anthropic)
        {
            yield return new KeyValuePair<string, string>("x-api-key", apiKey);
            yield return new KeyValuePair<string, string>("anthropic-version", AnthropicVersion);
            yield break;
        }

        yield return new KeyValuePair<string, string>("Authorization", "Bearer " + apiKey);
    }

    /// <summary>Builds the request body in the shape the destination expects.</summary>
    public static string BuildRequestBody(
        AnimeClickAiDialect dialect,
        string model,
        string systemPrompt,
        string userContent)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        object payload = dialect switch
        {
            AnimeClickAiDialect.Anthropic => new
            {
                model,
                max_tokens = MaxOutputTokens,
                system = systemPrompt,
                messages = new[] { new { role = "user", content = userContent } }
            },
            AnimeClickAiDialect.Ollama => new
            {
                model,
                stream = false,

                // Reasoning models on Ollama otherwise spend the budget thinking about a translation.
                think = false,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            },
            _ => new
            {
                model,
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            }
        };

        return JsonSerializer.Serialize(payload, options);
    }

    /// <summary>
    /// The JSON key that holds the reply. OpenAI nests it in choices[0].message.content and Ollama
    /// in message.content — both the first "content" in the document — while Anthropic returns a
    /// content array whose first entry carries "text".
    /// </summary>
    public static string ResolveReplyMarker(AnimeClickAiDialect dialect)
        => dialect == AnimeClickAiDialect.Anthropic ? "\"text\":" : "\"content\":";

    /// <summary>
    /// The key that precedes the reply in the response, when the shape has one. Searching after it
    /// is what keeps a gateway that echoes the request from having its copy of the system prompt read
    /// as the answer. Ollama's shape has no such wrapper, so it gets none.
    /// </summary>
    public static string? ResolveReplyAnchor(AnimeClickAiDialect dialect) => dialect switch
    {
        AnimeClickAiDialect.OpenAi => "\"choices\":",
        AnimeClickAiDialect.Anthropic => "\"content\":",
        _ => null
    };

    /// <summary>
    /// The JSON key that names a model in a listing: Ollama's /api/tags uses "name", every
    /// OpenAI-compatible /models and Anthropic's own use "id".
    /// </summary>
    public static string ResolveModelNameMarker(AnimeClickAiDialect dialect)
        => dialect == AnimeClickAiDialect.Ollama ? "\"name\":" : "\"id\":";
}
