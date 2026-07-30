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

    [GeneratedRegex(@"(?:S(?:tagione)?\s*(\d+)\s*(?:E|Ep(?:isodio)?\.?)\s*(.+)|(\d+)\s*[xX]\s*(.+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?<!\d)(\d+)(?:[\.,](\d+))?([A-Za-z])?(?:\s*[-–/]\s*(\d+))?")]
    private static partial Regex EpisodeTokenRegex();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"(Opening|Ending)\s+(\d+)\s*\|\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThemeSongRegex();

    [GeneratedRegex(@"myanimelist\.net/anime/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MalIdRegex();

    [GeneratedRegex(@"anilist\.co/anime/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AniListIdRegex();

    [GeneratedRegex(@"anidb\.net/(?:a|anime/)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AniDbIdRegex();

    // "Special[ei]?" also covers the Italian "Speciale"/"Speciali", which plain \bSpecial\b
    // does not: the word boundary fails before the trailing vowel, so rows labelled
    // "Speciale ..." used to be classified as regular episodes.
    [GeneratedRegex(@"\b(Special[ei]?|SP|OAV|OVA|OAD|ONA|PV|NCOP|NCED|Recap|Riassunto|Riepilogo|Sigla|Episode\s*0|Episodio\s*0|Prologo|Pilot|Bonus|Extra)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpecialTitleRegex();

    /// <summary>Matches an explicit episode marker followed by a number, e.g. "Ep. 01".</summary>
    [GeneratedRegex(@"\b(?:E|Ep|Eps|Episodio|Episode|Puntata)\.?\s*#?\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarkerRegex();

    // Covers both an episode overview and an episode title that carry no information beyond the
    // number. The episode provider used to guard titles with its own, narrower pattern requiring
    // a space and rejecting nothing else: "Episodio 11." , "Ep.11", "Episodio 11-12",
    // "Episodio #11" and "Episode 3!" all got written into the library as titles. One concept,
    // one pattern. "Puntata" is included because AnimeClick uses it too.
    [GeneratedRegex(@"^(?:Episodio|Episode|Ep\.?|Puntata)\s*#?\s*\d+(?:[\.,]\d+)?(?:\s*[-–/]\s*\d+)?[\.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePlaceholderTextRegex();

    /// <summary>
    /// True when the text is a bare episode designation such as "Episodio 11" or "Ep.11-12",
    /// i.e. it adds nothing to the number Jellyfin already has. Used to refuse it both as an
    /// overview and as a title.
    /// </summary>
    public static bool IsPlaceholderEpisodeText(string? text)
        => !string.IsNullOrWhiteSpace(text) && EpisodePlaceholderTextRegex().IsMatch(text.Trim());

    /// <summary>
    /// Parses an AnimeClick episode detail page. Only the schema.org description is
    /// trusted: surrounding page text may contain user comments and must not become
    /// Jellyfin metadata.
    /// </summary>
    public string? ParseEpisodeOverviewPage(string html)
    {
        TryParseEpisodeOverviewPage(html, out var overview);
        return overview;
    }

    /// <summary>
    /// Returns true only when the expected description node exists. An empty or
    /// placeholder description is therefore a recognized miss, while an interstitial
    /// or changed page shape remains retryable and must not enter negative cache.
    /// </summary>
    internal bool TryParseEpisodeOverviewPage(string html, out string? overview)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var descriptionNode = doc.DocumentNode.SelectSingleNode("//*[@itemprop='description']");
        if (descriptionNode is null)
        {
            overview = null;
            return false;
        }

        overview = NormalizeWhitespace(descriptionNode.InnerText);
        if (string.IsNullOrWhiteSpace(overview) || IsPlaceholderEpisodeText(overview))
        {
            overview = null;
        }

        return true;
    }

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
        if (ratingCountNode is not null)
        {
            // Strip thousands separators/labels: rating count is a plain integer count.
            var ratingCountDigits = new string(ratingCountNode.InnerText.Where(char.IsAsciiDigit).ToArray());
            if (ratingCountDigits.Length > 0
                && int.TryParse(ratingCountDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var ratingCount))
            {
                anime.RatingCount = ratingCount;
            }
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
        if (!string.IsNullOrWhiteSpace(episodesText))
        {
            // Take the first digit run so decorated values ("24 ep", "24 + 2 special") still parse.
            var episodesMatch = DigitsRegex().Match(episodesText);
            if (episodesMatch.Success
                && int.TryParse(episodesMatch.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epCount))
            {
                anime.EpisodeCount = epCount;
            }
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
                imageUrl = ToSafeImageUrl(baseUrl, imageUrl);

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
                imageUrl = ToSafeImageUrl(baseUrl, imageUrl);

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

            // Thumbnail. Validated against the configured host: this URL is handed to Jellyfin
            // as RemoteSearchResult.ImageUrl and fetched server-side, so an absolute src
            // pointing anywhere would otherwise become an arbitrary outbound request.
            var imgNode = item.SelectSingleNode(".//img");
            var thumbnailUrl = ToSafeImageUrl(baseUrl, imgNode?.GetAttributeValue("src", null));

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

    /// <summary>
    /// Resolves a scraped image URL against the configured base and confirms it targets an
    /// allowed host over HTTPS, returning null otherwise. Every image URL the parser hands
    /// back is fetched server-side by Jellyfin, so none of them may point anywhere else.
    /// Delegates to <see cref="AnimeClickClient.TryResolveAllowedImageUri"/>: the same
    /// allow-list used to exist twice, once here and once there, which is one copy too many
    /// for a security check.
    /// </summary>
    private static string? ToSafeImageUrl(string baseUrl, string? url)
        => AnimeClickClient.TryResolveAllowedImageUri(baseUrl, url, out var imageUri)
            ? imageUri.AbsoluteUri
            : null;

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
    /// <para>
    /// <b>Not used in production.</b> <see cref="AnimeClickEpisodeListLoader"/> deliberately always
    /// passes <c>null</c> here and to <see cref="FinalizeEpisodeList"/>, because inferred season
    /// boundaries must not be persisted: they would go stale the moment Jellyfin's own layout
    /// changes from 1x24 to 13+11. The equal-split decision is taken at match time instead, from
    /// <see cref="AnimeClickEpisodeCatalog.DeclaredSeasonsCount"/>. This overload therefore only
    /// runs from the test suite, and the <c>SeasonNumberIsSynthetic</c> branches of the matcher are
    /// unreachable in a running server. Kept as the documented legacy path rather than deleted,
    /// since removing it also means removing matcher branches: worth doing, but as its own change
    /// with its own verification, not folded into a cleanup pass.
    /// </para>
    /// </summary>
    public List<AnimeClickEpisode> ParseEpisodesPage(string html, string baseUrl, int? seasonsCount)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var episodes = new List<AnimeClickEpisode>();

        // AnimeClick has used several labels over time: "S1 Ep. 01", "S01E01",
        // "1x01", plain absolute numbers, OVA/SP rows and decimal/range values.
        var rows = doc.DocumentNode.SelectNodes("//table[contains(@class, 'table')]//tbody//tr")
                   ?? doc.DocumentNode.SelectNodes("//table//tr[td]");
        if (rows is null) return episodes;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells is null || cells.Count < 2) continue;

            var rawLabel = NormalizeWhitespace(cells[0].InnerText);
            if (string.IsNullOrWhiteSpace(rawLabel)) continue;

            ParseEpisodeLabel(
                rawLabel,
                out var seasonNumber,
                out var episodeNumber,
                out var episodeNumberEnd,
                out var hasNonStandardNumber);

            var titleLink = cells[1].SelectSingleNode(".//a");
            var title = NormalizeWhitespace(titleLink?.InnerText ?? cells[1].InnerText);
            var detailUrl = titleLink?.GetAttributeValue("href", null);
            var episodeProviderId = ExtractEpisodeId(detailUrl);
            if (detailUrl is not null && !detailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                detailUrl = baseUrl + detailUrl;
            }

            int? duration = null;
            if (cells.Count >= 3)
            {
                var durText = NormalizeWhitespace(cells[2].InnerText);
                var durMatch = DigitsRegex().Match(durText ?? string.Empty);
                if (durMatch.Success && int.TryParse(durMatch.Value, out var dur))
                {
                    duration = dur;
                }
            }

            var hasTrustworthyRegularLabel = HasTrustworthyRegularLabel(
                rawLabel,
                seasonNumber,
                episodeNumber,
                hasNonStandardNumber);
            var isSpecial = seasonNumber == 0
                || episodeNumber <= 0
                || hasNonStandardNumber
                || IsSpecialEpisodeTitle(rawLabel)
                || (!hasTrustworthyRegularLabel && IsSpecialEpisodeTitle(title));
            // An unseasoned range such as "Ep. 01-02" is non-standard but still belongs
            // to the regular season coordinate space so IndexNumberEnd can match it.
            if (isSpecial && !seasonNumber.HasValue && !episodeNumberEnd.HasValue)
            {
                seasonNumber = 0;
            }

            var duplicate = !string.IsNullOrWhiteSpace(episodeProviderId)
                ? episodes.Any(episode => string.Equals(
                    episode.ProviderId,
                    episodeProviderId,
                    StringComparison.OrdinalIgnoreCase))
                : episodes.Any(episode =>
                    string.Equals(episode.RawNumberLabel, rawLabel, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(episode.Title, title, StringComparison.OrdinalIgnoreCase));
            if (duplicate) continue;

            episodes.Add(new AnimeClickEpisode
            {
                SeasonNumber = seasonNumber,
                RawSeasonNumber = seasonNumber,
                RawNumberLabel = rawLabel,
                Number = episodeNumber,
                RawEpisodeNumber = episodeNumber > 0 || !hasNonStandardNumber
                    ? episodeNumber
                    : null,
                NumberEnd = episodeNumberEnd,
                HasNonStandardNumber = hasNonStandardNumber,
                IsSpecial = isSpecial,
                SourceOrder = episodes.Count + 1,
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
    /// Recomputes canonical ordinals and optional legacy equal-split hints. Callers that
    /// merge pages invoke this only after the complete table has been collected.
    /// <paramref name="seasonsCount"/> is always null in production — see the remarks on
    /// <see cref="ParseEpisodesPage(string, string, int?)"/>.
    /// </summary>
    internal static void FinalizeEpisodeList(List<AnimeClickEpisode> episodes, int? seasonsCount)
    {
        CanonicalizeEpisodeTimeline(episodes);
        TryInferSeasonsFromCount(episodes, seasonsCount);
    }

    internal static bool IsSpecialEpisodeTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && SpecialTitleRegex().IsMatch(title);

    private static bool HasTrustworthyRegularLabel(
        string? rawLabel,
        int? seasonNumber,
        int episodeNumber,
        bool hasNonStandardNumber)
        => seasonNumber != 0
            && episodeNumber > 0
            && !hasNonStandardNumber
            && !IsSpecialEpisodeTitle(rawLabel);

    private static void ParseEpisodeLabel(
        string rawLabel,
        out int? seasonNumber,
        out int episodeNumber,
        out int? episodeNumberEnd,
        out bool hasNonStandardNumber)
    {
        seasonNumber = null;
        episodeNumber = 0;
        episodeNumberEnd = null;
        hasNonStandardNumber = false;
        var numberText = rawLabel;

        var seasonMatch = SeasonEpisodeRegex().Match(rawLabel);
        if (seasonMatch.Success)
        {
            var seasonValue = seasonMatch.Groups[1].Success
                ? seasonMatch.Groups[1].Value
                : seasonMatch.Groups[3].Value;
            if (int.TryParse(seasonValue, out var parsedSeason))
            {
                seasonNumber = parsedSeason;
            }

            numberText = seasonMatch.Groups[2].Success
                ? seasonMatch.Groups[2].Value
                : seasonMatch.Groups[4].Value;
        }

        var numberMatch = EpisodeTokenRegex().Match(numberText);
        if (!numberMatch.Success || !int.TryParse(numberMatch.Groups[1].Value, out episodeNumber))
        {
            hasNonStandardNumber = true;
            return;
        }

        // EpisodeTokenRegex takes the first run of digits wherever it sits, so a label with no
        // season and no episode marker whose first number is a calendar year ("Speciale
        // natalizio 2015") used to become episode 2015. Treat it as non-standard instead: it is
        // then classified as a special and never enters the canonical timeline, where it would
        // push the following episodes down a slot on a page that mixes seasoned and unseasoned
        // rows, and would break the equal-split hint on a flat one. A label that is *only* the
        // number is still honoured, so genuinely long-running series keep working.
        if (!seasonMatch.Success
            && LooksLikeCalendarYear(numberMatch.Groups[1].Value)
            && !string.Equals(rawLabel.Trim(), numberMatch.Value, StringComparison.Ordinal)
            && !EpisodeMarkerRegex().IsMatch(rawLabel))
        {
            episodeNumber = 0;
            hasNonStandardNumber = true;
            return;
        }

        var hasFraction = numberMatch.Groups[2].Success;
        var hasSuffix = numberMatch.Groups[3].Success;
        if (numberMatch.Groups[4].Success
            && int.TryParse(numberMatch.Groups[4].Value, out var parsedEnd))
        {
            episodeNumberEnd = parsedEnd;
        }

        hasNonStandardNumber = hasFraction || hasSuffix || episodeNumberEnd.HasValue;
    }

    /// <summary>Four digits in the plausible broadcast-year range, e.g. "2015".</summary>
    private static bool LooksLikeCalendarYear(string digits)
        => digits.Length == 4
           && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
           && value is >= 1900 and <= 2099;

    /// <summary>
    /// Builds canonical coordinates. When every regular row has an unambiguous numeric
    /// coordinate, numeric order repairs reversed/out-of-order tables; otherwise source
    /// order remains the only safe evidence. Specials never shift regular ordinals.
    /// </summary>
    internal static void CanonicalizeEpisodeTimeline(List<AnimeClickEpisode> episodes)
    {
        for (var index = 0; index < episodes.Count; index++)
        {
            var episode = episodes[index];
            episode.SourceOrder = index + 1;
            episode.RawSeasonNumber ??= episode.SeasonNumber;
            episode.RawEpisodeNumber ??= episode.Number > 0 ? episode.Number : null;
            episode.SeasonNumber = episode.RawSeasonNumber;
            episode.SeasonNumberIsSynthetic = false;
            episode.AbsoluteNumber = episode.Number;
            episode.GlobalOrdinal = 0;
            episode.SeasonOrdinalNumber = 0;
            episode.SpecialOrdinalNumber = 0;
            episode.NumberIsAmbiguous = false;
            episode.IsSpecial = episode.RawSeasonNumber == 0
                || episode.Number <= 0
                || episode.HasNonStandardNumber
                || IsSpecialEpisodeTitle(episode.RawNumberLabel)
                || (!HasTrustworthyRegularLabel(
                        episode.RawNumberLabel,
                        episode.RawSeasonNumber,
                        episode.Number,
                        episode.HasNonStandardNumber)
                    && IsSpecialEpisodeTitle(episode.Title));
        }

        foreach (var duplicateGroup in episodes
                     .Where(episode => episode.RawEpisodeNumber.HasValue)
                     .GroupBy(episode => (
                         episode.IsSpecial,
                         episode.RawSeasonNumber,
                         episode.RawEpisodeNumber))
                     .Where(group => group.Count() > 1))
        {
            foreach (var duplicate in duplicateGroup)
            {
                duplicate.NumberIsAmbiguous = true;
            }
        }

        var regular = episodes
            .Where(episode => !episode.IsSpecial && episode.RawEpisodeNumber is > 0)
            .ToList();
        var globalTimelineReliable = regular.All(episode => !episode.NumberIsAmbiguous);
        IEnumerable<AnimeClickEpisode> canonicalOrder = regular.OrderBy(episode => episode.SourceOrder);
        if (globalTimelineReliable
            && regular.Count > 0
            && regular.All(episode => episode.RawSeasonNumber is > 0))
        {
            canonicalOrder = regular
                .OrderBy(episode => episode.RawSeasonNumber)
                .ThenBy(episode => episode.RawEpisodeNumber)
                .ThenBy(episode => episode.SourceOrder);
        }
        else if (globalTimelineReliable
                 && regular.Count > 0
                 && regular.All(episode => !episode.RawSeasonNumber.HasValue))
        {
            canonicalOrder = regular
                .OrderBy(episode => episode.RawEpisodeNumber)
                .ThenBy(episode => episode.SourceOrder);
        }

        regular = canonicalOrder.ToList();
        if (globalTimelineReliable)
        {
            for (var index = 0; index < regular.Count; index++)
            {
                regular[index].GlobalOrdinal = index + 1;
                regular[index].AbsoluteNumber = index + 1;
            }
        }

        foreach (var group in regular.GroupBy(episode => episode.RawSeasonNumber))
        {
            if (group.Any(episode => episode.NumberIsAmbiguous))
            {
                continue;
            }

            var ordered = group
                .OrderBy(episode => episode.RawEpisodeNumber)
                .ThenBy(episode => episode.SourceOrder)
                .ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].SeasonOrdinalNumber = index + 1;
            }
        }

        var specials = episodes
            .Where(episode => episode.IsSpecial)
            .OrderBy(episode => episode.SourceOrder)
            .ToList();
        for (var index = 0; index < specials.Count; index++)
        {
            specials[index].SpecialOrdinalNumber = index + 1;
            specials[index].AbsoluteNumber = specials[index].Number;
        }
    }

    /// <summary>
    /// Retains the v4 equal-split hint for callers without Jellyfin topology. Only regular
    /// rows participate: an OVA or recap no longer disables or shifts the split.
    /// </summary>
    private static void TryInferSeasonsFromCount(List<AnimeClickEpisode> episodes, int? seasonsCount)
    {
        if (!seasonsCount.HasValue || seasonsCount.Value <= 1)
        {
            return;
        }

        var regular = episodes
            .Where(episode => !episode.IsSpecial && episode.GlobalOrdinal > 0)
            .OrderBy(episode => episode.GlobalOrdinal)
            .ToList();
        if (regular.Count == 0 || regular.Any(episode => episode.RawSeasonNumber.HasValue))
        {
            return;
        }

        var perSeason = regular.Count / seasonsCount.Value;
        if (perSeason <= 0 || regular.Count % seasonsCount.Value != 0)
        {
            return;
        }

        for (var index = 0; index < regular.Count; index++)
        {
            regular[index].SeasonNumber = (index / perSeason) + 1;
            regular[index].SeasonNumberIsSynthetic = true;
            regular[index].SeasonOrdinalNumber = (index % perSeason) + 1;
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
            || Regex.IsMatch(label, @"\bPV\s*\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
