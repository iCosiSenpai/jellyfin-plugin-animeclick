using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeClick.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using static AnimeClick.Plugin.Services.AnimeClickTmdbClient;

namespace AnimeClick.Plugin.Services;

public sealed class AnimeClickArtwork(AnimeClickTmdbClient tmdb, AnimeClickFanartClient fanart)
{
    public async Task<List<RemoteImageInfo>> GetImagesAsync(BaseItem item, PluginConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var results = new List<RemoteImageInfo>();
        var movie = item is Movie;
        var series = item switch { Series s => s, Season s => s.Series, Episode e => e.Series, _ => null };
        var ids = series?.ProviderIds ?? item.ProviderIds;
        var tmdbId = PositiveId(ids.GetValueOrDefault("Tmdb"));
        var tvdbId = PositiveId(ids.GetValueOrDefault("Tvdb"));
        if (item is Series or Movie or Season && (movie ? tmdbId : tvdbId) is { } fanartId)
        {
            var data = await fanart.GetArtworkAsync(fanartId, movie, configuration, token).ConfigureAwait(false);
            if (data is { } root) results.AddRange(ParseFanart(root, movie, item is Season season ? season.IndexNumber : null, configuration.MinPosterWidth));
        }
        if (!configuration.EnableIntegratedImages) return results;
        string? path = null;
        int? expected = null;
        if (item is Movie && tmdbId.HasValue) { path = "movie/" + tmdbId; expected = tmdbId; }
        if (item is Series && tmdbId.HasValue) { path = "tv/" + tmdbId; expected = tmdbId; }
        if (item is Season s2 && s2.IndexNumber is >= 0 && tmdbId.HasValue)
        {
            var child = await tmdb.GetIntegratedChildAsync(tmdbId.Value, s2.IndexNumber.Value, null, item.GetProviderId("Tmdb"), configuration, token).ConfigureAwait(false);
            if (child is { } c) { path = $"tv/{tmdbId}/season/{s2.IndexNumber}"; expected = Number(c, "id"); }
        }
        if (item is Episode ep && ep.ParentIndexNumber is >= 0 && ep.IndexNumber is >= 0
            && ep.IndexNumberEnd.GetValueOrDefault(ep.IndexNumber.Value) == ep.IndexNumber && tmdbId.HasValue)
        {
            var child = await tmdb.GetIntegratedChildAsync(tmdbId.Value, ep.ParentIndexNumber.Value, ep.IndexNumber.Value, item.GetProviderId("Tmdb"), configuration, token).ConfigureAwait(false);
            if (child is { } c) { path = $"tv/{tmdbId}/season/{ep.ParentIndexNumber}/episode/{ep.IndexNumber}"; expected = Number(c, "id"); }
        }
        if (item is Person && PositiveId(item.GetProviderId("Tmdb")) is { } person) { path = "person/" + person; expected = person; }
        if (path is null) return results;
        var images = await tmdb.GetIntegratedImagesAsync(path, expected, configuration, token).ConfigureAwait(false);
        if (images is { } imageRoot)
        {
            foreach (var (field, type) in new[] { ("posters", ImageType.Primary), ("backdrops", ImageType.Backdrop), ("logos", ImageType.Logo), ("stills", ImageType.Primary), ("profiles", ImageType.Primary) })
                results.AddRange(Sort(Array(imageRoot, field).Select(v => Make(v, type, false, configuration.MinPosterWidth)).OfType<RemoteImageInfo>()).Take(20));
        }
        return results.DistinctBy(v => (v.Type, v.Url)).ToList();
    }

    internal static List<RemoteImageInfo> ParseFanart(JsonElement root, bool movie, int? season, int minWidth)
    {
        var groups = season.HasValue ? new[] { ("seasonposter", ImageType.Primary), ("seasonbanner", ImageType.Banner), ("seasonthumb", ImageType.Thumb) }
            : movie ? new[] { ("movieposter", ImageType.Primary), ("movie4kbackground", ImageType.Backdrop), ("moviebackground", ImageType.Backdrop), ("hdmovielogo", ImageType.Logo), ("movielogo", ImageType.Logo), ("hdmovieclearart", ImageType.Art), ("movieart", ImageType.Art), ("moviebanner", ImageType.Banner), ("moviethumb", ImageType.Thumb), ("moviedisc", ImageType.Disc) }
            : new[] { ("tvposter", ImageType.Primary), ("tv4kbackground", ImageType.Backdrop), ("showbackground", ImageType.Backdrop), ("hdtvlogo", ImageType.Logo), ("clearlogo", ImageType.Logo), ("hdclearart", ImageType.Art), ("clearart", ImageType.Art), ("tvbanner", ImageType.Banner), ("tvthumb", ImageType.Thumb) };
        var results = new List<RemoteImageInfo>();
        foreach (var group in groups)
            results.AddRange(Sort(Array(root, group.Item1).Where(v => !season.HasValue || Object(v, "season").ToString() == season.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Select(v => Make(v, group.Item2, true, minWidth)).OfType<RemoteImageInfo>()).Take(20));
        return results.DistinctBy(v => (v.Type, v.Url)).ToList();
    }

    private static IOrderedEnumerable<RemoteImageInfo> Sort(IEnumerable<RemoteImageInfo> images) => images
        .OrderByDescending(v => (long)(v.Width ?? 0) * (v.Height ?? 0)).ThenByDescending(v => v.Language == "it").ThenBy(v => v.Language is null ? 0 : 1);

    private static RemoteImageInfo? Make(JsonElement root, ImageType type, bool fanart, int minWidth)
    {
        var url = fanart ? NormalizeFanartUrl(String(root, "url")) : TmdbImageUrl(String(root, "file_path"));
        if (url is null) return null;
        var width = Dimension(root, "width"); var height = Dimension(root, "height");
        if (type == ImageType.Primary && width is > 0 && width < minWidth) return null;
        var language = String(root, fanart ? "lang" : "iso_639_1");
        if (language is "00" or "") language = null;
        return new RemoteImageInfo { ProviderName = "AnimeClick", Url = url, Type = type, Width = width, Height = height, Language = language };
    }

    private static int? Dimension(JsonElement root, string field) => int.TryParse(Object(root, field).ToString(), out var value) && value is > 0 and <= 20000 ? value : null;
    internal static string? TmdbImageUrl(string? path) => path is not null && Regex.IsMatch(path, @"^/[A-Za-z0-9_-]+\.(?:jpg|jpeg|png|webp|svg)$", RegexOptions.IgnoreCase)
        ? "https://image.tmdb.org/t/p/original" + path : null;
    internal static string? NormalizeFanartUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.IdnHost != "assets.fanart.tv"
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !Regex.IsMatch(uri.AbsolutePath, @"^/fanart/[A-Za-z0-9/_-]+\.(?:jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase)) return null;
        return "https://assets.fanart.tv" + uri.AbsolutePath;
    }

    internal static bool IsTrustedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && ((uri.IdnHost == "image.tmdb.org" && uri.AbsolutePath.StartsWith("/t/p/original/", StringComparison.Ordinal)
            && TmdbImageUrl(uri.AbsolutePath["/t/p/original".Length..]) == uri.AbsoluteUri)
            || NormalizeFanartUrl(url) == uri.AbsoluteUri);
}
