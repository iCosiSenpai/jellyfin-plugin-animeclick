using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnimeClick.Plugin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnimeClick.Plugin.Services;

/// <summary>Opt-in public ID exchange. Only the fixed project repository receives credentials.</summary>
public sealed class AnimeClickCommunityService(IHttpClientFactory factory, AnimeClickCacheService cache,
    Func<PluginConfiguration>? configuration = null, IApplicationPaths? paths = null) : BackgroundService
{
    private readonly Func<PluginConfiguration> _configuration = configuration ?? (() => Plugin.Instance?.Configuration ?? new PluginConfiguration());
    // Outbox and publication receipts are durable data, separate from administrative cache clears.
    private readonly AnimeClickCacheService _state = paths is null ? cache : new AnimeClickCacheService(paths,
        NullLogger<AnimeClickCacheService>.Instance, Path.Combine(paths.DataPath, "AnimeClickCommunity"));
    public const string Repository = "iCosiSenpai/jellyfin-plugin-animeclick";
    private const string OutboxKey = "community::outbox:v1";
    private const string DatasetKey = "community::approved:v1";
    private const int MaximumBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextDatasetAttempt;
    private const string DisabledMessage = "La condivisione è disattivata.";
    private string _message = DisabledMessage;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AnimeClickCommunityMapping? BuildMapping(BaseItem item)
    {
        if (item is not (Series or Movie)) return null;
        if (!AnimeClickClient.TryNormalizeAnimeClickId(item.GetProviderId("AnimeClick"), out var id)) return null;
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "Tmdb", "Tvdb", "AniList" })
            if (NormalizePublicId(item.GetProviderId(key)) is { } externalId) ids[key] = externalId;
        return ids.Count == 0 ? null : new AnimeClickCommunityMapping
        {
            Kind = item is Movie ? "Movie" : "Series", AnimeClickId = id.Split('/')[0], ProviderIds = ids
        };
    }

    internal static string? NormalizePublicId(string? value)
        => value is { Length: > 0 and <= 10 } && value.All(char.IsAsciiDigit)
            && long.TryParse(value, out var id) && id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    internal static AnimeClickCommunityDataset ParseDataset(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new JsonException("Dataset too large");
        var dataset = JsonSerializer.Deserialize<AnimeClickCommunityDataset>(json, JsonOptions) ?? throw new JsonException();
        if (dataset.SchemaVersion != 1 || dataset.Mappings is null || dataset.Mappings.Count > 5000) throw new JsonException();
        foreach (var mapping in dataset.Mappings)
        {
            if (mapping is null || mapping.Kind is not ("Series" or "Movie")
                || NormalizePublicId(mapping.AnimeClickId) != mapping.AnimeClickId
                || mapping.ProviderIds is not { Count: > 0 and <= 3 }
                || mapping.ProviderIds.Any(pair => pair.Key is not ("Tmdb" or "Tvdb" or "AniList")
                    || NormalizePublicId(pair.Value) != pair.Value)) throw new JsonException("Invalid public mapping");
        }
        return dataset;
    }

    internal static string? Match(AnimeClickCommunityDataset dataset, string kind, IReadOnlyDictionary<string, string> ids)
    {
        var publicIds = ids.Where(pair => pair.Key is "Tmdb" or "Tvdb" or "AniList")
            .Where(pair => NormalizePublicId(pair.Value) is not null)
            .ToDictionary(pair => pair.Key, pair => NormalizePublicId(pair.Value)!, StringComparer.OrdinalIgnoreCase);
        // Any conflicting known identity prevents a community suggestion from overriding evidence.
        var candidates = dataset.Mappings.Where(mapping => mapping.Kind == kind
            && mapping.ProviderIds.Any(pair => publicIds.TryGetValue(pair.Key, out var id) && id == pair.Value)).ToList();
        if (candidates.Any(mapping => mapping.ProviderIds.Any(pair => publicIds.TryGetValue(pair.Key, out var id) && id != pair.Value)))
            return null;
        var winners = candidates.Select(mapping => mapping.AnimeClickId).Distinct(StringComparer.Ordinal).ToList();
        return winners.Count == 1 ? winners[0] : null;
    }

    public async Task<string?> ResolveAsync(string kind, IReadOnlyDictionary<string, string> ids, PluginConfiguration config, CancellationToken token)
    {
        if (!config.EnableCommunityMappings || !ids.Any(pair => pair.Key is "Tmdb" or "Tvdb" or "AniList")) return null;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var dataset = await cache.GetAsync<AnimeClickCommunityDataset>(DatasetKey, 24, token).ConfigureAwait(false);
            if (dataset is null && DateTimeOffset.UtcNow >= _nextDatasetAttempt)
            {
                _nextDatasetAttempt = DateTimeOffset.UtcNow.AddHours(1);
                try
                {
                    using var client = factory.CreateClient(AnimeClickHttp.ClientName);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await client.GetAsync(
                        $"https://raw.githubusercontent.com/{Repository}/main/community/mappings.json",
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    dataset = ParseDataset(await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false));
                    await cache.SetAsync(DatasetKey, dataset, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* A community outage must never interrupt normal metadata lookup. */ }
            }
            // Keep the last reviewed snapshot usable during an outage.
            dataset ??= await cache.GetAsync<AnimeClickCommunityDataset>(DatasetKey, token).ConfigureAwait(false);
            if (dataset is null) return null;
            try { dataset = ParseDataset(JsonSerializer.Serialize(dataset, JsonOptions)); }
            catch (JsonException) { return null; }
            return Match(dataset, kind, ids);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> EnqueueCorrectionAsync(BaseItem item, CancellationToken token)
    {
        var config = _configuration();
        if (!config.EnableCommunitySharing) return "Disattivata";
        var mapping = BuildMapping(item);
        if (mapping is null) return "Nessun ID pubblico da condividere: le stagioni e i collegamenti senza ID esterni restano locali.";
        if (string.IsNullOrWhiteSpace(config.CommunityGitHubToken)) return "Configura il token GitHub per condividere le correzioni.";
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var fingerprint = Fingerprint(mapping);
            if (await _state.GetAsync<bool>("community::sent::" + fingerprint, token).ConfigureAwait(false)) return "Abbinamento già condiviso.";
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            if (pending.Any(entry => Fingerprint(entry.Mapping) == fingerprint)) return "Abbinamento già in attesa di invio.";
            if (pending.Count >= 100) return "Coda della comunità piena. La correzione locale è salvata.";
            pending.Add(new AnimeClickCommunitySubmission { Mapping = mapping });
            await _state.SetAsync(OutboxKey, pending, token).ConfigureAwait(false);
            var persisted = await LoadOutboxAsync(token).ConfigureAwait(false);
            if (!persisted.Any(entry => Fingerprint(entry.Mapping) == fingerprint))
                return "Impossibile salvare l’invio. La correzione locale è conservata: verifica i permessi dei dati del plugin.";
            _message = "Correzione in attesa di invio automatico a GitHub.";
            return _message;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> StatusAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            var config = _configuration();

            // _message only changes when something is sent or queued, so right after sharing is switched
            // on it still held the initial "disattivata" text and the page contradicted the setting.
            var message = !config.EnableCommunitySharing
                ? "La condivisione è disattivata. Nessun invio viene eseguito."
                : string.IsNullOrWhiteSpace(config.CommunityGitHubToken)
                    ? "Condivisione attiva ma senza token GitHub: nessun invio è possibile finché non lo salvi."
                    : _message != DisabledMessage
                        ? _message
                        : pending.Count > 0 ? "Correzioni in attesa di invio." : "Condivisione attiva: nessuna correzione in attesa di invio.";
            return new { Enabled = config.EnableCommunitySharing, Pending = pending.Count(entry => entry.Attempts < 5),
                Failed = pending.Count(entry => entry.Attempts >= 5), Message = message };
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            foreach (var entry in pending) { entry.Attempts = 0; entry.NextAttemptAt = DateTimeOffset.UtcNow; }
            await _state.SetAsync(OutboxKey, pending, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendNextAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                _message = "Invio temporaneamente non disponibile; gli abbinamenti locali sono conservati.";
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task SendNextAsync(CancellationToken token)
    {
        var config = _configuration();
        if (!config.EnableCommunitySharing || string.IsNullOrWhiteSpace(config.CommunityGitHubToken)) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            var entry = pending.FirstOrDefault(entry => entry.Attempts < 5 && entry.NextAttemptAt <= DateTimeOffset.UtcNow);
            if (entry is null) return;
            // Revalidate persisted data before any public request; never forward extra fields.
            ParseDataset(JsonSerializer.Serialize(new AnimeClickCommunityDataset { Mappings = [entry.Mapping] }, JsonOptions));
            var fingerprint = Fingerprint(entry.Mapping);
            try
            {
                using var client = factory.CreateClient(AnimeClickHttp.ClientName);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Community/1.1");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.CommunityGitHubToken.Trim());
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                // Recover an acknowledged-late POST without blindly creating another issue.
                var marker = "mapping-" + fingerprint;
                using var search = await client.GetAsync("https://api.github.com/search/issues?q="
                    + Uri.EscapeDataString($"repo:{Repository} in:title {marker}"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                search.EnsureSuccessStatusCode();
                using var existing = JsonDocument.Parse(await ReadBoundedAsync(search, timeout.Token).ConfigureAwait(false));
                if (existing.RootElement.GetProperty("total_count").GetInt32() == 0)
                {
                    // Opt-out is checked immediately before publication as well.
                    if (!_configuration().EnableCommunitySharing) return;
                    using var response = await client.PostAsJsonAsync($"https://api.github.com/repos/{Repository}/issues", new
                    {
                        title = $"[Abbinamento] {entry.Mapping.Kind} AnimeClick {entry.Mapping.AnimeClickId} {marker}",
                        body = "Correzione condivisa automaticamente con consenso dell’amministratore. Solo identificativi pubblici; da verificare prima dell’approvazione.\n\n```json\n"
                            + JsonSerializer.Serialize(entry.Mapping, JsonOptions) + "\n```"
                    }, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                await _state.SetAsync("community::sent::" + fingerprint, true, token).ConfigureAwait(false);
                pending.Remove(entry);
                _message = "Correzione condivisa su GitHub, in attesa di revisione.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                entry.Attempts++;
                entry.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, entry.Attempts));
                _message = entry.Attempts >= 5 ? "Invio non riuscito: verifica il token GitHub e premi Riprova."
                    : "GitHub non disponibile o token non valido. L’invio verrà ritentato automaticamente.";
            }
            await _state.SetAsync(OutboxKey, pending, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<AnimeClickCommunitySubmission>> LoadOutboxAsync(CancellationToken token)
    {
        var stored = await _state.GetAsync<List<AnimeClickCommunitySubmission>>(OutboxKey, token).ConfigureAwait(false) ?? [];
        var valid = new List<AnimeClickCommunitySubmission>();
        foreach (var entry in stored.Take(100))
        {
            try
            {
                if (entry is null || entry.Attempts < 0) continue;
                ParseDataset(JsonSerializer.Serialize(new AnimeClickCommunityDataset { Mappings = [entry.Mapping] }, JsonOptions));
                valid.Add(entry);
            }
            catch (JsonException) { /* Corrupted local entries must neither escape nor block valid contributions. */ }
        }
        return valid;
    }

    private static string Fingerprint(AnimeClickCommunityMapping mapping)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapping.Kind + ":" + mapping.AnimeClickId + ":"
            + string.Join(",", mapping.ProviderIds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)))))[..20];

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new HttpRequestException("Response too large");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumBytes) throw new HttpRequestException("Response too large");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public sealed class AnimeClickCommunityDataset
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("mappings")]
    public List<AnimeClickCommunityMapping> Mappings { get; set; } = [];
}

public sealed class AnimeClickCommunityMapping
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("animeClickId")]
    public string AnimeClickId { get; set; } = string.Empty;
    [JsonPropertyName("providerIds")]
    public Dictionary<string, string> ProviderIds { get; set; } = [];
}

public sealed class AnimeClickCommunitySubmission
{
    public AnimeClickCommunityMapping Mapping { get; set; } = new();
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
}
