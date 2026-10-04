using System.Globalization;
using System.Text.RegularExpressions;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// AniList as a keyless source next to TMDB. It never replaces what AnimeClick wrote: it fills the
/// fields still empty — cast with characters, animation studio, dates, status, score, trailer — and
/// offers its cover and banner after the higher-resolution artwork. It writes no text, because AniList
/// has none in Italian, and it trusts an AniList ID only when type and year agree with the work.
/// </summary>
public sealed class AnimeClickAniListMetadata(AnimeClickAniListResolver aniList)
{
    private static readonly string[] SeriesFormats = ["TV", "TV_SHORT", "ONA", "OVA"];
    private static readonly string[] SeasonFormats = ["TV", "TV_SHORT", "ONA"];

    /// <summary>Fills the empty fields of a series or a film; true when AniList contributed.</summary>
    public async Task<bool> CompleteAsync<T>(MetadataResult<T> result, IReadOnlyDictionary<string, string> ids, bool movie,
        PluginConfiguration configuration, CancellationToken token) where T : BaseItem
    {
        if (!configuration.EnableAniListMetadata || AniListId(ids) is not { } id) return false;
        var media = await aniList.GetMediaAsync(id, configuration, token).ConfigureAwait(false);
        if (media is null || !Agrees(media, movie, result.Item.ProductionYear)) return false;
        Apply(result, media, configuration);
        return true;
    }

    /// <summary>
    /// A film needs a MOVIE entry, a series a TV/ONA/OVA one, and a known year may differ by one at most:
    /// an ID left by another plugin that points at a commercial or at the third season is ignored.
    /// </summary>
    internal static bool Agrees(AnimeClickAniListMedia media, bool movie, int? knownYear)
    {
        var formatOk = movie ? media.Format == "MOVIE" : SeriesFormats.Contains(media.Format);
        return formatOk && !(knownYear is > 0 && media.Year is > 0 && Math.Abs(knownYear.Value - media.Year.Value) > 1);
    }

    internal static void Apply<T>(MetadataResult<T> result, AnimeClickAniListMedia media, PluginConfiguration configuration) where T : BaseItem
    {
        var target = result.Item;
        if (AnimeClickMetadataText.Title(target.OriginalTitle) is null && AnimeClickMetadataText.Title(media.RomajiTitle) is { } original)
            target.OriginalTitle = original;
        target.PremiereDate ??= media.StartDate;
        target.ProductionYear ??= media.Year;
        if (configuration.EnableCommunityRating && target.CommunityRating is null && media.AverageScore is > 0 and <= 100)
            target.CommunityRating = (float)Math.Round(media.AverageScore.Value / 10d, 1);
        if (configuration.EnableStudios && AnimeClickMetadataText.Labels(target.Studios).Length == 0 && media.Studios.Count > 0)
            target.Studios = AnimeClickMetadataText.Labels(media.Studios);
        if (configuration.EnableTrailers && target.RemoteTrailers.Count == 0
            && media.TrailerYouTubeId is { } trailer && Regex.IsMatch(trailer, "^[A-Za-z0-9_-]{11}$"))
            target.RemoteTrailers = [new MediaUrl { Name = "Trailer", Url = "https://www.youtube.com/watch?v=" + trailer }];
        if (target is Series series)
        {
            series.Status ??= media.Status switch
            {
                "FINISHED" or "CANCELLED" => SeriesStatus.Ended,
                "RELEASING" or "HIATUS" => SeriesStatus.Continuing,
                _ => null
            };
            if (series.Status == SeriesStatus.Ended) series.EndDate ??= media.EndDate;
        }

        if (configuration.EnableCast)
        {
            // AnimeClick's people come first; AniList adds only the kinds still missing, as TMDB does.
            var current = result.People ?? [];
            var kinds = current.Select(person => person.Type).ToHashSet();
            var added = People(media).Where(person => !kinds.Contains(person.Type));
            result.People = current.Concat(added).DistinctBy(person => (person.Name, person.Type, person.Role)).ToList();
        }

        result.HasMetadata = true;
    }

