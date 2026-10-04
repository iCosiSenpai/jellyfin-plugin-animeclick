using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// The public community dataset: parsing, validation, fingerprints and matching. Pure and
/// network-free, and kept in step with tools/community_mappings.py and the relay through the shared
/// examples in community/fixtures/proposals.json.
/// </summary>
public static class AnimeClickCommunityData
{
    public const int SchemaVersion = 2;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumMappings = 5000;
    public const int MaximumSeasonNumber = 100;
    public const int MaximumEpisodeCount = 2000;

    /// <summary>Public identities a series or a film may carry.</summary>
    public static readonly string[] WorkProviders = ["Tmdb", "Tvdb", "AniList"];

    /// <summary>
    /// Identities that name a whole series. AniList is left out on purpose: it identifies a single
    /// cour, so a season keyed by it could attach a card to the wrong part of the series.
    /// </summary>
    public static readonly string[] SeriesProviders = ["Tmdb", "Tvdb"];

    // Deliberately not the Web defaults: those read "2" as a number and ignore the case of field names,
    // and the review tool and the relay accept neither.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false
    };

    public static AnimeClickCommunityDataset ParseDataset(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new JsonException("Dataset too large");
        var dataset = JsonSerializer.Deserialize<AnimeClickCommunityDataset>(json, JsonOptions) ?? throw new JsonException();
        if (dataset.SchemaVersion != SchemaVersion || dataset.Mappings is null || dataset.Mappings.Count > MaximumMappings)
            throw new JsonException("Unsupported community dataset");
        if (dataset.Relay is not null && !IsValidRelay(dataset.Relay)) throw new JsonException("Invalid relay address");
        foreach (var mapping in dataset.Mappings)
            if (!IsValid(mapping)) throw new JsonException("Invalid public mapping");
        return dataset;
    }

    /// <summary>Round-trips a dataset through the parser, so a cached copy is held to the same rules.</summary>
    public static AnimeClickCommunityDataset Revalidate(AnimeClickCommunityDataset dataset)
        => ParseDataset(JsonSerializer.Serialize(dataset, JsonOptions));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static bool IsValid(AnimeClickCommunityMapping? mapping)
    {
        if (mapping is null || NormalizePublicId(mapping.AnimeClickId) != mapping.AnimeClickId) return false;
        return mapping.Kind switch
        {
            "Series" or "Movie" => mapping.Series is null && mapping.SeasonNumber is null && mapping.EpisodeCount is null
                && ValidIds(mapping.ProviderIds, WorkProviders),
            "Season" => mapping.ProviderIds is null
                && ValidIds(mapping.Series, SeriesProviders)
                && mapping.SeasonNumber is >= 0 and <= MaximumSeasonNumber
                && mapping.EpisodeCount is >= 1 and <= MaximumEpisodeCount,
            _ => false
        };
    }

    private static bool ValidIds(Dictionary<string, string>? ids, string[] allowed)
        => ids is { Count: > 0 } && ids.Count <= allowed.Length
            && ids.All(pair => allowed.Contains(pair.Key, StringComparer.Ordinal) && NormalizePublicId(pair.Value) == pair.Value);

    public static string? NormalizePublicId(string? value)
        => value is { Length: > 0 and <= 10 } && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id.ToString(CultureInfo.InvariantCulture)
                : null;

    /// <summary>
    /// The relay is published in the dataset rather than compiled in, so it can be switched on or moved
    /// without a release. Only a plain https address is accepted: no credentials, query or fragment.
    /// </summary>
    public static bool IsValidRelay(string? value)
        => value is { Length: > 0 and <= 200 }
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.IsDefaultPort
            && !value.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// Sorted keys, no whitespace: the exact text the review tool and the relay hash. Written by hand
    /// because the schema is fixed and a general canonicaliser would be one more thing to keep equal.
    /// </summary>
    public static string Canonical(AnimeClickCommunityMapping mapping)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("animeClickId", mapping.AnimeClickId);
            if (mapping.Kind == "Season")
            {
                writer.WriteNumber("episodeCount", mapping.EpisodeCount ?? 0);
                writer.WriteString("kind", mapping.Kind);
                writer.WriteNumber("seasonNumber", mapping.SeasonNumber ?? 0);
                WriteIds(writer, "series", mapping.Series);
            }
            else
            {
                writer.WriteString("kind", mapping.Kind);
                WriteIds(writer, "providerIds", mapping.ProviderIds);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteIds(Utf8JsonWriter writer, string name, Dictionary<string, string>? ids)
        {
            writer.WriteStartObject(name);
            foreach (var pair in (ids ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
        }
    }

    /// <summary>First 20 hexadecimal characters of the SHA-256 of the canonical text.</summary>
    public static string Fingerprint(AnimeClickCommunityMapping mapping)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(mapping))))[..20];

    /// <summary>
    /// The approved AnimeClick card for a series or a film, or null. Any conflicting known identity
    /// prevents a suggestion from overriding evidence, and two cards for the same identity cancel out.
    /// </summary>
    public static string? Match(AnimeClickCommunityDataset dataset, string kind, IReadOnlyDictionary<string, string> ids)
    {
        var known = PublicIds(ids, WorkProviders);
        var candidates = dataset.Mappings
            .Where(mapping => mapping.Kind == kind && mapping.ProviderIds is not null
                && mapping.ProviderIds.Any(pair => known.TryGetValue(pair.Key, out var id) && id == pair.Value))
            .ToList();
        return Unique(candidates, candidate => candidate.ProviderIds!, known);
    }

    /// <summary>
    /// The approved card for one season of a series. The season number and the number of episodes in the
    /// library must both agree: another library that cut the same series differently gets nothing rather
    /// than a card that describes different episodes.
    /// </summary>
    public static string? MatchSeason(AnimeClickCommunityDataset dataset, IReadOnlyDictionary<string, string> seriesIds,
        int seasonNumber, int episodeCount)
    {
        var known = PublicIds(seriesIds, SeriesProviders);
        var candidates = dataset.Mappings
            .Where(mapping => mapping.Kind == "Season" && mapping.Series is not null
                && mapping.SeasonNumber == seasonNumber && mapping.EpisodeCount == episodeCount
                && mapping.Series.Any(pair => known.TryGetValue(pair.Key, out var id) && id == pair.Value))
            .ToList();
        return Unique(candidates, candidate => candidate.Series!, known);
    }

    private static Dictionary<string, string> PublicIds(IReadOnlyDictionary<string, string> ids, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ids)
        {
            var provider = allowed.FirstOrDefault(name => string.Equals(name, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (provider is not null && NormalizePublicId(pair.Value) is { } id) result[provider] = id;
        }

        return result;
    }

    private static string? Unique(List<AnimeClickCommunityMapping> candidates,
        Func<AnimeClickCommunityMapping, Dictionary<string, string>> idsOf, Dictionary<string, string> known)
    {
        if (candidates.Any(candidate => idsOf(candidate).Any(pair => known.TryGetValue(pair.Key, out var id) && id != pair.Value)))
            return null;
        var winners = candidates.Select(candidate => candidate.AnimeClickId).Distinct(StringComparer.Ordinal).ToList();
        return winners.Count == 1 ? winners[0] : null;
    }
}

public sealed class AnimeClickCommunityDataset
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = AnimeClickCommunityData.SchemaVersion;

    /// <summary>Where installations without a GitHub token send proposals. Absent until the relay exists.</summary>
    [JsonPropertyName("relay")]
    public string? Relay { get; set; }

    [JsonPropertyName("mappings")]
    public List<AnimeClickCommunityMapping> Mappings { get; set; } = [];
}

public sealed class AnimeClickCommunityMapping
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("animeClickId")]
    public string AnimeClickId { get; set; } = string.Empty;

    /// <summary>Series and films: their own public identities.</summary>
    [JsonPropertyName("providerIds")]
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>Seasons: the identities of the series the season belongs to.</summary>
    [JsonPropertyName("series")]
    public Dictionary<string, string>? Series { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episodeCount")]
    public int? EpisodeCount { get; set; }
}
