using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Resolves an AniList anime ID from a title via the AniList GraphQL API.
/// AnimeClick pages expose no cross-site IDs, so without this the AniList /
/// Fanart image fetchers have no ID to look up. Best-effort: returns null on
/// any failure and never throws into the metadata pipeline.
/// </summary>
public class AnimeClickAniListResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeClickAniListResolver> _logger;

    public AnimeClickAniListResolver(IHttpClientFactory httpClientFactory, ILogger<AnimeClickAniListResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Queries the AniList GraphQL API for an anime matching <paramref name="title"/>
    /// and returns its AniList ID, or null if no match / on any error.
    /// </summary>
    public async Task<string?> ResolveAniListIdAsync(string? title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);

            var query = "{\"query\":\"{ Media(search: \\\"" + EscapeGraphQL(title) + "\\\", type: ANIME) { id type title { romaji english } } }\"}";
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("AniListResolver: AniList returned {Status} for {Title}", response.StatusCode, title);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var id = ParseAniListIdFromSearch(json);
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogDebug("AniListResolver: no AniList match for {Title}", title);
                return null;
            }

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniListResolver: failed for {Title}", title);
            return null;
        }
    }

    internal static string EscapeGraphQL(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string? ParseAniListIdFromSearch(string json)
    {
        // Shape: {"data":{"Media":{"id":14175, ...}}}. Dependency-free extraction
        // (avoids pulling in a System.Text.Json version that conflicts with the host).
        const string idMarker = "\"id\":";
        const string dataMarker = "\"data\"";
        const string mediaMarker = "\"Media\"";
        var dataIdx = json.IndexOf(dataMarker, StringComparison.Ordinal);
        if (dataIdx < 0) return null;
        var mediaIdx = json.IndexOf(mediaMarker, dataIdx, StringComparison.Ordinal);
        if (mediaIdx < 0) return null;
        var idIdx = json.IndexOf(idMarker, mediaIdx, StringComparison.Ordinal);
        if (idIdx < 0) return null;
        var after = idIdx + idMarker.Length;
        while (after < json.Length && (json[after] == ' ' || json[after] == '\t')) after++;
        var start = after;
        while (after < json.Length && char.IsDigit(json[after])) after++;
        if (after == start) return null;
        return json.Substring(start, after - start);
    }
}
