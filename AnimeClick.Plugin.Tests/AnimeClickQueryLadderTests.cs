using System;
using System.Linq;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using Xunit;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// La scala di tentativi con cui si cerca un titolo su AnimeClick.
///
/// AnimeClick non fa match parola per parola: cerca una sottostringa contigua nei titoli
/// e nello slug. "Fate Camelot" non trova nulla, "Fate Grand Order Camelot" trova la scheda
/// perche' lo slug e' fate-grand-order-camelot. Questo decide la forma dei tentativi:
/// accorciare il titolo dalla coda (prefissi via via piu' corti) e, in ultima istanza,
/// provare le singole parole distintive.
///
/// Ogni caso qui viene da un titolo che in libreria era rimasto senza sinossi italiana.
/// </summary>
public class AnimeClickQueryLadderTests
{
    // ── Ripulitura del nome ──

    [Theory]
    // Il caso Paprika: la cartella porta il nome grezzo della release, e Jellyfin
    // usa quello come titolo dell'elemento. Senza ripulitura nessuna query puo' funzionare.
    [InlineData("Paprika.2006.4K.HDR.DV.2160p.BDRip Ita Eng Jap x265-NAHOM", "Paprika")]
    [InlineData("Akira 1988 1080p BluRay x264 AAC 5.1-GROUP", "Akira")]
    [InlineData("Perfect Blue [BDRip 1080p Hi10P FLAC] [Dual-Audio]", "Perfect Blue")]
    [InlineData("Redline (2009) [WEB-DL 2160p HEVC 10bit] MULTi VOSTFR", "Redline")]
    public void CleanSearchQuery_TogliILeResiduiDelleRelease(string raw, string expected)
        => Assert.Equal(expected, AnimeClickSeriesSearchProvider.CleanSearchQuery(raw));

    [Theory]
    // Non deve mangiare parole vere che somigliano a sigle tecniche.
    [InlineData("Dr. Stone")]
    [InlineData("Eyeshield 21")]
    [InlineData("Cowboy Bebop")]
    [InlineData("Steins;Gate 0")]
    [InlineData("K-On!")]
    [InlineData("Perfect Blue")]
    public void CleanSearchQuery_NonRovinaITitoliNormali(string title)
        => Assert.Equal(title, AnimeClickSeriesSearchProvider.CleanSearchQuery(title));

    // ── Prefissi via via piu' corti ──

    [Fact]
    public void PrefixQueries_AccorciaDallaCodaFinoAllaPrimaParola()
    {
        var ladder = AnimeClickSeriesSearchProvider.GetPrefixQueries("Saekano the Movie Finale").ToList();

        // "Saekano" da solo e' l'unica forma che AnimeClick trova (slug: saekano-movie).
        Assert.Contains("Saekano", ladder);
        // e deve arrivarci accorciando, non saltando direttamente
        Assert.Equal(ladder.OrderByDescending(q => q.Length).ToList(), ladder);
    }

    [Fact]
    public void PrefixQueries_NonRestituisceIlTitoloIntero()
    {
        // Il titolo intero e' gia' stato provato prima: ripeterlo e' una richiesta sprecata.
        const string full = "Mushoku Tensei Jobless Reincarnation";
        Assert.DoesNotContain(full, AnimeClickSeriesSearchProvider.GetPrefixQueries(full));
    }

    [Fact]
    public void PrefixQueries_TitoloDiUnaSolaParolaNonProduceNulla()
        => Assert.Empty(AnimeClickSeriesSearchProvider.GetPrefixQueries("Paprika"));

    // ── Parole distintive, ultima spiaggia ──

    [Fact]
    public void DistinctiveTokens_TrovaLaParolaCheIdentificaLOpera()
    {
        // "Fate/Grand Order: Divine Realm of the Round Table - Camelot Wandering; Agateram":
        // l'unica parola che porta alla scheda giusta e' "Camelot".
        var tokens = AnimeClickSeriesSearchProvider
            .GetDistinctiveTokens("Fate/Grand Order: Divine Realm of the Round Table - Camelot Wandering; Agateram")
            .ToList();

        Assert.Contains("Camelot", tokens);
    }

