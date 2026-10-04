using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnimeClick.Plugin.Services;

/// <summary>The three sharing choices offered in the setup and the Community tab.</summary>
public static class AnimeClickCommunitySharing
{
    public const string Ask = "Ask";
    public const string Always = "Always";
    public const string Never = "Never";

    public static string Normalize(string? mode) => mode switch
    {
        Always => Always,
        Never => Never,
        _ => Ask
    };
}

/// <summary>
/// Public ID exchange. Reads the reviewed dataset from the project repository and sends corrections
/// the administrator agreed to share, either through the community relay (no account needed) or,
/// when a token is saved, directly to GitHub under the administrator's own account.
/// </summary>
public sealed class AnimeClickCommunityService(IHttpClientFactory factory, AnimeClickCacheService cache,
    Func<PluginConfiguration>? configuration = null, IApplicationPaths? paths = null, ILibraryManager? library = null)
    : BackgroundService
{
    public const string Repository = "iCosiSenpai/jellyfin-plugin-animeclick";
    public const string DatasetUrl = $"https://raw.githubusercontent.com/{Repository}/main/community/mappings-v2.json";
    private const string IssuePrefix = $"https://github.com/{Repository}/issues/";
    private const string OutboxKey = "community::outbox:v1";
    private const string ProposalsKey = "community::proposals:v1";
    private const string InstallationKey = "community::installation:v1";
    private const string StatesCheckedKey = "community::states-checked:v1";
    private const string DatasetKey = "community::dataset:v2";
    private const int MaximumOutbox = 100;
    private const int MaximumProposals = 200;
    private const int StateChecksPerDay = 10;

    private readonly Func<PluginConfiguration> _configuration = configuration ?? (() => Plugin.Instance?.Configuration ?? new PluginConfiguration());

    // Outbox, proposals and the installation identifier are durable data, separate from administrative cache clears.
    private readonly AnimeClickCacheService _state = paths is null ? cache : new AnimeClickCacheService(paths,
        NullLogger<AnimeClickCacheService>.Instance, Path.Combine(paths.DataPath, "AnimeClickCommunity"));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _datasetGate = new(1, 1);
    private DateTimeOffset _nextDatasetAttempt;
    private string? _lastError;

    /* ===== Mappings ===== */

    /// <summary>The public identities of a series or a film, or null when it has nothing public to share.</summary>
    public static AnimeClickCommunityMapping? BuildMapping(BaseItem item)
    {
        if (item is not (Series or Movie)) return null;
        if (!AnimeClickClient.TryNormalizeAnimeClickId(item.GetProviderId("AnimeClick"), out var id)) return null;
        var ids = PublicIds(item.ProviderIds, AnimeClickCommunityData.WorkProviders);
        return ids.Count == 0 ? null : new AnimeClickCommunityMapping
        {
            Kind = item is Movie ? "Movie" : "Series", AnimeClickId = id.Split('/')[0], ProviderIds = ids
        };
    }

    /// <summary>
    /// A season is shared as the IDs of its series, its number and how many episodes the library holds in
    /// it. The count is what keeps a library cut differently from receiving the wrong card.
    /// </summary>
    public static AnimeClickCommunityMapping? BuildSeasonMapping(Season season, IReadOnlyDictionary<string, string> seriesIds, int? episodeCount)
    {
        if (!AnimeClickClient.TryNormalizeAnimeClickId(season.GetProviderId("AnimeClick"), out var id)
            || season.IndexNumber is not (>= 0 and <= AnimeClickCommunityData.MaximumSeasonNumber)
            || episodeCount is not (>= 1 and <= AnimeClickCommunityData.MaximumEpisodeCount)) return null;
        var ids = PublicIds(seriesIds, AnimeClickCommunityData.SeriesProviders);
        return ids.Count == 0 ? null : new AnimeClickCommunityMapping
        {
            Kind = "Season", AnimeClickId = id.Split('/')[0], Series = ids,
            SeasonNumber = season.IndexNumber, EpisodeCount = episodeCount
        };
    }

    /// <summary>
    /// Episodes in a season as the community counts them: distinct episode numbers held by real files,
    /// with a multi-episode file counting every number it covers.
    /// </summary>
    public static int? CountEpisodes(IEnumerable<Episode> episodes)
    {
        var numbers = new HashSet<int>();
        foreach (var episode in episodes)
        {
            if (episode.IsVirtualItem || episode.IndexNumber is not >= 0) continue;
            var start = episode.IndexNumber.Value;
            var end = Math.Max(start, episode.IndexNumberEnd ?? start);
            if (end - start > 100) continue;
            for (var number = start; number <= end; number++) numbers.Add(number);
        }

        return numbers.Count == 0 ? null : numbers.Count;
    }

    /// <summary>Builds the mapping for any shareable item, reading the library for seasons.</summary>
    public AnimeClickCommunityMapping? CreateMapping(BaseItem item)
    {
        if (item is not Season season) return BuildMapping(item);
        if (library is null) return null;
        var series = library.GetItemById(season.SeriesId);
        return series is null ? null : BuildSeasonMapping(season, series.ProviderIds, CountSeasonEpisodes(season));
    }

    /// <summary>Counts the episodes of a season from the library, or null when it cannot be read.</summary>
    public int? CountSeasonEpisodes(Season season) => AnimeClickLibrarySeasons.CountEpisodes(library, season);

    /// <summary>Finds the library season a metadata request is about, through its folder.</summary>
    public Season? FindSeason(string? path) => AnimeClickLibrarySeasons.Find(library, path);

    private static Dictionary<string, string> PublicIds(IReadOnlyDictionary<string, string> ids, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in allowed)
        {
            var value = ids.FirstOrDefault(pair => string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase)).Value;
            if (AnimeClickCommunityData.NormalizePublicId(value) is { } id) result[provider] = id;
        }

        return result;
    }

    /* ===== Reading ===== */

    public async Task<string?> ResolveAsync(string kind, IReadOnlyDictionary<string, string> ids, PluginConfiguration config, CancellationToken token)
    {
        if (!config.EnableCommunityMappings || !ids.Any(pair => AnimeClickCommunityData.WorkProviders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
            return null;
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        return dataset is null ? null : AnimeClickCommunityData.Match(dataset, kind, ids);
    }

    public async Task<string?> ResolveSeasonAsync(IReadOnlyDictionary<string, string> seriesIds, int seasonNumber, int? episodeCount,
        PluginConfiguration config, CancellationToken token)
    {
        if (!config.EnableCommunityMappings || episodeCount is not > 0
            || !seriesIds.Any(pair => AnimeClickCommunityData.SeriesProviders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
            return null;
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        return dataset is null ? null : AnimeClickCommunityData.MatchSeason(dataset, seriesIds, seasonNumber, episodeCount.Value);
    }

    /// <summary>
    /// The approved card for a library season that has none yet. The library is read only when the dataset
    /// has a season with that number, so ordinary libraries never pay for the episode count.
    /// </summary>
    public async Task<string?> ResolveLibrarySeasonAsync(IReadOnlyDictionary<string, string> seriesIds, int seasonNumber, string? seasonPath,
        PluginConfiguration config, CancellationToken token)
    {
        if (!config.EnableCommunityMappings) return null;
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        if (dataset is null || !dataset.Mappings.Any(mapping => mapping.Kind == "Season" && mapping.SeasonNumber == seasonNumber)) return null;
        var season = FindSeason(seasonPath);
        var episodes = season is null ? null : CountSeasonEpisodes(season);
        return episodes is null ? null : AnimeClickCommunityData.MatchSeason(dataset, seriesIds, seasonNumber, episodes.Value);
    }

    /// <summary>
    /// The reviewed dataset: cached for a day, fetched at most once an hour after a failure, and the last
    /// reviewed copy kept usable during an outage. A community outage never interrupts metadata lookup.
    /// </summary>
    internal async Task<AnimeClickCommunityDataset?> GetDatasetAsync(CancellationToken token)
    {
        await _datasetGate.WaitAsync(token).ConfigureAwait(false);
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
                    using var response = await client.GetAsync(DatasetUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    dataset = AnimeClickCommunityData.ParseDataset(await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false));
                    await cache.SetAsync(DatasetKey, dataset, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { dataset = null; }
            }

            dataset ??= await cache.GetAsync<AnimeClickCommunityDataset>(DatasetKey, token).ConfigureAwait(false);
            if (dataset is null) return null;
            try { return AnimeClickCommunityData.Revalidate(dataset); }
            catch (JsonException) { return null; }
        }
        finally { _datasetGate.Release(); }
    }

    /* ===== Sharing ===== */

    /// <summary>
    /// Called after the administrator corrects a match. «Sempre» queues the proposal at once, «Chiedi»
    /// returns it for the page to show with a Share button, «Mai» does nothing.
    /// </summary>
    public async Task<AnimeClickCommunityOffer> OfferAsync(BaseItem item, CancellationToken token)
    {
        var mode = AnimeClickCommunitySharing.Normalize(_configuration().CommunitySharingMode);
        if (mode == AnimeClickCommunitySharing.Never) return new AnimeClickCommunityOffer { Mode = mode };
        var mapping = CreateMapping(item);
        if (mapping is null)
            return new AnimeClickCommunityOffer
            {
                Mode = mode,
                Message = "Nessun ID pubblico da condividere: servono l’ID AnimeClick e almeno un ID TMDB o TheTVDB (o AniList per serie e film)."
            };
        if (mode == AnimeClickCommunitySharing.Ask)
            return new AnimeClickCommunityOffer { Mode = mode, Proposal = mapping, Fingerprint = AnimeClickCommunityData.Fingerprint(mapping) };
        return new AnimeClickCommunityOffer { Mode = mode, Queued = true, Message = await EnqueueAsync(mapping, token).ConfigureAwait(false) };
    }

    /// <summary>Queues an item after an explicit click on «Condividi», whatever the automatic choice.</summary>
    public async Task<string> ShareAsync(BaseItem item, CancellationToken token)
    {
        var mapping = CreateMapping(item);
        return mapping is null
            ? "Nessun ID pubblico da condividere per questo elemento."
            : await EnqueueAsync(mapping, token).ConfigureAwait(false);
    }

    /// <summary>Compatibility entry point: shares a correction only when the choice is «Sempre».</summary>
    public async Task<string> EnqueueCorrectionAsync(BaseItem item, CancellationToken token)
    {
        var offer = await OfferAsync(item, token).ConfigureAwait(false);
        return offer.Message ?? (offer.Mode == AnimeClickCommunitySharing.Never ? "Disattivata" : "In attesa della tua conferma.");
    }

    internal async Task<string> EnqueueAsync(AnimeClickCommunityMapping mapping, CancellationToken token)
    {
        if (!AnimeClickCommunityData.IsValid(mapping)) return "Proposta non valida: non è stata messa in coda.";
        var fingerprint = AnimeClickCommunityData.Fingerprint(mapping);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proposals = await LoadProposalsAsync(token).ConfigureAwait(false);
            if (proposals.Any(proposal => proposal.Fingerprint == fingerprint && proposal.State is "Sent" or "Approved"))
                return "Abbinamento già condiviso.";
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            if (pending.Any(entry => AnimeClickCommunityData.Fingerprint(entry.Mapping) == fingerprint)) return "Abbinamento già in attesa di invio.";
            if (pending.Count >= MaximumOutbox) return "Coda della comunità piena. La correzione locale è salvata.";
            pending.Add(new AnimeClickCommunitySubmission { Mapping = mapping });
            await _state.SetAsync(OutboxKey, pending, token).ConfigureAwait(false);
            var persisted = await LoadOutboxAsync(token).ConfigureAwait(false);
            if (!persisted.Any(entry => AnimeClickCommunityData.Fingerprint(entry.Mapping) == fingerprint))
                return "Impossibile salvare l’invio. La correzione locale è conservata: verifica i permessi dei dati del plugin.";
            proposals.RemoveAll(proposal => proposal.Fingerprint == fingerprint);
            proposals.Insert(0, new AnimeClickCommunityProposal { Fingerprint = fingerprint, Mapping = mapping, State = "Queued" });
            await SaveProposalsAsync(proposals, token).ConfigureAwait(false);
            return "Grazie! La proposta partirà a breve per la revisione.";
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
            var proposals = await LoadProposalsAsync(token).ConfigureAwait(false);
            foreach (var proposal in proposals.Where(proposal => proposal.State == "Failed")) proposal.State = "Queued";
            await SaveProposalsAsync(proposals, token).ConfigureAwait(false);
            _lastError = null;
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
                await RefreshProposalStatesAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                _lastError = "Invio temporaneamente non disponibile; gli abbinamenti locali sono conservati.";
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends the next due proposal. A saved GitHub token sends it under the administrator's account;
    /// otherwise the relay published in the dataset opens the issue. «Mai» stops every send, queued
    /// proposals included.
    /// </summary>
    internal async Task SendNextAsync(CancellationToken token)
    {
        var config = _configuration();
        if (AnimeClickCommunitySharing.Normalize(config.CommunitySharingMode) == AnimeClickCommunitySharing.Never) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            var entry = pending.FirstOrDefault(entry => entry.Attempts < 5 && entry.NextAttemptAt <= DateTimeOffset.UtcNow);
            if (entry is null) return;
            var useGitHub = !string.IsNullOrWhiteSpace(config.CommunityGitHubToken);
            string? relay = null;
            if (!useGitHub)
            {
                relay = (await GetDatasetAsync(token).ConfigureAwait(false))?.Relay;
                if (relay is null) return; // Stays queued until the relay is published.
            }

            var fingerprint = AnimeClickCommunityData.Fingerprint(entry.Mapping);
            AnimeClickSendResult result;
            try
            {
                result = useGitHub
                    ? await SendToGitHubAsync(entry.Mapping, fingerprint, config.CommunityGitHubToken.Trim(), token).ConfigureAwait(false)
                    : await SendToRelayAsync(relay!, entry.Mapping, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                result = AnimeClickSendResult.Retry(null);
            }

            var proposals = await LoadProposalsAsync(token).ConfigureAwait(false);
            var proposal = proposals.FirstOrDefault(item => item.Fingerprint == fingerprint);
            if (proposal is null)
            {
                proposal = new AnimeClickCommunityProposal { Fingerprint = fingerprint, Mapping = entry.Mapping };
                proposals.Insert(0, proposal);
            }

            switch (result.Outcome)
            {
                case AnimeClickSendOutcome.Sent:
                    pending.Remove(entry);
                    proposal.State = "Sent";
                    proposal.Issue = result.Issue;
                    proposal.Url = result.Url;
                    proposal.SentAt = DateTimeOffset.UtcNow;
                    _lastError = null;
                    break;
                case AnimeClickSendOutcome.Rejected:
                    pending.Remove(entry);
                    proposal.State = "Failed";
                    _lastError = "Il servizio ha rifiutato una proposta non valida: è stata scartata.";
                    break;
                default:
                    entry.Attempts++;
                    var wait = result.RetryAfter ?? TimeSpan.FromMinutes(Math.Pow(2, entry.Attempts));
                    entry.NextAttemptAt = DateTimeOffset.UtcNow.Add(wait);
                    if (entry.Attempts >= 5) proposal.State = "Failed";
                    _lastError = entry.Attempts >= 5
                        ? useGitHub ? "Invio non riuscito: verifica il token GitHub e premi Riprova." : "Invio non riuscito: riprova più tardi."
                        : "Servizio non raggiungibile: l’invio verrà ritentato automaticamente.";
                    break;
            }

            await _state.SetAsync(OutboxKey, pending, token).ConfigureAwait(false);
            await SaveProposalsAsync(proposals, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<AnimeClickSendResult> SendToRelayAsync(string relay, AnimeClickCommunityMapping mapping, CancellationToken token)
    {
        using var client = factory.CreateClient(AnimeClickHttp.ClientName);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Community/2.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var body = AnimeClickCommunityData.Serialize(new AnimeClickRelayRequest
        {
            Installation = await GetInstallationAsync(token).ConfigureAwait(false),
            PluginVersion = typeof(AnimeClickCommunityService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Mapping = mapping
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(relay, content, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
            return AnimeClickSendResult.Rejected();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return AnimeClickSendResult.Retry(response.Headers.RetryAfter?.Delta is { } delta && delta < TimeSpan.FromDays(1) ? delta : TimeSpan.FromHours(1));
        if (!response.IsSuccessStatusCode) return AnimeClickSendResult.Retry(null);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false));
        return Sent(document.RootElement, "issue", "url");
    }

    private async Task<AnimeClickSendResult> SendToGitHubAsync(AnimeClickCommunityMapping mapping, string fingerprint, string githubToken, CancellationToken token)
    {
        using var client = factory.CreateClient(AnimeClickHttp.ClientName);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Community/2.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        // Recover an acknowledged-late POST without blindly creating another issue.
        var marker = "mapping-" + fingerprint;
        using (var search = await client.GetAsync("https://api.github.com/search/issues?q="
            + Uri.EscapeDataString($"repo:{Repository} in:title {marker}"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
        {
            search.EnsureSuccessStatusCode();
            using var existing = JsonDocument.Parse(await ReadBoundedAsync(search, timeout.Token).ConfigureAwait(false));
            if (existing.RootElement.GetProperty("total_count").GetInt32() > 0)
                return Sent(existing.RootElement.GetProperty("items")[0], "number", "html_url");
        }

        // Opt-out is checked immediately before publication as well.
        if (AnimeClickCommunitySharing.Normalize(_configuration().CommunitySharingMode) == AnimeClickCommunitySharing.Never)
            return AnimeClickSendResult.Retry(TimeSpan.FromHours(1));
        using var response = await client.PostAsJsonAsync($"https://api.github.com/repos/{Repository}/issues",
            new { title = IssueTitle(mapping, fingerprint), body = IssueBody(mapping) }, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.UnprocessableEntity) return AnimeClickSendResult.Rejected();
        response.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false));
        return Sent(created.RootElement, "number", "html_url");
    }

    /// <summary>Same title the relay writes, so the review action reads both channels the same way.</summary>
    public static string IssueTitle(AnimeClickCommunityMapping mapping, string fingerprint)
        => $"[Proposta] {mapping.Kind} · AnimeClick {mapping.AnimeClickId} · mapping-{fingerprint}";

    public static string IssueBody(AnimeClickCommunityMapping mapping)
        => "Proposta di abbinamento condivisa dal plugin con il consenso dell’amministratore. "
            + "Contiene solo identificativi pubblici; i controlli automatici la verificano prima della revisione.\n\n"
            + "```json\n" + AnimeClickCommunityData.Canonical(mapping) + "\n```\n";

    private static AnimeClickSendResult Sent(JsonElement element, string issueProperty, string urlProperty)
    {
        int? issue = element.TryGetProperty(issueProperty, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetInt32(out var value) && value > 0 ? value : null;
        string? url = element.TryGetProperty(urlProperty, out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;

        // Only a link to an issue of this repository is ever shown in the page.
        if (url is not null && (!url.StartsWith(IssuePrefix, StringComparison.Ordinal)
            || !int.TryParse(url.AsSpan(IssuePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var linked)
            || (issue is not null && linked != issue))) url = null;
        if (url is null && issue is not null) url = IssuePrefix + issue.Value.ToString(CultureInfo.InvariantCulture);
        return AnimeClickSendResult.Sent(issue, url);
    }

    /// <summary>
    /// Once a day, reads the public state of up to ten proposals still under review. A proposal the
    /// dataset contains is approved without asking anyone.
    /// </summary>
    internal async Task RefreshProposalStatesAsync(CancellationToken token)
    {
        var checkedAt = await _state.GetAsync<DateTimeOffset?>(StatesCheckedKey, token).ConfigureAwait(false);
        if (checkedAt is not null && DateTimeOffset.UtcNow - checkedAt.Value < TimeSpan.FromDays(1)) return;
        await _state.SetAsync<DateTimeOffset?>(StatesCheckedKey, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proposals = await LoadProposalsAsync(token).ConfigureAwait(false);
            MarkApproved(proposals, dataset);
            using var client = factory.CreateClient(AnimeClickHttp.ClientName);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeClick-Community/2.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            foreach (var proposal in proposals.Where(proposal => proposal.State == "Sent" && proposal.Issue is not null).Take(StateChecksPerDay))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/issues/{proposal.Issue}",
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    using var issue = JsonDocument.Parse(await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false));
                    if (issue.RootElement.GetProperty("state").GetString() != "closed") continue;
                    var reason = issue.RootElement.TryGetProperty("state_reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
                    proposal.State = reason == "completed" ? "Approved" : "NotAccepted";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { /* The state is informative only; the next daily check tries again. */ }
            }

            await SaveProposalsAsync(proposals, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void MarkApproved(List<AnimeClickCommunityProposal> proposals, AnimeClickCommunityDataset? dataset)
    {
        if (dataset is null) return;
        var approved = dataset.Mappings.Select(AnimeClickCommunityData.Fingerprint).ToHashSet(StringComparer.Ordinal);
        foreach (var proposal in proposals.Where(proposal => proposal.State != "Approved" && approved.Contains(proposal.Fingerprint)))
            proposal.State = "Approved";
    }

    /* ===== Status for the page ===== */

    public async Task<object> StatusAsync(CancellationToken token)
    {
        var config = _configuration();
        var mode = AnimeClickCommunitySharing.Normalize(config.CommunitySharingMode);
        var hasToken = !string.IsNullOrWhiteSpace(config.CommunityGitHubToken);
        var relay = hasToken ? null : (await GetDatasetAsync(token).ConfigureAwait(false))?.Relay;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await LoadOutboxAsync(token).ConfigureAwait(false);
            var message = mode == AnimeClickCommunitySharing.Never
                ? "La condivisione è disattivata. Nessun invio viene eseguito."
                : _lastError
                    ?? (!hasToken && relay is null
                        ? "Il servizio di invio senza account non è ancora attivo: le proposte restano in coda finché non lo sarà."
                        : pending.Count > 0
                            ? "Correzioni in attesa di invio."
                            : mode == AnimeClickCommunitySharing.Always
                                ? "Condivisione automatica attiva: nessuna correzione in attesa di invio."
                                : "Ti chiederò conferma dopo ogni correzione: nessuna proposta in attesa di invio.");
            return new
            {
                Mode = mode,
                Enabled = mode != AnimeClickCommunitySharing.Never,
                Channel = hasToken ? "github" : relay is null ? "unavailable" : "relay",
                Pending = pending.Count(entry => entry.Attempts < 5),
                Failed = pending.Count(entry => entry.Attempts >= 5),
                Message = message
            };
        }
        finally { _gate.Release(); }
    }

    /// <summary>The proposals this installation sent, newest first, with the state of their review.</summary>
    public async Task<IReadOnlyList<AnimeClickCommunityProposal>> ProposalsAsync(CancellationToken token)
    {
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proposals = await LoadProposalsAsync(token).ConfigureAwait(false);
            MarkApproved(proposals, dataset);
            return proposals;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The numbers of the Community card on the home page.</summary>
    public async Task<AnimeClickCommunitySummary> SummaryAsync(CancellationToken token)
    {
        var config = _configuration();
        var dataset = await GetDatasetAsync(token).ConfigureAwait(false);
        var proposals = await ProposalsAsync(token).ConfigureAwait(false);
        return new AnimeClickCommunitySummary
        {
            MappingsEnabled = config.EnableCommunityMappings,
            Mode = AnimeClickCommunitySharing.Normalize(config.CommunitySharingMode),
            DatasetAvailable = dataset is not null,
            Available = dataset?.Mappings.Count ?? 0,
            AvailableSeasons = dataset?.Mappings.Count(mapping => mapping.Kind == "Season") ?? 0,
            InLibrary = dataset is null ? 0 : CountInLibrary(dataset),
            RelayAvailable = dataset?.Relay is not null || !string.IsNullOrWhiteSpace(config.CommunityGitHubToken),
            Queued = proposals.Count(proposal => proposal.State == "Queued"),
            InReview = proposals.Count(proposal => proposal.State == "Sent"),
            Approved = proposals.Count(proposal => proposal.State == "Approved"),
            NotAccepted = proposals.Count(proposal => proposal.State == "NotAccepted"),
            Failed = proposals.Count(proposal => proposal.State == "Failed")
        };
    }

    /// <summary>Series, films and seasons of the library whose AnimeClick card is the one the community approved.</summary>
    internal int CountInLibrary(AnimeClickCommunityDataset dataset)
    {
        if (library is null || dataset.Mappings.Count == 0) return 0;
        try
        {
            var count = 0;
            var works = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie], Recursive = true, IsVirtualItem = false
            });
            var seasonSeries = dataset.Mappings.Where(mapping => mapping.Kind == "Season")
                .SelectMany(mapping => mapping.Series!.Select(pair => pair.Key + ":" + pair.Value)).ToHashSet(StringComparer.Ordinal);
            foreach (var work in works)
            {
                var own = work.GetProviderId("AnimeClick")?.Split('/')[0];
                if (own is not null && AnimeClickCommunityData.Match(dataset, work is Movie ? "Movie" : "Series", work.ProviderIds) == own) count++;
                if (work is not Series series || seasonSeries.Count == 0
                    || !AnimeClickCommunityData.SeriesProviders.Any(provider => series.GetProviderId(provider) is { } id && seasonSeries.Contains(provider + ":" + id)))
                    continue;
                foreach (var season in library.GetItemList(new InternalItemsQuery
                         {
                             ParentId = series.Id, IncludeItemTypes = [BaseItemKind.Season], IsVirtualItem = false
                         }).OfType<Season>())
                {
                    var seasonId = season.GetProviderId("AnimeClick")?.Split('/')[0];
                    if (seasonId is null || season.IndexNumber is not { } number || CountSeasonEpisodes(season) is not { } episodes) continue;
                    if (AnimeClickCommunityData.MatchSeason(dataset, series.ProviderIds, number, episodes) == seasonId) count++;
                }
            }

            return count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /* ===== Storage ===== */

    private async Task<string> GetInstallationAsync(CancellationToken token)
    {
        var stored = await _state.GetAsync<string>(InstallationKey, token).ConfigureAwait(false);
        if (stored is { Length: 32 } && stored.All(char.IsAsciiHexDigitLower)) return stored;
        var created = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await _state.SetAsync(InstallationKey, created, token).ConfigureAwait(false);
        return created;
    }

    private async Task<List<AnimeClickCommunitySubmission>> LoadOutboxAsync(CancellationToken token)
    {
        var stored = await _state.GetAsync<List<AnimeClickCommunitySubmission>>(OutboxKey, token).ConfigureAwait(false) ?? [];

        // Corrupted local entries must neither escape nor block valid contributions.
        return stored.Take(MaximumOutbox)
            .Where(entry => entry is not null && entry.Attempts >= 0 && AnimeClickCommunityData.IsValid(entry.Mapping))
            .ToList();
    }

    private async Task<List<AnimeClickCommunityProposal>> LoadProposalsAsync(CancellationToken token)
    {
        var stored = await _state.GetAsync<List<AnimeClickCommunityProposal>>(ProposalsKey, token).ConfigureAwait(false) ?? [];
        return stored.Where(proposal => proposal is not null && AnimeClickCommunityData.IsValid(proposal.Mapping))
            .Take(MaximumProposals).ToList();
    }

    private Task SaveProposalsAsync(List<AnimeClickCommunityProposal> proposals, CancellationToken token)
        => _state.SetAsync(ProposalsKey, proposals.Take(MaximumProposals).ToList(), token);

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > AnimeClickCommunityData.MaximumBytes) throw new HttpRequestException("Response too large");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > AnimeClickCommunityData.MaximumBytes) throw new HttpRequestException("Response too large");
            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public sealed class AnimeClickCommunityOffer
{
    public string Mode { get; set; } = AnimeClickCommunitySharing.Ask;

    /// <summary>With «Chiedi»: exactly what «Condividi» would send.</summary>
    public AnimeClickCommunityMapping? Proposal { get; set; }

    public string? Fingerprint { get; set; }

    public bool Queued { get; set; }

    public string? Message { get; set; }
}

public sealed class AnimeClickCommunitySummary
{
    public bool MappingsEnabled { get; set; }
    public string Mode { get; set; } = AnimeClickCommunitySharing.Ask;
    public bool DatasetAvailable { get; set; }
    public int Available { get; set; }
    public int AvailableSeasons { get; set; }
    public int InLibrary { get; set; }
    public bool RelayAvailable { get; set; }
    public int Queued { get; set; }
    public int InReview { get; set; }
    public int Approved { get; set; }
    public int NotAccepted { get; set; }
    public int Failed { get; set; }
}

public sealed class AnimeClickCommunitySubmission
{
    public AnimeClickCommunityMapping Mapping { get; set; } = new();
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AnimeClickCommunityProposal
{
    public string Fingerprint { get; set; } = string.Empty;
    public AnimeClickCommunityMapping Mapping { get; set; } = new();

    /// <summary>Queued, Sent (under review), Approved, NotAccepted or Failed.</summary>
    public string State { get; set; } = "Queued";

    public int? Issue { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class AnimeClickRelayRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = AnimeClickCommunityData.SchemaVersion;

    [System.Text.Json.Serialization.JsonPropertyName("installation")]
    public string Installation { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("mapping")]
    public AnimeClickCommunityMapping Mapping { get; set; } = new();
}

internal enum AnimeClickSendOutcome
{
    Sent,
    Rejected,
    Retry
}

internal readonly record struct AnimeClickSendResult(AnimeClickSendOutcome Outcome, int? Issue, string? Url, TimeSpan? RetryAfter)
{
    public static AnimeClickSendResult Sent(int? issue, string? url) => new(AnimeClickSendOutcome.Sent, issue, url, null);
    public static AnimeClickSendResult Rejected() => new(AnimeClickSendOutcome.Rejected, null, null, null);
    public static AnimeClickSendResult Retry(TimeSpan? after) => new(AnimeClickSendOutcome.Retry, null, null, after);
}