    /// <summary>Japanese voice actors with the character they play, and the main staff.</summary>
    internal static List<PersonInfo> People(AnimeClickAniListMedia media)
    {
        var people = media.Cast
            .Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => new PersonInfo { Name = person.Name, Role = person.Role, Type = PersonKind.Actor, ImageUrl = TrustedImage(person.ImageUrl) })
            .ToList();
        foreach (var person in media.Staff)
        {
            PersonKind? kind = person.Role switch
            {
                "Director" => PersonKind.Director,
                "Original Creator" or "Original Story" or "Series Composition" => PersonKind.Writer,
                "Music" => PersonKind.Composer,
                _ => null
            };
            if (kind is not null && !string.IsNullOrWhiteSpace(person.Name))
                people.Add(new PersonInfo { Name = person.Name, Role = person.Role, Type = kind.Value, ImageUrl = TrustedImage(person.ImageUrl) });
        }

        return people;
    }

    /// <summary>
    /// The year season N aired, walking the AniList sequel chain from the series' first season. Used only
    /// when Jellyfin has no date for that season, and only when the entry it lands on holds exactly as
    /// many episodes as the library season: a split cour or a recut gives no answer instead of a wrong one.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>?> WithSeasonYearAsync(IReadOnlyDictionary<int, int>? knownYears,
        IReadOnlyDictionary<string, string>? seriesIds, int seasonNumber, int? episodeCount,
        PluginConfiguration configuration, CancellationToken token)
    {
        if (!configuration.EnableAniListMetadata || seasonNumber < 2 || episodeCount is not > 0
            || knownYears?.GetValueOrDefault(seasonNumber) is > 0 || seriesIds is null || AniListId(seriesIds) is not { } rootId)
            return knownYears;
        var year = await SeasonYearAsync(rootId, seasonNumber, episodeCount.Value,
            id => aniList.GetMediaAsync(id, configuration, token)).ConfigureAwait(false);
        if (year is null) return knownYears;
        var merged = knownYears is null ? new Dictionary<int, int>() : new Dictionary<int, int>(knownYears);
        merged[seasonNumber] = year.Value;
        return merged;
    }

    internal static async Task<int?> SeasonYearAsync(int rootId, int seasonNumber, int episodeCount, Func<int, Task<AnimeClickAniListMedia?>> load)
    {
        var current = await load(rootId).ConfigureAwait(false);

        // The ID must name the first season: a TV prequel means it names a later one.
        if (current is null || !SeasonFormats.Contains(current.Format)
            || current.Relations.Any(relation => relation.Type == "PREQUEL" && SeasonFormats.Contains(relation.Format)))
            return null;
        for (var season = 2; season <= seasonNumber; season++)
        {
            var sequels = current!.Relations.Where(relation => relation.Type == "SEQUEL" && SeasonFormats.Contains(relation.Format)).ToList();
            if (sequels.Count != 1) return null;
            current = await load(sequels[0].Id).ConfigureAwait(false);
            if (current is null) return null;
        }

        return current!.Episodes == episodeCount ? current.Year : null;
    }

    /// <summary>Cover and banner of a series or a film, offered after Fanart and TMDB.</summary>
    public async Task<List<RemoteImageInfo>> GetImagesAsync(BaseItem item, PluginConfiguration configuration, CancellationToken token)
    {
        if (!configuration.EnableAniListMetadata || item is not (Series or Movie) || AniListId(item.ProviderIds) is not { } id) return [];
        var media = await aniList.GetMediaAsync(id, configuration, token).ConfigureAwait(false);
        return media is null || !Agrees(media, item is Movie, item.ProductionYear) ? [] : Images(media);
    }

    internal static List<RemoteImageInfo> Images(AnimeClickAniListMedia media)
    {
        var images = new List<RemoteImageInfo>();
        if (TrustedImage(media.CoverUrl) is { } cover)
            images.Add(new RemoteImageInfo { ProviderName = "AnimeClick", Url = cover, Type = ImageType.Primary });
        if (TrustedImage(media.BannerUrl) is { } banner)
            images.Add(new RemoteImageInfo { ProviderName = "AnimeClick", Url = banner, Type = ImageType.Banner });
        return images;
    }

    /// <summary>Only images on AniList's own CDN, under the paths it uses for media and staff.</summary>
    internal static string? TrustedImage(string? url)
        => AnimeClickArtwork.IsAniListImage(url) ? new Uri(url!).AbsoluteUri : null;

    private static int? AniListId(IReadOnlyDictionary<string, string> ids)
    {
        var value = ids.FirstOrDefault(pair => string.Equals(pair.Key, "AniList", StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }
}
