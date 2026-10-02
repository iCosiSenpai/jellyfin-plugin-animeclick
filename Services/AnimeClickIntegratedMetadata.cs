using System.Globalization;
using System.Text.Json;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using static AnimeClick.Plugin.Services.AnimeClickTmdbClient;

namespace AnimeClick.Plugin.Services;

/// <summary>Internal data sources fill only gaps left by AnimeClick. No library writes happen here.</summary>
public sealed class AnimeClickIntegratedMetadata(AnimeClickTmdbClient tmdb, AnimeClickAiTranslator translator)
{
    public async Task CompleteAsync<T>(MetadataResult<T> result, string? name, int? year,
        IReadOnlyDictionary<string, string> storedIds, AnimeClickAnime? anime, bool movie,
        PluginConfiguration configuration, CancellationToken token) where T : BaseItem, new()
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.EnableIntegratedMetadata) return;
        var target = result.Item;
        var ids = MergeIds(target, storedIds);
        var id = await tmdb.ResolveAnimeIdAsync(ids, anime?.OriginalTitle, anime?.Title ?? name,
            anime?.ProductionYear ?? year, movie, configuration, token).ConfigureAwait(false);
        if (id is null) return;
        var data = await tmdb.GetIntegratedDetailsAsync(id.Value, movie, configuration, token).ConfigureAwait(false);
        if (data is not { } root || Conflicts(ids, root)) return;
        target.SetProviderId("Tmdb", id.Value.ToString(CultureInfo.InvariantCulture));
        AddExternalIds(target, Object(root, "external_ids"));
        if (AnimeClickMetadataText.Title(target.OriginalTitle) is null)
            target.OriginalTitle = AnimeClickMetadataText.Title(String(root, movie ? "original_title" : "original_name"));
        var date = Date(root, movie ? "release_date" : "first_air_date");
        target.PremiereDate ??= date;
        target.ProductionYear ??= date?.Year;
        if (configuration.EnableCommunityRating && target.CommunityRating is null && Rating(root) is { } rating)
            target.CommunityRating = rating;
        if (configuration.EnableStudios && AnimeClickMetadataText.Labels(target.Studios).Length == 0)
            target.Studios = AnimeClickMetadataText.Labels(Array(root, "production_companies").Select(v => String(v, "name") ?? ""));
        if (configuration.EnableProductionLocations && target.ProductionLocations.Length == 0)
            target.ProductionLocations = AnimeClickMetadataText.Labels(Array(root, "production_countries").Select(v => String(v, "iso_3166_1") ?? ""));
        if (target is Series series)
        {
            series.Status ??= String(root, "status") switch
            {
                "Ended" or "Canceled" => SeriesStatus.Ended,
                "Returning Series" or "In Production" => SeriesStatus.Continuing,
                _ => null
            };
            if (series.Status == SeriesStatus.Ended) series.EndDate ??= Date(root, "last_air_date");
        }
        if (string.IsNullOrWhiteSpace(target.OfficialRating)) target.OfficialRating = Certification(root, movie);
        if (configuration.EnableCast)
        {
            var people = People(Object(root, movie ? "credits" : "aggregate_credits"));
            // Keep AnimeClick's names and roles. An incomplete native cast can still lack directors/writers.
            var current = result.People ?? [];
            var kinds = current.Select(v => v.Type).ToHashSet();
            result.People = current.Concat(people.Where(v => !kinds.Contains(v.Type)))
                .DistinctBy(v => (v.Name, v.Type, v.Role)).ToList();
        }
        if (configuration.EnableTrailers && target.RemoteTrailers.Count == 0)
            target.RemoteTrailers = Array(Object(root, "videos"), "results")
                .Where(v => String(v, "site") == "YouTube" && String(v, "type") is "Trailer" or "Teaser"
                    && String(v, "iso_639_1") is "it" or null)
                .Where(v => System.Text.RegularExpressions.Regex.IsMatch(String(v, "key") ?? "", @"^[A-Za-z0-9_-]{11}$"))
                .Select(v => new MediaUrl { Name = String(v, "type") == "Teaser" ? "Teaser" : "Trailer", Url = "https://www.youtube.com/watch?v=" + String(v, "key") }).ToArray();
        result.HasMetadata = true;
    }

    public async Task<bool> CompleteEpisodeAsync(Episode target, EpisodeInfo info,
        PluginConfiguration configuration, CancellationToken token)
    {
        if (!configuration.EnableIntegratedMetadata || info.ParentIndexNumber is not >= 0 || info.IndexNumber is not >= 0
            || info.IndexNumberEnd.GetValueOrDefault(info.IndexNumber.Value) != info.IndexNumber.Value
            || PositiveId(info.SeriesProviderIds?.GetValueOrDefault("Tmdb")) is not { } id) return false;
        var data = await tmdb.GetIntegratedChildAsync(id, info.ParentIndexNumber.Value, info.IndexNumber.Value,
            info.GetProviderId("Tmdb"), configuration, token).ConfigureAwait(false);
        if (data is not { } root || Conflicts(info.ProviderIds, root)) return false;
        target.PremiereDate ??= Date(root, "air_date");
        target.ProductionYear ??= target.PremiereDate?.Year;
        if (configuration.EnableCommunityRating) target.CommunityRating ??= Rating(root);
        target.SetProviderId("Tmdb", Number(root, "id").ToString(CultureInfo.InvariantCulture));
        AddExternalIds(target, Object(root, "external_ids"));
        AnimeClickNumberingGuard.Preserve(target, info);
        return true;
    }

    public async Task<bool> CompleteSeasonAsync(Season target, SeasonInfo info,
        PluginConfiguration configuration, CancellationToken token)
    {
        if (!configuration.EnableIntegratedMetadata || info.IndexNumber is not >= 0
            || PositiveId(info.SeriesProviderIds?.GetValueOrDefault("Tmdb")) is not { } id) return false;
        var data = await tmdb.GetIntegratedChildAsync(id, info.IndexNumber.Value, null, info.GetProviderId("Tmdb"), configuration, token).ConfigureAwait(false);
        if (data is not { } root || Conflicts(info.ProviderIds, root)) return false;
        target.PremiereDate ??= Date(root, "air_date");
        target.ProductionYear ??= target.PremiereDate?.Year;
        if (configuration.EnableCommunityRating) target.CommunityRating ??= Rating(root);
        target.SetProviderId("Tmdb", Number(root, "id").ToString(CultureInfo.InvariantCulture));
        AddExternalIds(target, Object(root, "external_ids"));
        var translations = Object(root, "translations");
        var translationsJson = translations.ValueKind == JsonValueKind.Object ? translations.GetRawText() : "{}";
        if (configuration.EnablePlot && string.IsNullOrWhiteSpace(target.Overview))
        {
            target.Overview = AnimeClickMetadataText.ItalianOverview(ParseEpisodeTranslation(translationsJson, "it-IT"));
            if (target.Overview is null && AnimeClickAiTranslator.IsConfigured(configuration, out _)
                && AnimeClickMetadataText.Clean(ParseEpisodeTranslation(translationsJson, "en-US")) is { } english)
                target.Overview = await translator.TranslateMetadataFieldAsync(english, "season-text", $"TMDB:tv:{id}:season:{info.IndexNumber}",
                    "season.overview", "en", "it", configuration, token).ConfigureAwait(false);
        }
        // Generic 'Season 1' translations are ignored; Jellyfin retains its localized season label.
        if (configuration.PreferItalianTitle && AnimeClickMetadataText.Title(target.Name) is null)
        {
            target.Name = AnimeClickMetadataText.Title(ParseEpisodeTranslation(translationsJson, "it-IT", "name"))!;
            if (target.Name is null && AnimeClickAiTranslator.IsConfigured(configuration, out _)
                && AnimeClickMetadataText.Title(ParseEpisodeTranslation(translationsJson, "en-US", "name")) is { } englishName)
                target.Name = AnimeClickMetadataText.Title(await translator.TranslateMetadataFieldAsync(englishName, "season-text",
                    $"TMDB:tv:{id}:season:{info.IndexNumber}", "season.name", "en", "it", configuration, token).ConfigureAwait(false))!;
        }
        AnimeClickNumberingGuard.Preserve(target, info);
        return true;
    }

    internal static Dictionary<string, string> MergeIds(BaseItem target, IReadOnlyDictionary<string, string> stored)
    {
        var ids = new Dictionary<string, string>(target.ProviderIds, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in stored) if (!string.IsNullOrWhiteSpace(pair.Value)) ids[pair.Key] = pair.Value;
        // Echo verified manual identities so fallback discovery cannot replace them.
        foreach (var pair in stored.Where(v => v.Key is "Tmdb" or "Tvdb" or "Imdb" or "AniList"))
            if (!string.IsNullOrWhiteSpace(pair.Value)) target.SetProviderId(pair.Key, pair.Value);
        return ids;
    }

    private static bool Conflicts(IReadOnlyDictionary<string, string>? ids, JsonElement root)
    {
        if (ids is null) return false;
        var external = Object(root, "external_ids");
        foreach (var (provider, field) in new[] { ("Imdb", "imdb_id"), ("Tvdb", "tvdb_id") })
        {
            var value = Object(external, field);
            var returned = value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : null;
            if (!string.IsNullOrWhiteSpace(returned) && ids.TryGetValue(provider, out var expected)
                && !string.IsNullOrWhiteSpace(expected) && expected != returned) return true;
        }
        return false;
    }

    private static void AddExternalIds(BaseItem target, JsonElement external)
    {
        if (PositiveId(Object(external, "tvdb_id").ToString()) is { } tvdb && string.IsNullOrWhiteSpace(target.GetProviderId("Tvdb")))
            target.SetProviderId("Tvdb", tvdb.ToString(CultureInfo.InvariantCulture));
        var imdb = String(external, "imdb_id");
        if (imdb is not null && System.Text.RegularExpressions.Regex.IsMatch(imdb, @"^tt[0-9]+$") && string.IsNullOrWhiteSpace(target.GetProviderId("Imdb")))
            target.SetProviderId("Imdb", imdb);
    }

    private static float? Rating(JsonElement root) => Object(root, "vote_average") is { ValueKind: JsonValueKind.Number } value
        && value.TryGetSingle(out var rating) && float.IsFinite(rating) && rating is > 0 and <= 10
        && Number(root, "vote_count") > 0 ? rating : null;

    private static string? Certification(JsonElement root, bool movie)
    {
        foreach (var item in Array(Object(root, movie ? "release_dates" : "content_ratings"), "results")
            .Where(v => String(v, "iso_3166_1") == "IT"))
        {
            var value = movie ? Array(item, "release_dates").Select(v => String(v, "certification")).FirstOrDefault(v => v is not null) : String(item, "rating");
            if (value is not null) return value;
        }
        return null;
    }

    internal static List<PersonInfo> People(JsonElement credits)
    {
        var results = new List<PersonInfo>();
        foreach (var (field, type) in new[] { ("cast", PersonKind.Actor), ("crew", PersonKind.Unknown) })
        foreach (var person in Array(credits, field).Take(150))
        {
            if (String(person, "name") is not { } name || Number(person, "id") <= 0) continue;
            var jobs = field == "cast" ? new[] { "Actor" } : Array(person, "jobs").Select(v => String(v, "job")).DefaultIfEmpty(String(person, "job"));
            foreach (var job in jobs)
            {
                var kind = type == PersonKind.Actor ? type : job switch { "Director" => PersonKind.Director, "Writer" or "Screenplay" => PersonKind.Writer, "Producer" or "Executive Producer" => PersonKind.Producer, "Original Music Composer" => PersonKind.Composer, _ => PersonKind.Unknown };
                if (kind == PersonKind.Unknown) continue;
                var role = type == PersonKind.Actor ? String(person, "character") ?? string.Join(", ", Array(person, "roles").Select(v => String(v, "character")).Where(v => v is not null)) : null;
                results.Add(new PersonInfo { Name = name, Type = kind, Role = role,
                    ImageUrl = AnimeClickArtwork.TmdbImageUrl(String(person, "profile_path")),
                    ProviderIds = new Dictionary<string, string> { ["Tmdb"] = Number(person, "id").ToString(CultureInfo.InvariantCulture) } });
            }
        }
        return results.DistinctBy(v => (v.Name, v.Type, v.Role)).ToList();
    }
}
