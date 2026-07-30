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

/// <summary>
/// Read-only access to the local Jellyfin server. Used to answer the question the offline
/// report cannot: not "what would the plugin produce" but "where does what is stored differ
/// from what the plugin would produce now". Only GET requests are issued.
/// </summary>
internal sealed class JellyfinClient : IDisposable
{
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
        using var response = await _client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    public void Dispose() => _client.Dispose();
}
