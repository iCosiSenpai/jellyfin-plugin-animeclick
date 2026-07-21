using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AnimeClick.Plugin.Models;
using HtmlAgilityPack;

namespace AnimeClick.Plugin.Services;

public partial class AnimeClickHtmlParser
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex YearExtractRegex();

    [GeneratedRegex(@"(\d{4})")]
    private static partial Regex FourDigitYearRegex();

    [GeneratedRegex(@"/anime/(\d+(?:/[^/?#]+)?)")]
    private static partial Regex AnimeUrlIdRegex();

    [GeneratedRegex(@"/episodio/(\d+(?:/[^/?#]+)?)")]
    private static partial Regex EpisodeUrlIdRegex();

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex EpisodeNumberRegex();

    [GeneratedRegex(@"S\s*(\d+)\s+Ep\.?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"(Opening|Ending)\s+(\d+)\s*\|\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeSongRegex();

    [GeneratedRegex(@"myanimelist\.net/anime/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MalIdRegex();

    [GeneratedRegex(@"anilist\.co/anime/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AniListIdRegex();

    [GeneratedRegex(@"anidb\.net/(?:a|anime/)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AniDbIdRegex();

    [GeneratedRegex(@"\b(Special|OAV|OVA|OAD|ONA|PV|Recap|Episode\s*0|Bonus|Extra|Episodio\s+Speciale)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialTitleRegex();

    /// <summary>
    /// Parses a full anime detail page from AnimeClick.
    /// Uses schema.org microdata and the well-defined dl/dt/dd structure.
    /// </summary>
    public AnimeClickAnime ParseAnimePage(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var anime = new AnimeClickAnime
        {
            Url = url,
            Id = ExtractId(url)
        };

        // --- Title (Italian) ---
        // <h1 itemprop="name">Mahoromatic</h1>
        anime.Title =
            Text(doc, "//h1[@itemprop='name']")
            ?? Text(doc, "//h1")
            ?? Meta(doc, "property", "og:title")
            ?? anime.Id;

        // --- Original title ---
        // <dt>Titolo originale</dt><dd><span itemprop="name">...</span></dd>
        anime.OriginalTitle = DtDdValue(doc, "Titolo originale");

        // --- Overview / Trama ---
        // <div id="trama-div" itemprop="description">Trama: ...</div>
        var tramaNode = doc.DocumentNode.SelectSingleNode("//*[@id='trama-div']");
        if (tramaNode is not null)
        {
            var rawTrama = NormalizeWhitespace(tramaNode.InnerText);
            // Remove the "Trama:" prefix if present
            if (rawTrama is not null && rawTrama.StartsWith("Trama:", StringComparison.OrdinalIgnoreCase))
            {
                rawTrama = rawTrama.Substring(6).TrimStart();
            }
            anime.Overview = rawTrama;
        }
        else
        {
            anime.Overview =
                Meta(doc, "property", "og:description")
                ?? Meta(doc, "name", "description");
        }

        // --- Cover Image ---
        // <meta itemprop="image" content="https://...cover.jpg" />
        anime.ImageUrl =
            doc.DocumentNode.SelectSingleNode("//meta[@itemprop='image']")?.GetAttributeValue("content", null)
            ?? Meta(doc, "property", "og:image");
        anime.BannerUrl = anime.ImageUrl;

        // --- Production Year ---
        // <meta itemprop="datePublished" content="2001-01-01" />
        var datePublished = doc.DocumentNode.SelectSingleNode("//meta[@itemprop='datePublished']")
            ?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(datePublished) && TryExtractYear(datePublished, out var year))
        {
            anime.ProductionYear = year;
        }
        else
        {
            // Fallback: parse from dt/dd "Anno"
            var yearText = DtDdValue(doc, "Anno");
            if (TryExtractYear(yearText, out var yearFallback))
            {
                anime.ProductionYear = yearFallback;
            }
        }

        // --- Community Rating ---
        // <span itemprop="ratingValue" content="6.569">6,569</span>
        var ratingNode = doc.DocumentNode.SelectSingleNode("//span[@itemprop='ratingValue']");
        if (ratingNode is not null)
        {
            var ratingStr = ratingNode.GetAttributeValue("content", null)
                ?? NormalizeDecimal(ratingNode.InnerText);
            if (float.TryParse(NormalizeDecimal(ratingStr), NumberStyles.Float, CultureInfo.InvariantCulture, out var vote))
            {
                anime.CommunityRating = vote;
            }
        }

        // --- Rating Count ---
        var ratingCountNode = doc.DocumentNode.SelectSingleNode("//span[@itemprop='ratingCount']");
        if (ratingCountNode is not null && int.TryParse(ratingCountNode.InnerText.Trim(), out var ratingCount))
        {
            anime.RatingCount = ratingCount;
        }

        // --- Genres ---
        // <span itemprop="genre">Commedia</span>
        var genreNodes = doc.DocumentNode.SelectNodes("//span[@itemprop='genre']");
        if (genreNodes is not null)
        {
            foreach (var genreNode in genreNodes)
            {
                var genre = NormalizeWhitespace(genreNode.InnerText);
                if (!string.IsNullOrWhiteSpace(genre) && !anime.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    anime.Genres.Add(genre);
                }
            }
        }

        // --- Studios ---
        // Studios are listed under <dt>Studio</dt>
        var studioLinks = DtDdLinks(doc, "Studio");
        foreach (var studio in studioLinks)
        {
            if (!anime.Studios.Contains(studio, StringComparer.OrdinalIgnoreCase))
            {
                anime.Studios.Add(studio);
            }
        }

        // --- Category ---
        anime.Category = DtDdLinkText(doc, "Categoria");

        // --- Tags ---
        // Keep only values that have a direct semantic target in Jellyfin:
        // demographic target, generic editorial tags and source material.
        foreach (var label in new[] { "Target", "Tag generici", "Tratto da" })
        {
            var values = DtDdLinks(doc, label);
            if (values.Length == 0)
            {
                var plainValue = DtDdValue(doc, label);
                values = string.IsNullOrWhiteSpace(plainValue) ? [] : [plainValue];
            }

            foreach (var tag in values)
            {
                if (!anime.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    anime.Tags.Add(tag);
                }
            }
        }

        // Also add Category as a tag if present (historical behaviour).
        if (!string.IsNullOrWhiteSpace(anime.Category)
            && !anime.Tags.Contains(anime.Category, StringComparer.OrdinalIgnoreCase))
        {
            anime.Tags.Add(anime.Category);
        }

        // --- Production locations ---
        var locations = DtDdLinks(doc, "Nazionalità");
        if (locations.Length == 0)
        {
            var nationality = DtDdValue(doc, "Nazionalità");
            locations = string.IsNullOrWhiteSpace(nationality) ? [] : [nationality];
        }

        foreach (var location in locations)
        {
            if (!anime.ProductionLocations.Contains(location, StringComparer.OrdinalIgnoreCase))
            {
                anime.ProductionLocations.Add(location);
            }
        }

        // --- Official Rating / Content Rating ---
        anime.OfficialRating = DtDdValue(doc, "Classificazione") ?? DtDdValue(doc, "Rating");

        // --- Episode Count ---
        var episodesText = DtDdValue(doc, "Episodi");
        if (!string.IsNullOrWhiteSpace(episodesText) && int.TryParse(episodesText.Trim(), out var epCount))
        {
            anime.EpisodeCount = epCount;
        }

        // --- Seasons Count (Stagioni) ---
        // AnimeClick exposes the list under <dt>Stagioni</dt>, like "Autunno (2015) Primavera (2016)" (2 seasons).
        // Used later to synthesise SeasonNumber when the /episodi table lists episodes as a continuous
        // "Ep. 01".."Ep. 24" block without explicit "S1/S2 Ep." row prefixes.
        var seasonsText = DtDdValue(doc, "Stagioni");
        if (!string.IsNullOrWhiteSpace(seasonsText))
        {
            // Each season entry typically contains a 4-digit year inside parentheses, e.g. "Autunno (2015)".
            // Count the parenthesised year patterns to estimate how many seasons/cours are declared.
            var seasonsMatches = YearExtractRegex().Matches(seasonsText);
            if (seasonsMatches.Count > 0)
            {
                anime.SeasonsCount = seasonsMatches.Count;
            }
        }

        // --- Status ---
        anime.Status = DtDdValue(doc, "Stato in patria");

        // --- Premiere Date ---
        if (!string.IsNullOrWhiteSpace(datePublished) &&
            DateTimeOffset.TryParse(datePublished, CultureInfo.InvariantCulture, DateTimeStyles.None, out var premiere))
        {
            anime.PremiereDate = premiere;
        }

        // --- Provider IDs (Extraction from external links) ---
        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if (links != null)
        {
            foreach (var link in links)
            {
                var href = link.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrEmpty(href)) continue;

                var malMatch = MalIdRegex().Match(href);
                if (malMatch.Success)
                {
                    anime.ProviderIds["MyAnimeList"] = malMatch.Groups[1].Value;
                    continue;
                }

                var aniListMatch = AniListIdRegex().Match(href);
                if (aniListMatch.Success)
                {
                    anime.ProviderIds["AniList"] = aniListMatch.Groups[1].Value;
                    continue;
                }

                var aniDbMatch = AniDbIdRegex().Match(href);
                if (aniDbMatch.Success)
                {
                    anime.ProviderIds["AniDB"] = aniDbMatch.Groups[1].Value;
                    continue;
                }
            }
        }

        anime.ProviderIds["AnimeClick"] = anime.Id;
        return anime;
    }

    /// <summary>
    /// Parses the AJAX characters page (/anime/{id}/personaggi).
    /// Extracts character names and their voice actors (Japanese + Italian).
    /// </summary>
    public List<AnimeClickPerson> ParseCharactersPage(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var people = new List<AnimeClickPerson>();

        // Each character block: <div class="media thumbnail thumbnail-personaggio">
        var characterBlocks = doc.DocumentNode.SelectNodes("//div[contains(@class, 'thumbnail-personaggio')]");
        if (characterBlocks is null) return people;

        foreach (var block in characterBlocks)
        {
            // Character name: <span itemprop="character"> ... <span itemprop="name">Naruto Uzumaki</span>
            var characterNameNode = block.SelectSingleNode(".//span[@itemprop='character']//span[@itemprop='name']");
            var characterName = NormalizeWhitespace(characterNameNode?.InnerText);
            if (string.IsNullOrWhiteSpace(characterName)) continue;

            // Voice actors: <span itemprop="actor"> ... <span itemprop="name">Junko Takeuchi</span>
            var actorNodes = block.SelectNodes(".//span[@itemprop='actor']");
            if (actorNodes is null) continue;

            foreach (var actorNode in actorNodes)
            {
                var actorName = NormalizeWhitespace(actorNode.SelectSingleNode(".//span[@itemprop='name']")?.InnerText);
                if (string.IsNullOrWhiteSpace(actorName)) continue;

                // Avoid duplicates
                if (people.Any(p => p.Name == actorName && p.Role == characterName)) continue;

                // Extract the actor's AnimeClick page link (e.g. /autore/64107/gen-sato)
                var urlNode = actorNode.SelectSingleNode(".//a[@itemprop='url']");
                var actorId = urlNode?.GetAttributeValue("href", null);
                var imageUrl = actorNode.SelectSingleNode(".//img")?.GetAttributeValue("src", null);
                imageUrl = ToSafePersonImageUrl(baseUrl, imageUrl);

                people.Add(new AnimeClickPerson
                {
                    Name = actorName,
                    Type = "Actor",
                    Role = characterName,
                    Id = actorId,
                    ImageUrl = imageUrl
                });
            }
        }

        return people;
    }

    /// <summary>
    /// Parses the AJAX staff page (/anime/{id}/staff).
    /// Extracts director, writer, composer, and other staff roles.
    /// </summary>
    public List<AnimeClickPerson> ParseStaffPage(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var people = new List<AnimeClickPerson>();

        // AnimeClick exposes many granular roles. Preserve the original role text
        // while mapping only to semantically compatible Jellyfin person kinds.
        var roleMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Regia"] = "Director",
            ["Regia dell'episodio"] = "Director",
            ["Soggetto Originale"] = "Writer",
            ["Series Composition"] = "Writer",
            ["Sceneggiatura"] = "Writer",
            ["Musiche"] = "Composer",
            ["Assistente alle musiche"] = "Composer",
            ["Character Design"] = "Artist",
            ["Assistente al Character Design"] = "Artist",
            ["Direzione delle animazioni"] = "Artist",
            ["Direzione artistica"] = "Artist",
            ["Storyboard"] = "Artist",
            ["Disegni chiave"] = "Artist",
            ["Direzione della fotografia"] = "Unknown",
            ["Produttore"] = "Producer",
            ["Produttore animazioni"] = "Producer",
            ["Planning Manager"] = "Producer",
            ["Montaggio"] = "Editor",
            ["Coordinamento editoriale"] = "Editor",
            ["color design"] = "Colorist",
            ["Direzione del suono"] = "Engineer",
            ["Produzione della colonna sonora"] = "Producer",
            ["Effetti sonori"] = "Engineer"
        };
        var organizationRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Animazioni", "Produzione", "Distributore", "Emittente TV"
        };

        // Structure: <h4>Regia</h4> followed by <div class="well"> with people.
        var h4Nodes = doc.DocumentNode.SelectNodes("//h4[not(@class)]");
        if (h4Nodes is null) return people;

        foreach (var h4 in h4Nodes)
        {
            var roleTitle = NormalizeWhitespace(h4.InnerText);
            if (string.IsNullOrWhiteSpace(roleTitle) || organizationRoles.Contains(roleTitle)) continue;

            var jellyfinType = roleMapping.GetValueOrDefault(roleTitle, "Unknown");
            if (roleTitle.StartsWith("Opening ", StringComparison.OrdinalIgnoreCase)
                || roleTitle.StartsWith("Ending ", StringComparison.OrdinalIgnoreCase))
            {
                jellyfinType = "Artist";
            }

            var wellDiv = h4.SelectSingleNode("following-sibling::div[contains(@class, 'well')][1]");
            if (wellDiv is null) continue;

            var nameNodes = wellDiv.SelectNodes(".//h4[@class='media-heading']//a");
            if (nameNodes is null) continue;

            foreach (var nameNode in nameNodes)
            {
                var name = NormalizeWhitespace(nameNode.InnerText);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var existing = people.FirstOrDefault(person =>
                    string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(person.Type, jellyfinType, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    if (string.IsNullOrWhiteSpace(existing.Role))
                    {
                        existing.Role = roleTitle;
                    }
                    else if (!existing.Role.Contains(roleTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Role += ", " + roleTitle;
                    }

                    continue;
                }

                var actorId = nameNode.GetAttributeValue("href", null);
                var mediaNode = nameNode.SelectSingleNode("ancestor::div[contains(@class, 'media')][1]");
                var imageUrl = mediaNode?.SelectSingleNode(".//img")?.GetAttributeValue("src", null);
                imageUrl = ToSafePersonImageUrl(baseUrl, imageUrl);

                people.Add(new AnimeClickPerson
                {
                    Name = name,
                    Type = jellyfinType,
                    Role = roleTitle,
                    Id = actorId,
                    ImageUrl = imageUrl
                });
            }
        }

        return people;
    }

    /// <summary>
    /// Parses search results HTML and returns anime-only results.
    /// </summary>
    public List<AnimeClickSearchResult> ParseSearchResults(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<AnimeClickSearchResult>();

        // Each search result is a <div class="media item-search-item">
        var items = doc.DocumentNode.SelectNodes("//div[contains(@class, 'item-search-item')]");
        if (items is null)
        {
            // Defensive: site structure may have changed
            if (html.Length > 200 && html.Contains("item-search", StringComparison.OrdinalIgnoreCase))
            {
                // caller will log at higher level if needed
            }
            return results;
        }

        foreach (var item in items)
        {
            // The link inside media-heading: <h4 class="media-heading"><a href="/anime/72/naruto">Naruto</a></h4>
            var linkNode = item.SelectSingleNode(".//h4[contains(@class, 'media-heading')]//a");
            if (linkNode is null)
            {
                continue;
            }

            var href = linkNode.GetAttributeValue("href", string.Empty);

            // Only include anime results (skip /manga/, /novel/, /drama/)
            if (!href.StartsWith("/anime/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = NormalizeWhitespace(linkNode.InnerText) ?? string.Empty;
            var id = ExtractId(href);

            // Thumbnail
            var imgNode = item.SelectSingleNode(".//img");
            var thumbnailUrl = imgNode?.GetAttributeValue("src", null);
            if (thumbnailUrl is not null && !thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                thumbnailUrl = baseUrl + thumbnailUrl;
            }

            // Year from <li>anno inizio: 2002</li>
            int? year = null;
            string? format = null;
            var liNodes = item.SelectNodes(".//li");
            if (liNodes is not null)
            {
                var yearLi = liNodes.FirstOrDefault(li =>
                    li.InnerText.Contains("anno inizio", StringComparison.OrdinalIgnoreCase));
                if (yearLi is not null)
                {
                    var match = FourDigitYearRegex().Match(yearLi.InnerText);
                    if (match.Success && int.TryParse(match.Value, out var y))
                    {
                        year = y;
                    }
                }

                foreach (var li in liNodes)
                {
                    var liText = NormalizeWhitespace(li.InnerText);
                    if (string.IsNullOrWhiteSpace(liText))
                    {
                        continue;
                    }

                    if (liText.Contains("Serie TV", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("TV", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("Movie", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("Film", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("Special", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("OVA", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("OAV", StringComparison.OrdinalIgnoreCase) ||
                        liText.Contains("ONA", StringComparison.OrdinalIgnoreCase))
                    {
                        format = liText;
                    }
                }
            }

            // Avoid duplicates by ID
            if (results.Any(r => r.Id == id))
            {
                continue;
            }

            results.Add(new AnimeClickSearchResult
            {
                Id = id,
                Title = title,
                Url = baseUrl + href,
                ThumbnailUrl = thumbnailUrl,
                ProductionYear = year,
                Format = format
            });
        }

        if (results.Count == 0 && html.Length > 300)
        {
            // Possible selector drift or empty-results page — higher layers can decide to log/warn.
        }

        return results;
    }

    // ── Helper: Extract ID+slug from URL (e.g. "72/naruto") ──

    private static string ExtractId(string url)
    {
        // Matches /anime/72/naruto or /anime/72
        var match = AnimeUrlIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : url;
    }

    private static string? ExtractEpisodeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = EpisodeUrlIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ToSafePersonImageUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, url, out var imageUri)
            || imageUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var baseHost = baseUri.IdnHost.TrimEnd('.');
        var imageHost = imageUri.IdnHost.TrimEnd('.');
        var exactConfiguredHost = string.Equals(baseHost, imageHost, StringComparison.OrdinalIgnoreCase)
            && baseUri.Port == imageUri.Port;
        var configuredForAnimeClick = string.Equals(baseHost, "animeclick.it", StringComparison.OrdinalIgnoreCase)
            || baseHost.EndsWith(".animeclick.it", StringComparison.OrdinalIgnoreCase);
        var officialAnimeClickHost = string.Equals(imageHost, "animeclick.it", StringComparison.OrdinalIgnoreCase)
            || imageHost.EndsWith(".animeclick.it", StringComparison.OrdinalIgnoreCase);

        return exactConfiguredHost || (configuredForAnimeClick && officialAnimeClickHost && imageUri.Port == 443)
            ? imageUri.AbsoluteUri
            : null;
    }

    // ── Helper: OG / meta tags ──

    private static string? Meta(HtmlDocument doc, string attr, string value)
        => doc.DocumentNode.SelectSingleNode($"//meta[@{attr}='{value}']")?.GetAttributeValue("content", null)?.Trim();

    private static string? Text(HtmlDocument doc, string xpath)
        => NormalizeWhitespace(doc.DocumentNode.SelectSingleNode(xpath)?.InnerText);

    // ── Helper: dl/dt/dd structure ──

    /// <summary>
    /// Finds a &lt;dt&gt; with the given label and returns the text of the following &lt;dd&gt;.
    /// </summary>
    private static string? DtDdValue(HtmlDocument doc, string label)
    {
        var dtNodes = doc.DocumentNode.SelectNodes("//dt");
        if (dtNodes is null) return null;

        foreach (var dt in dtNodes)
        {
            if (NormalizeWhitespace(dt.InnerText)?.Contains(label, StringComparison.OrdinalIgnoreCase) == true)
            {
                var dd = dt.SelectSingleNode("following-sibling::dd[1]");
                if (dd is null) continue;

                // Prefer itemprop span if present
                var itempropSpan = dd.SelectSingleNode(".//span[@itemprop]");
                if (itempropSpan is not null)
                {
                    return NormalizeWhitespace(itempropSpan.InnerText);
                }

                return NormalizeWhitespace(dd.InnerText);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a &lt;dt&gt; with the given label and returns the first link text from the following &lt;dd&gt;.
    /// </summary>
    private static string? DtDdLinkText(HtmlDocument doc, string label)
    {
        var dtNodes = doc.DocumentNode.SelectNodes("//dt");
        if (dtNodes is null) return null;

        foreach (var dt in dtNodes)
        {
            if (NormalizeWhitespace(dt.InnerText)?.Contains(label, StringComparison.OrdinalIgnoreCase) == true)
            {
                var dd = dt.SelectSingleNode("following-sibling::dd[1]");
                var link = dd?.SelectSingleNode(".//a");
                return link is not null ? NormalizeWhitespace(link.InnerText) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a &lt;dt&gt; with the given label and returns all link texts from the following &lt;dd&gt;.
    /// </summary>
    private static string[] DtDdLinks(HtmlDocument doc, string label)
    {
        var dtNodes = doc.DocumentNode.SelectNodes("//dt");
        if (dtNodes is null) return [];

        foreach (var dt in dtNodes)
        {
            if (NormalizeWhitespace(dt.InnerText)?.Contains(label, StringComparison.OrdinalIgnoreCase) == true)
            {
                var dd = dt.SelectSingleNode("following-sibling::dd[1]");
                var links = dd?.SelectNodes(".//a");
                if (links is null) return [];

                return links
                    .Select(a => NormalizeWhitespace(a.InnerText))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()!;
            }
        }

        return [];
    }

    // ── Helper: text normalization ──

    private static bool TryExtractYear(string? value, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = YearExtractRegex().Match(value);
        return match.Success && int.TryParse(match.Value, out year);
    }

    private static string? NormalizeDecimal(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Replace(',', '.');

    private static string? NormalizeWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex().Replace(HtmlEntity.DeEntitize(value), " ").Trim();

    // ── Episodes parsing ──

    /// <summary>
    /// Parses the episodes page (/anime/{id}/episodi).
    /// Extracts episode numbers and Italian titles from the table structure.
    /// </summary>
    public List<AnimeClickEpisode> ParseEpisodesPage(string html, string baseUrl)
        => ParseEpisodesPage(html, baseUrl, seasonsCount: null);

    /// <summary>
    /// Parses the /episodi table. When <paramref name="seasonsCount"/> is provided and strictly
    /// greater than 1, and every parsed episode carries a null <see cref="AnimeClickEpisode.SeasonNumber"/>
    /// (i.e. AnimeClick lists the episodes as a continuous "Ep. 01"..<c>Ep. NN</c> block without per-row
    /// <c>S1/S2 Ep.</c> prefixes), the parser synthesises the season number so the matcher's
    /// <c>seasonOrdinal</c> branch can resolve multi-season Jellyfin libraries.
    /// </summary>
    public List<AnimeClickEpisode> ParseEpisodesPage(string html, string baseUrl, int? seasonsCount)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var episodes = new List<AnimeClickEpisode>();

        // Episodes are listed in a table: <table class="table ...">
        // Each row: <td>S1 Ep. 01</td><td><a href="/episodio/...">Titolo</a></td><td>24'</td>
        var rows = doc.DocumentNode.SelectNodes("//table[contains(@class, 'table')]//tbody//tr")
                   ?? doc.DocumentNode.SelectNodes("//table//tr[td]");
        if (rows is null) return episodes;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells is null || cells.Count < 2) continue;

            // First cell: episode number (e.g. "S1 Ep. 01" or just "1")
            var numText = NormalizeWhitespace(cells[0].InnerText);
            if (string.IsNullOrWhiteSpace(numText)) continue;

            int? seasonNumber = null;
            int epNum;
            var seasonMatch = SeasonEpisodeRegex().Match(numText);
            if (seasonMatch.Success)
            {
                seasonNumber = int.TryParse(seasonMatch.Groups[1].Value, out var s) ? s : null;
                epNum = int.TryParse(seasonMatch.Groups[2].Value, out var e) ? e : 0;
            }
            else
            {
                var numMatch = EpisodeNumberRegex().Match(numText);
                if (!numMatch.Success || !int.TryParse(numMatch.Groups[1].Value, out epNum)) continue;
            }

            // Second cell: title with link
            var titleLink = cells[1].SelectSingleNode(".//a");
            var title = NormalizeWhitespace(titleLink?.InnerText ?? cells[1].InnerText);

            var detailUrl = titleLink?.GetAttributeValue("href", null);
            var episodeProviderId = ExtractEpisodeId(detailUrl);
            if (detailUrl is not null && !detailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                detailUrl = baseUrl + detailUrl;
            }

            // Third cell (optional): duration
            int? duration = null;
            if (cells.Count >= 3)
            {
                var durText = NormalizeWhitespace(cells[2].InnerText);
                var durMatch = DigitsRegex().Match(durText ?? "");
                if (durMatch.Success && int.TryParse(durMatch.Value, out var dur))
                {
                    duration = dur;
                }
            }

            // Auto-detect special episodes (OVAs, Specials, Bonus) by title
            // when AnimeClick page doesn't provide season grouping
            if (!seasonNumber.HasValue && IsSpecialEpisodeTitle(title))
            {
                seasonNumber = 0;
            }

            // Avoid duplicates: use (Season, Number) pair when season info is available
            if (episodes.Any(e => e.SeasonNumber == seasonNumber && e.Number == epNum)) continue;

            episodes.Add(new AnimeClickEpisode
            {
                SeasonNumber = seasonNumber,
                Number = epNum,
                AbsoluteNumber = epNum,
                SeasonOrdinalNumber = epNum,
                Title = title,
                DetailUrl = detailUrl,
                ProviderId = episodeProviderId,
                DurationMinutes = duration
            });
        }

        FinalizeEpisodeList(episodes, seasonsCount);
        return episodes;
    }

    /// <summary>
    /// Recomputes ordinals and optional synthetic seasons for a complete episode list.
    /// Callers that merge paginated tables must invoke this only after all pages have
    /// been collected; normalizing each page independently shifts season ordinals.
    /// </summary>
    internal static void FinalizeEpisodeList(List<AnimeClickEpisode> episodes, int? seasonsCount)
    {
        NormalizeEpisodeOrdinals(episodes);
        TryInferSeasonsFromCount(episodes, seasonsCount);
    }

    internal static bool IsSpecialEpisodeTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && SpecialTitleRegex().IsMatch(title);

    private static void NormalizeEpisodeOrdinals(List<AnimeClickEpisode> episodes)
    {
        foreach (var group in episodes.GroupBy(e => e.SeasonNumber))
        {
            var ordered = group
                .OrderBy(e => e.AbsoluteNumber > 0 ? e.AbsoluteNumber : e.Number)
                .ThenBy(e => e.Title)
                .ToList();

            if (ordered.Count == 0)
            {
                continue;
            }

            var firstNumber = ordered[0].AbsoluteNumber > 0 ? ordered[0].AbsoluteNumber : ordered[0].Number;
            for (var i = 0; i < ordered.Count; i++)
            {
                var episode = ordered[i];
                episode.AbsoluteNumber = episode.AbsoluteNumber > 0 ? episode.AbsoluteNumber : episode.Number;

                if (episode.SeasonNumber.HasValue)
                {
                    var relative = episode.AbsoluteNumber - firstNumber + 1;
                    episode.SeasonOrdinalNumber = relative > 0 ? relative : i + 1;
                }
                else
                {
                    episode.SeasonOrdinalNumber = episode.Number;
                }
            }
        }
    }

    /// <summary>
    /// When <paramref name="seasonsCount"/> is greater than 1 and every parsed episode
    /// carries a null <see cref="AnimeClickEpisode.SeasonNumber"/> (i.e. AnimeClick lists
    /// episodes as a continuous "Ep. 01"..<c>Ep. NN</c> block without per-row "S1/S2 Ep." prefixes),
    /// splits the episodes evenly across N seasons and assigns both the synthetic
    /// <see cref="AnimeClickEpisode.SeasonNumber"/> and the within-group ordinal
    /// (<see cref="AnimeClickEpisode.SeasonOrdinalNumber"/>) so the matcher's
    /// <c>seasonOrdinal</c> branch can resolve multi-season Jellyfin libraries.
    /// </summary>
    private static void TryInferSeasonsFromCount(List<AnimeClickEpisode> episodes, int? seasonsCount)
    {
        if (episodes.Count == 0
            || !seasonsCount.HasValue
            || seasonsCount.Value <= 1
            || episodes.Any(e => e.SeasonNumber.HasValue))
        {
            return;
        }

        var count = episodes.Count;
        var perSeason = count / seasonsCount.Value;
        if (perSeason <= 0 || count % seasonsCount.Value != 0)
        {
            // Refuse to synthesise when the table does not divide evenly across declared seasons,
            // because that would shift ordinals and corrupt matching.
            return;
        }

        var ordered = episodes.OrderBy(e => e.AbsoluteNumber).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var season = (i / perSeason) + 1;
            var ordinalInSeason = (i % perSeason) + 1;
            ordered[i].SeasonNumber = season;
            ordered[i].SeasonNumberIsSynthetic = true;
            ordered[i].SeasonOrdinalNumber = ordinalInSeason;
        }
    }

    // ── Relations parsing ──

    /// <summary>
    /// Parses the relations page (/anime/{id}/relazioni).
    /// Extracts related works (sequel, prequel, spin-off, etc.).
    /// </summary>
    public List<AnimeClickRelation> ParseRelationsPage(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var relations = new List<AnimeClickRelation>();

        // Structure: <div class="media"> containing:
        //   <h4/h5 class="media-heading"><a href="/anime/561/naruto-shippuden">Title</a></h4>
        //   <span class="label label-success">Sequel</span>
        var mediaBlocks = doc.DocumentNode.SelectNodes("//div[contains(@class, 'media')]");
        if (mediaBlocks is null) return relations;

        foreach (var block in mediaBlocks)
        {
            var headingLink = block.SelectSingleNode(".//*[self::h4 or self::h5][contains(@class, 'media-heading')]//a");
            if (headingLink is null) continue;

            var title = NormalizeWhitespace(headingLink.InnerText);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var href = headingLink.GetAttributeValue("href", string.Empty);
            // Only include anime relations (skip manga, novel, etc.)
            if (!href.Contains("/anime/", StringComparison.OrdinalIgnoreCase)) continue;

            var id = ExtractId(href);

            // Relation type is currently rendered as opera-tipo-relazione;
            // retain the older label selector as a compatibility fallback.
            var relationNode = block.SelectSingleNode(".//span[contains(@class, 'opera-tipo-relazione')]")
                ?? block.SelectSingleNode(".//span[contains(@class, 'label')]");
            var relationType = NormalizeWhitespace(relationNode?.InnerText) ?? "Correlato";

            // Try to extract year and format from <p> or <span> in description/media-body
            int? year = null;
            string? format = null;
            var infoNodes = block.SelectNodes(".//div[contains(@class, 'media-body')]//p")
                         ?? block.SelectNodes(".//div[contains(@class, 'media-body')]//span")
                         ?? block.SelectNodes(".//div[contains(@class, 'description')]//span")
                         ?? block.SelectNodes(".//span");
            if (infoNodes is not null)
            {
                foreach (var node in infoNodes)
                {
                    var text = NormalizeWhitespace(node.InnerText) ?? "";
                    var yearMatch = YearExtractRegex().Match(text);
                    if (yearMatch.Success && int.TryParse(yearMatch.Value, out var y))
                    {
                        year = y;
                    }
                    else if (text.Contains("Serie TV", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("Film", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("OVA", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("OAV", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("Special", StringComparison.OrdinalIgnoreCase))
                    {
                        format = text;
                    }
                }
            }

            // Avoid duplicates
            if (relations.Any(r => r.AnimeClickId == id)) continue;

            relations.Add(new AnimeClickRelation
            {
                Title = title,
                AnimeClickId = id,
                Url = baseUrl + href,
                RelationType = relationType,
                Year = year,
                Format = format
            });
        }

        return relations;
    }

    // ── Multimedia / Theme Songs parsing ──

    /// <summary>
    /// Parses the multimedia page (/anime/{id}/multimedia).
    /// Extracts opening/ending theme song titles.
    /// Pattern: "Anime Name - Opening 1 | Song Title"
    /// </summary>
    public List<AnimeClickThemeSong> ParseMultimediaPage(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var songs = new List<AnimeClickThemeSong>();

        // Look for h4 or h3 headings containing "Opening" or "Ending"
        // Pattern: "Naruto - Opening 1 | Rocks" or "Naruto - Ending 10 | Speed"
        var headings = doc.DocumentNode.SelectNodes("//h4 | //h3 | //h5");
        if (headings is null) return songs;

        foreach (var heading in headings)
        {
            var text = NormalizeWhitespace(heading.InnerText);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Match pattern: "... Opening N | Title" or "... Ending N | Title"
            var match = ThemeSongRegex().Match(text);
            if (!match.Success) continue;

            var type = match.Groups[1].Value;
            var number = int.TryParse(match.Groups[2].Value, out var num) ? num : 1;
            var songPart = match.Groups[3].Value.Trim();

            // Try to split "Song Title - Artist" or just "Song Title"
            string title;
            string? artist = null;
            var dashIndex = songPart.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dashIndex > 0)
            {
                title = songPart[..dashIndex].Trim();
                artist = songPart[(dashIndex + 3)..].Trim();
            }
            else
            {
                title = songPart;
            }

            // Avoid duplicates
            var typeNormalized = type.Contains("Opening", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending";
            if (songs.Any(s => s.Type == typeNormalized && s.Number == number)) continue;

            songs.Add(new AnimeClickThemeSong
            {
                Type = typeNormalized,
                Number = number,
                Title = title,
                Artist = artist
            });
        }

        return songs;
    }

    public AnimeClickMultimediaDiagnostics ParseMultimediaDiagnostics(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var trailers = new List<AnimeClickTrailer>();
        var videoNodes = doc.DocumentNode.SelectNodes("//a[@href] | //iframe[@src] | //embed[@src]");
        if (videoNodes is not null)
        {
            foreach (var node in videoNodes)
            {
                var rawUrl = node.GetAttributeValue("href", null)
                    ?? node.GetAttributeValue("src", null);
                if (string.IsNullOrWhiteSpace(rawUrl)
                    || (!rawUrl.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                        && !rawUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var label = NormalizeWhitespace(node.GetAttributeValue("title", null))
                    ?? NormalizeWhitespace(node.GetAttributeValue("aria-label", null))
                    ?? NormalizeWhitespace(node.InnerText)
                    ?? NormalizeWhitespace(node.SelectSingleNode(
                        "preceding::*[self::h2 or self::h3 or self::h4 or self::h5][1]")?.InnerText);
                if (string.IsNullOrWhiteSpace(label) || !IsTrailerLabel(label))
                {
                    // Do not turn openings, endings or arbitrary clips into Jellyfin trailers.
                    continue;
                }

                var normalizedUrl = NormalizeYouTubeUrl(rawUrl);
                if (normalizedUrl is null
                    || trailers.Any(trailer => string.Equals(
                        trailer.Url,
                        normalizedUrl,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                trailers.Add(new AnimeClickTrailer
                {
                    Name = label,
                    Url = normalizedUrl
                });
            }
        }

        var songs = ParseMultimediaPage(html);
        var warning = songs.Count == 0 && trailers.Count > 0
            ? "La pagina multimedia espone trailer/PV ma non dati OP/ED strutturati per questa scheda."
            : null;

        return new AnimeClickMultimediaDiagnostics
        {
            Songs = songs,
            Trailers = trailers,
            HasTrailerOrPvOnly = warning is not null,
            Warning = warning
        };
    }

    private static bool IsTrailerLabel(string label)
        => label.Contains("Trailer", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Teaser", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Promo", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(label, @"\bPV\s*\d*\b", RegexOptions.IgnoreCase);

    private static string? NormalizeYouTubeUrl(string url)
    {
        var candidate = url.Trim().Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = "https:" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var isShortHost = host == "youtu.be" || host == "www.youtu.be";
        var isYouTubeHost = host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
            or "music.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com";
        if (!isShortHost && !isYouTubeHost)
        {
            return null;
        }

        string? videoId = null;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (isShortHost && segments.Length > 0)
        {
            videoId = segments[0];
        }
        else if (segments.Length > 1
            && (segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)))
        {
            videoId = segments[1];
        }
        else if (segments.Length > 0 && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            var queryMatch = Regex.Match(uri.Query, @"(?:^|[?&])v=(?<id>[A-Za-z0-9_-]{6,20})(?:&|$)");
            if (queryMatch.Success)
            {
                videoId = queryMatch.Groups["id"].Value;
            }
        }

        return videoId is not null && Regex.IsMatch(videoId, @"^[A-Za-z0-9_-]{6,20}$")
            ? $"https://www.youtube.com/watch?v={videoId}"
            : null;
    }
}

/// <summary>
/// Intermediate model for search results before mapping to Jellyfin's RemoteSearchResult.
/// </summary>
public class AnimeClickSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public int? ProductionYear { get; set; }
    public string? Format { get; set; }
}

public class AnimeClickMultimediaDiagnostics
{
    public List<AnimeClickThemeSong> Songs { get; set; } = [];
    public List<AnimeClickTrailer> Trailers { get; set; } = [];
    public bool HasTrailerOrPvOnly { get; set; }
    public string? Warning { get; set; }
}
