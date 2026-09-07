using System.Linq;
using AnimeClick.Plugin.Services;
using Xunit;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// Il parser contro l'HTML che il sito serve davvero (vedi <see cref="RealSiteFixtures"/>).
///
/// Ogni test qui e' nato rosso: AnimeClick e' passato da Bootstrap 3 a Bootstrap 5
/// (<c>div.well</c> → <c>div.card</c>, <c>h4.media-heading</c> → <c>h4.mo-heading</c>,
/// e sulla pagina di ricerca l'h4 ha perso ogni classe) e il parser cercava ancora
/// le classi vecchie. In produzione questo significava zero candidati su ogni ricerca,
/// quindi nessun film nuovo poteva piu' essere riconosciuto.
/// </summary>
public class AnimeClickRealSiteParsingTests
{
    private const string BaseUrl = "https://www.animeclick.it";

    // ── Ricerca ──

    [Fact]
    public void ParseSearchResults_TrovaGliAnimeNellaPaginaVera()
    {
        var results = new AnimeClickHtmlParser().ParseSearchResults(RealSiteFixtures.Search, BaseUrl);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id.StartsWith("25493", System.StringComparison.Ordinal));
        Assert.Contains(results, r => r.Id.StartsWith("23809", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ParseSearchResults_LeggeTitoloAnnoEFormato()
    {
        var results = new AnimeClickHtmlParser().ParseSearchResults(RealSiteFixtures.Search, BaseUrl);

        var film = Assert.Single(results.Where(r => r.Id.StartsWith("25493", System.StringComparison.Ordinal)));
        Assert.Equal("Rascal Does Not Dream of a Dreaming Girl", film.Title);
        Assert.Equal(2019, film.ProductionYear);
        Assert.Contains("Film", film.Format ?? string.Empty, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSearchResults_ScartaMangaNovelENews()
    {
        // La stessa ricerca restituisce anche /manga/, /novel/ e /news/: sono 9 delle 11 voci.
        var results = new AnimeClickHtmlParser().ParseSearchResults(RealSiteFixtures.Search, BaseUrl);

        Assert.All(results, r => Assert.Contains("/anime/", r.Url, System.StringComparison.Ordinal));
    }

    [Fact]
    public void ParseSearchResults_IlFilmSopravviveAlFiltroFormatoDeiFilm()
    {
        // Il percorso che in produzione tornava vuoto: cercare un film e restituirlo.
        var results = new AnimeClickHtmlParser()
            .ParseSearchResults(RealSiteFixtures.Search, BaseUrl)
            .Where(r => AnimeClickSearchScorer.IsFormatCompatible(r, seriesRequest: false))
            .ToList();

        Assert.Contains(results, r => r.Id.StartsWith("25493", System.StringComparison.Ordinal));
    }

    // ── Scheda anime ──

    [Fact]
    public void ParseAnimePage_LeggeTitoloTramaAnnoEGeneri()
    {
        var anime = new AnimeClickHtmlParser()
            .ParseAnimePage($"{BaseUrl}/anime/25493/seishun-buta-film", RealSiteFixtures.AnimePage);

        Assert.Equal("Rascal Does Not Dream of a Dreaming Girl", anime.Title);
        Assert.Equal(2019, anime.ProductionYear);
        Assert.NotNull(anime.Overview);
        Assert.Contains("Sakuta", anime.Overview!, System.StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(anime.Genres);
    }

    [Fact]
    public void ParseAnimePage_LeggeIlTitoloOriginaleRomaji()
    {
        var anime = new AnimeClickHtmlParser()
            .ParseAnimePage($"{BaseUrl}/anime/25493/seishun-buta-film", RealSiteFixtures.AnimePage);

        Assert.Equal(
            "Seishun Buta Yarou wa Yumemiru Shoujo no Yume o Minai",
            anime.OriginalTitle);
    }

    // ── Personaggi, staff, relazioni ──

    [Fact]
    public void ParseCharactersPage_LeggePersonaggiEDoppiatori()
    {
        var people = new AnimeClickHtmlParser()
            .ParseCharactersPage(RealSiteFixtures.CharactersPage, BaseUrl);

        Assert.NotEmpty(people);
        var sakuta = Assert.Single(people.Where(p =>
            p.Role is not null && p.Role.Contains("Sakuta Azusagawa", System.StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Kaito Ishikawa", sakuta.Name);
        Assert.Equal("Actor", sakuta.Type);
    }

    [Fact]
    public void ParseStaffPage_LeggeLoStaffRaggruppatoPerRuolo()
    {
        var people = new AnimeClickHtmlParser()
            .ParseStaffPage(RealSiteFixtures.StaffPage, BaseUrl);

        Assert.NotEmpty(people);
        Assert.Contains(people, p =>
            p.Name.Contains("Masui", System.StringComparison.OrdinalIgnoreCase)
            && p.Role is not null
            && p.Role.Contains("Regia", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseStaffPage_NonScambiaINomiPerRuoli()
    {
        // Nel markup nuovo il titolo del ruolo e il nome della persona sono entrambi <h4>:
        // <h4>Regia</h4> per il ruolo, <h4 class="mo-heading"> per la persona.
        // Confonderli produce crediti fantasma intestati a "Regia" o "Musiche".
        var people = new AnimeClickHtmlParser()
            .ParseStaffPage(RealSiteFixtures.StaffPage, BaseUrl);

        Assert.DoesNotContain(people, p =>
            string.Equals(p.Name, "Regia", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "Musiche", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "Sceneggiatura", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseRelationsPage_LeggeLeOpereCollegate()
    {
        var relations = new AnimeClickHtmlParser()
            .ParseRelationsPage(RealSiteFixtures.RelationsPage, BaseUrl);

        Assert.NotEmpty(relations);
        Assert.All(relations, r => Assert.Contains("/anime/", r.Url, System.StringComparison.Ordinal));
    }
}