    [Fact]
    public void DistinctiveTokens_ScartaLeParoleGenericheEIArticoli()
    {
        var tokens = AnimeClickSeriesSearchProvider
            .GetDistinctiveTokens("The Magical Girl and the Evil Lieutenant Used to Be Archenemies")
            .ToList();

        Assert.DoesNotContain("The", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("and", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Used", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Movie", tokens, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistinctiveTokens_NeProvaPocheNonTutte()
    {
        // Ogni parola e' una richiesta al sito: la coda lunga va tagliata.
        var tokens = AnimeClickSeriesSearchProvider
            .GetDistinctiveTokens("WorldEnd What are you doing at the end of the world Are you busy Will you save us")
            .ToList();

        Assert.True(tokens.Count <= 3, $"troppe parole distintive: {tokens.Count}");
    }

    // ── Soglia di punteggio ──

    [Fact]
    public void SogliaPunteggio_RifiutaUnCandidatoCheNonCentraNulla()
    {
        // "Camelot" da sola restituisce anche "La spada magica - Alla ricerca di Camelot" (1998).
        // Per un film del 2020 quel candidato non deve essere accettato.
        var estraneo = new AnimeClickSearchResult
        {
            Id = "7871/la-spada-magica",
            Title = "La spada magica - Alla ricerca di Camelot",
            ProductionYear = 1998,
            Format = "categoria: Film"
        };

        var punteggio = AnimeClickSearchScorer.Score(estraneo, "Camelot", productionYear: 2020, seriesRequest: false);
        Assert.False(
            AnimeClickSearchScorer.IsAcceptable(
                punteggio, AnimeClickSearchScorer.SearchStage.DistinctiveToken,
                yearMatchesExactly: false, queryLength: "Camelot".Length),
            $"un candidato di 22 anni prima non deve passare la soglia (punteggio {punteggio})");
    }

    [Fact]
    public void SogliaPunteggio_AccettaIlCandidatoGiusto()
    {
        var giusto = new AnimeClickSearchResult
        {
            Id = "25065/fate-grand-order-camelot",
            Title = "Fate/Grand Order: Shinsei Entaku Ryouiki Camelot",
            ProductionYear = 2020,
            Format = "categoria: Film"
        };

        var punteggio = AnimeClickSearchScorer.Score(giusto, "Camelot", productionYear: 2020, seriesRequest: false);
        Assert.True(
            AnimeClickSearchScorer.IsAcceptable(
                punteggio, AnimeClickSearchScorer.SearchStage.DistinctiveToken,
                yearMatchesExactly: true, queryLength: "Camelot".Length),
            $"il candidato con anno e formato esatti deve passare (punteggio {punteggio})");
    }

    [Fact]
    public void SogliaPunteggio_LaQueryPrincipaleNonHaSoglia()
    {
        // Sul nome cosi' com'e' in libreria ci si fida: e' il comportamento storico,
        // e un titolo senza anno non deve smettere di funzionare.
        Assert.True(AnimeClickSearchScorer.IsAcceptable(0, AnimeClickSearchScorer.SearchStage.Primary));
    }

    [Fact]
    public void ParolaSingola_SenzaAnnoNonSiAccettaNulla()
    {
        // Il caso vero: "Saekano the Movie Finale" non ha anno in libreria, e cercando
        // la parola "Finale" AnimeClick restituisce "Gintama - Capitolo Finale: Yorozuya
        // per sempre" — un film, titolo che contiene la parola, punteggio alto.
        // Senza un anno da confrontare non c'e' modo di distinguerlo: si rifiuta.
        var gintama = new AnimeClickSearchResult
        {
            Id = "4499/gintama-the-movie-final",
            Title = "Gintama - Capitolo Finale: Yorozuya per sempre",
            ProductionYear = 2021,
            Format = "categoria: Film"
        };

        var punteggio = AnimeClickSearchScorer.Score(gintama, "Finale", productionYear: null, seriesRequest: false);
        Assert.False(
            AnimeClickSearchScorer.IsAcceptable(
                punteggio, AnimeClickSearchScorer.SearchStage.DistinctiveToken,
                yearMatchesExactly: false, queryLength: "Finale".Length),
            $"senza anno una parola sola non basta (punteggio {punteggio})");
    }

    [Fact]
    public void Prefisso_UnSoloCandidatoCompatibileVaBene()
    {
        // "Saekano" trova saekano-movie, che si chiama "Saenai Heroine no Sodatekata Fine":
        // col titolo in libreria non condivide una parola, quindi il punteggio resta basso.
        // Ma e' l'unico film che quel prefisso restituisce, e il prefisso e' il titolo stesso.
        var saekano = new AnimeClickSearchResult
        {
            Id = "22906/saekano-movie",
            Title = "Saenai Heroine no Sodatekata Fine",
            ProductionYear = 2019,
            Format = "categoria: Film"
        };

        var punteggio = AnimeClickSearchScorer.Score(saekano, "Saekano", productionYear: null, seriesRequest: false);
        Assert.True(
            AnimeClickSearchScorer.IsAcceptable(
                punteggio, AnimeClickSearchScorer.SearchStage.Prefix,
                isOnlyCompatibleCandidate: true, queryLength: "Saekano".Length),
            $"l'unico candidato compatibile di un prefisso lungo va accettato (punteggio {punteggio})");
    }

    [Fact]
    public void Prefisso_UnSoloCandidatoMaPrefissoTroppoCortoNo()
    {
        var qualunque = new AnimeClickSearchResult { Id = "1/x", Title = "Qualcosa", Format = "categoria: Film" };
        var punteggio = AnimeClickSearchScorer.Score(qualunque, "Fate", productionYear: null, seriesRequest: false);

        Assert.False(
            AnimeClickSearchScorer.IsAcceptable(
                punteggio, AnimeClickSearchScorer.SearchStage.Prefix,
                isOnlyCompatibleCandidate: true, queryLength: "Fate".Length));
    }

    // ── Stagioni ──

    [Fact]
    public void Stagioni_LAnnoDistingueLaStagioneGiustaDallaPrima()
    {
        // "Mushoku Tensei: Jobless Reincarnation Season 3" perde "Season 3" nella ripulitura,
        // quindi la query coincide parola per parola col titolo della prima stagione.
        // A decidere resta solo l'anno: se non pesa abbastanza, la terza stagione eredita
        // la sinossi della prima.
        const string query = "Mushoku Tensei: Jobless Reincarnation";
        var prima = new AnimeClickSearchResult
        {
            Id = "27002/mushoku-tensei-isekai-ittara-honki-dasu",
            Title = "Mushoku Tensei: Jobless Reincarnation",
            ProductionYear = 2021,
            Format = "categoria: Serie TV"
        };
        var terza = new AnimeClickSearchResult
        {
            Id = "56733/mushoku-tensei-isekai-ittara-honki-dasu-3",
            Title = "Mushoku Tensei: Jobless Reincarnation III",
            ProductionYear = 2026,
            Format = "categoria: Serie TV"
        };

        var puntiPrima = AnimeClickSearchScorer.Score(prima, query, productionYear: 2026, seriesRequest: true);
        var puntiTerza = AnimeClickSearchScorer.Score(terza, query, productionYear: 2026, seriesRequest: true);

        Assert.True(puntiTerza > puntiPrima,
            $"la terza stagione deve vincere: terza {puntiTerza}, prima {puntiPrima}");
    }
}
