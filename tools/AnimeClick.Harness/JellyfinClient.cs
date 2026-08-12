using System.Text.Json;

namespace AnimeClick.Harness;

internal sealed record JellyfinSeries(string Id, string Name, string AnimeClickId);

internal sealed record JellyfinEpisode(
    string Id,
    int? SeasonNumber,
    int? IndexNumber,
    int? IndexNumberEnd,
    string? Name,
    string? AnimeClickProviderId);

internal sealed record JellyfinQualityItem(
    string Name,
    string ItemType,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Overview,
    bool Locked);

/// <summary>
/// Read-only access to the local Jellyfin server. Used to answer the question the offline
/// report cannot: not "what would the plugin produce" but "where does what is stored differ
/// from what the plugin would produce now". Only GET requests are issued.
/// </summary>
internal sealed class JellyfinClient : IDisposable
{
    private const int PageSize = 5000;
    private readonly HttpClient _client;

    public JellyfinClient(string baseUrl, string token)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"MediaBrowser Token=\"{token}\"");
    }

    public int GetRequestCount { get; private set; }

    public async Task<string> GetServerNameAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("System/Info", cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("ServerName", out var name)
            ? name.GetString() ?? "?"
            : "?";
    }

    /// <summary>Series carrying an AnimeClick provider id, in library order.</summary>
    public async Task<List<JellyfinSeries>> GetAnimeClickSeriesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
                "Items?includeItemTypes=Series&recursive=true&fields=ProviderIds&enableImages=false&limit=5000",
                cancellationToken)
            .ConfigureAwait(false);

        var series = new List<JellyfinSeries>();
        foreach (var item in document.RootElement.GetProperty("Items").EnumerateArray())
        {
            var animeClickId = ProviderId(item, "AnimeClick");
            if (string.IsNullOrWhiteSpace(animeClickId))
            {
                continue;
            }

            series.Add(new JellyfinSeries(
                item.GetProperty("Id").GetString()!,
                item.TryGetProperty("Name", out var name) ? name.GetString() ?? "?" : "?",
                animeClickId));
        }

        return series;
    }

    /// <summary>
    /// AnimeClick id resolved onto each season, when the season provider managed to follow the
    /// franchise. Essential: a Jellyfin series aggregates seasons that AnimeClick publishes as
    /// separate entries, so matching every episode against the series-level entry would be wrong.
    /// </summary>
    public async Task<Dictionary<int, string>> GetSeasonAnimeClickIdsAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
                $"Items?parentId={seriesId}&includeItemTypes=Season&fields=ProviderIds,IndexNumber"
                + "&enableImages=false&limit=200",
                cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<int, string>();
        foreach (var item in document.RootElement.GetProperty("Items").EnumerateArray())
        {
            var season = Int(item, "IndexNumber");
            var animeClickId = ProviderId(item, "AnimeClick");
            if (season is > 0 && !string.IsNullOrWhiteSpace(animeClickId))
            {
                map[season.Value] = animeClickId;
            }
        }

        return map;
    }

    public async Task<List<JellyfinEpisode>> GetEpisodesAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
                $"Items?parentId={seriesId}&includeItemTypes=Episode&recursive=true"
                + "&fields=ProviderIds,ParentIndexNumber,IndexNumber&enableImages=false&limit=5000",
                cancellationToken)
            .ConfigureAwait(false);

        var episodes = new List<JellyfinEpisode>();
        foreach (var item in document.RootElement.GetProperty("Items").EnumerateArray())
        {
            episodes.Add(new JellyfinEpisode(
                item.GetProperty("Id").GetString()!,
                Int(item, "ParentIndexNumber"),
                Int(item, "IndexNumber"),
                Int(item, "IndexNumberEnd"),
                item.TryGetProperty("Name", out var name) ? name.GetString() : null,
                ProviderId(item, "AnimeClick")));
        }

        return episodes;
    }

    /// <summary>
    /// Reads only metadata already stored by Jellyfin. Series and movies enter the audit when they
    /// carry an AnimeClick provider id; episodes enter when their parent series does, matching the
    /// production quality service. No item id, provider id or synopsis leaves this client.
    /// </summary>
    public async Task<List<JellyfinQualityItem>> GetLibraryQualityItemsAsync(
        CancellationToken cancellationToken)
    {
        var rows = new List<QualityRow>();
        var startIndex = 0;
        while (true)
        {
            using var document = await GetJsonAsync(
                    "Items?includeItemTypes=Series,Movie,Episode&recursive=true"
                    + "&fields=Overview,ProviderIds,LockedFields,ParentIndexNumber,IndexNumber,SeriesInfo"
                    + $"&enableImages=false&startIndex={startIndex}&limit={PageSize}",
                    cancellationToken)
                .ConfigureAwait(false);
            var page = document.RootElement.GetProperty("Items");
            var pageCount = 0;
            foreach (var item in page.EnumerateArray())
            {
                pageCount++;
                rows.Add(new QualityRow(
                    item.TryGetProperty("Id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("Name", out var name) ? name.GetString() ?? "?" : "?",
                    item.TryGetProperty("Type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("SeriesId", out var seriesId) ? seriesId.GetString() : null,
                    item.TryGetProperty("SeriesName", out var seriesName) ? seriesName.GetString() : null,
                    Int(item, "ParentIndexNumber"),
                    Int(item, "IndexNumber"),
                    item.TryGetProperty("Overview", out var overview) ? overview.GetString() : null,
                    IsOverviewLocked(item),
                    !string.IsNullOrWhiteSpace(ProviderId(item, "AnimeClick"))));
            }

            startIndex += pageCount;
            var total = document.RootElement.TryGetProperty("TotalRecordCount", out var totalElement)
                        && totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : startIndex;
            if (pageCount == 0 || pageCount < PageSize || startIndex >= total)
            {
                break;
            }
        }

        var animeSeriesIds = rows
            .Where(row => string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase)
                          && row.HasAnimeClickId)
            .Select(row => row.Id)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rows
            .Where(row =>
                (row.HasAnimeClickId
                 && (string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.ItemType, "Movie", StringComparison.OrdinalIgnoreCase)))
                || (string.Equals(row.ItemType, "Episode", StringComparison.OrdinalIgnoreCase)
                    && row.SeriesId is not null
                    && animeSeriesIds.Contains(row.SeriesId)))
            .Select(row => new JellyfinQualityItem(
                row.Name,
                row.ItemType,
                row.SeriesName,
                row.SeasonNumber,
                row.EpisodeNumber,
                row.Overview,
                row.Locked))
            .ToList();
    }

    private static bool IsOverviewLocked(JsonElement item)
    {
        if (Boolean(item, "LockData") || Boolean(item, "IsLocked"))
        {
            return true;
        }

        return item.TryGetProperty("LockedFields", out var fields)
               && fields.ValueKind == JsonValueKind.Array
               && fields.EnumerateArray().Any(field =>
                   string.Equals(field.GetString(), "Overview", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Boolean(JsonElement item, string name)
        => item.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private static string? ProviderId(JsonElement item, string provider)
    {
        if (!item.TryGetProperty("ProviderIds", out var ids) || ids.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in ids.EnumerateObject())
        {
            if (string.Equals(property.Name, provider, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static int? Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        GetRequestCount++;
        using var response = await _client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    public void Dispose() => _client.Dispose();

    private sealed record QualityRow(
        string Id,
        string Name,
        string ItemType,
        string? SeriesId,
        string? SeriesName,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? Overview,
        bool Locked,
        bool HasAnimeClickId);
}
