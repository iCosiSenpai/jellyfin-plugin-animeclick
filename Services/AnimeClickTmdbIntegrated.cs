using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Providers;

namespace AnimeClick.Plugin.Services;

public partial class AnimeClickTmdbClient
{
    private readonly SemaphoreStripe _integratedGates = new();

    /// <summary>Existing identities win. Automatic title matching requires one exact animation result.</summary>
    public async Task<int?> ResolveAnimeIdAsync(IReadOnlyDictionary<string, string> ids, string? originalTitle,
        string? name, int? year, bool movie, PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuration.TmdbApiKey)) return null;
        if (PositiveId(ids.GetValueOrDefault("Tmdb")) is { } stored) return stored;
        // A nonempty invalid/conflicting manual ID must not silently become a different match.
        if (!string.IsNullOrWhiteSpace(ids.GetValueOrDefault("Tmdb"))) return null;
        var kind = movie ? "movie" : "tv";
        foreach (var provider in new[] { "Imdb", "Tvdb" })
        {
            var value = ids.GetValueOrDefault(provider);
            if (string.IsNullOrWhiteSpace(value) || (provider == "Tvdb" && movie)) continue;
            if (provider == "Imdb" ? !Regex.IsMatch(value, @"^tt[0-9]+$") : PositiveId(value) is null) return null;
            var source = provider == "Imdb" ? "imdb_id" : "tvdb_id";
            var json = await GetIntegratedJsonAsync($"tmdbFind:v1::{kind}::{provider}::{value}",
                $"find/{Uri.EscapeDataString(value)}?external_source={source}", configuration, token,
                root => Object(root, kind + "_results").ValueKind == JsonValueKind.Array).ConfigureAwait(false);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var matches = Array(doc.RootElement, kind + "_results").Select(v => Number(v, "id")).Where(v => v > 0).Distinct().ToArray();
            // A supplied public ID is stronger evidence than a fuzzy name; never contradict it.
            return matches.Length == 1 ? matches[0] : null;
        }
        var titles = new[] { originalTitle, name }.Where(v => AnimeClickMetadataText.Title(v) is not null)
            .Select(v => AnimeClickAutomaticIdentification.PartNumber(v!) is > 1 ? v!.Trim() : AnimeClickSeriesSearchProvider.CleanSearchQuery(v!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = new HashSet<int>();
        foreach (var title in titles)
        {
            var key = "tmdbIdentify:v1::" + kind + "::" + title + "::" + year;
            // Do not filter server-side by year: seeing remakes is necessary to reject ambiguous matches.
            var json = await GetIntegratedJsonAsync(key, $"search/{kind}?query={Uri.EscapeDataString(title)}&language=it-IT&include_adult=false",
                configuration, token, root => Object(root, "results").ValueKind == JsonValueKind.Array).ConfigureAwait(false);
            if (json is null) return null;
            var matches = SelectAnimeIds(json, title, year, movie).ToHashSet();
            using var search = JsonDocument.Parse(json);
            foreach (var candidate in Array(search.RootElement, "results").Where(v => IsAnimationYear(v, year, movie)
                && Number(v, "id") > 0 && !matches.Contains(Number(v, "id"))).Take(5))
            {
                var candidateId = Number(candidate, "id");
                var aliases = await GetIntegratedJsonAsync($"tmdbAliases:v1::{kind}::{candidateId}", $"{kind}/{candidateId}/alternative_titles",
                    configuration, token, root => Number(root, "id") == candidateId
                        && Object(root, movie ? "titles" : "results").ValueKind == JsonValueKind.Array).ConfigureAwait(false);
                if (aliases is null) return null;
                using var aliasesDoc = JsonDocument.Parse(aliases);
                if (Array(aliasesDoc.RootElement, movie ? "titles" : "results").Any(v => NormalizeIdentity(String(v, "title")) == NormalizeIdentity(title)))
                    matches.Add(candidateId);
            }
            if (matches.Count > 1) return null;
            foreach (var match in matches) found.Add(match);
            if (found.Count > 1) return null;
        }
        return found.Count == 1 ? found.Single() : null;
    }

    internal static int[] SelectAnimeIds(string json, string title, int? year, bool movie)
    {
        using var document = JsonDocument.Parse(json);
        var query = NormalizeIdentity(title);
        if (query.Length == 0) return [];
        return Array(document.RootElement, "results").Where(item =>
        {
            if (!IsAnimationYear(item, year, movie)) return false;
            return new[] { String(item, movie ? "title" : "name"), String(item, movie ? "original_title" : "original_name") }
                .Any(v => NormalizeIdentity(v) == query);
        }).Select(v => Number(v, "id")).Where(v => v > 0).Distinct().ToArray();
    }

    private static bool IsAnimationYear(JsonElement item, int? year, bool movie) =>
        Array(item, "genre_ids").Any(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var id) && id == 16)
        && (!year.HasValue || Date(item, movie ? "release_date" : "first_air_date")?.Year == year);

    internal Task<JsonElement?> GetIntegratedDetailsAsync(int id, bool movie, PluginConfiguration configuration, CancellationToken token)
    {
        if (id <= 0) return Task.FromResult<JsonElement?>(null);
        var kind = movie ? "movie" : "tv";
        var append = new List<string> { "external_ids" };
        if (configuration.EnableCast) append.Add(movie ? "credits" : "aggregate_credits");
        if (configuration.EnableTrailers) append.Add("videos");
        append.Add(movie ? "release_dates" : "content_ratings");
        return GetIntegratedElementAsync($"tmdbIntegrated:v1::{kind}::{id}::{string.Join(',', append)}",
            $"{kind}/{id}?language=it-IT&append_to_response={string.Join(',', append)}", configuration, token,
            root => Number(root, "id") == id);
    }

    internal Task<JsonElement?> GetIntegratedChildAsync(int seriesId, int season, int? episode, string? expectedId,
        PluginConfiguration configuration, CancellationToken token)
    {
        if (seriesId <= 0 || season < 0 || episode < 0) return Task.FromResult<JsonElement?>(null);
        var path = $"tv/{seriesId}/season/{season}" + (episode.HasValue ? $"/episode/{episode}" : "");
        return GetIntegratedElementAsync("tmdbIntegratedChild:v1::" + path,
            path + "?language=it-IT&append_to_response=external_ids,translations", configuration, token,
            root => Number(root, "id") > 0 && Number(root, "season_number", -1) == season
                && (!episode.HasValue || Number(root, "episode_number", -1) == episode)
                && (string.IsNullOrWhiteSpace(expectedId) || Number(root, "id").ToString(CultureInfo.InvariantCulture) == expectedId));
    }

    internal Task<JsonElement?> GetIntegratedImagesAsync(string path, int? expectedId,
        PluginConfiguration configuration, CancellationToken token)
        => GetIntegratedElementAsync("tmdbIntegratedImages:v1::" + path, path + "/images", configuration, token,
            root => !expectedId.HasValue || Number(root, "id") == expectedId);

    private async Task<JsonElement?> GetIntegratedElementAsync(string key, string path,
        PluginConfiguration configuration, CancellationToken token, Func<JsonElement, bool> validate)
    {
        var json = await GetIntegratedJsonAsync(key, path, configuration, token, validate).ConfigureAwait(false);
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<string?> GetIntegratedJsonAsync(string key, string path, PluginConfiguration configuration,
        CancellationToken token, Func<JsonElement, bool>? validate = null)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuration.TmdbApiKey)) return null;
        var gate = _integratedGates.Get(key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await GetKnownMetadataJsonAsync(key, BaseUrl + "/" + path
                + (path.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(configuration.TmdbApiKey), configuration, token, validate).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal static int? PositiveId(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    internal static JsonElement Object(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value) ? value : default;
    internal static IEnumerable<JsonElement> Array(JsonElement root, string field)
    {
        var value = Object(root, field);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    }
    internal static string? String(JsonElement root, string field) => Object(root, field) is { ValueKind: JsonValueKind.String } value ? AnimeClickMetadataText.Clean(value.GetString()) : null;
    internal static int Number(JsonElement root, string field, int fallback = 0) => Object(root, field) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : fallback;
    internal static DateTime? Date(JsonElement root, string field) => DateTime.TryParseExact(String(root, field), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
    private static string NormalizeIdentity(string? value) => Regex.Replace(AnimeClickSearchScorer.RemoveDiacritics(value ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
}
