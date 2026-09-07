using AnimeClick.Plugin.Services;
using Xunit;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// La sinossi italiana di un film o di una serie quando AnimeClick non ce l'ha.
///
/// Fino a qui la catena di ripiego (TMDb in italiano, poi traduzione) esisteva solo per gli
/// episodi: per un film, se la scheda AnimeClick aveva il campo trama vuoto, il plugin non
/// emetteva nulla e l'opera restava senza sinossi. E' il caso di "Eiga Karakai Jouzu no
/// Takagi-san": AnimeClick ha la scheda (38803) ma il div della trama contiene solo "Trama:",
/// mentre TMDb la sinossi in italiano ce l'ha.
/// </summary>
public class AnimeClickItalianOverviewTests
{
    [Fact]
    public void UrlFilm_ChiedeLItaliano()
    {
        var url = AnimeClickTmdbClient.BuildMovieDetailsUrl("CHIAVE", 938567, "it-IT");

        Assert.Contains("/movie/938567", url, System.StringComparison.Ordinal);
        Assert.Contains("language=it-IT", url, System.StringComparison.Ordinal);
        Assert.Contains("api_key=CHIAVE", url, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UrlSerie_ChiedeLItaliano()
    {
        var url = AnimeClickTmdbClient.BuildTvDetailsUrl("CHIAVE", 97009, "it-IT");

        Assert.Contains("/tv/97009", url, System.StringComparison.Ordinal);
        Assert.Contains("language=it-IT", url, System.StringComparison.Ordinal);
    }

    [Fact]
    public void LeggeLaTramaDallaRispostaTmdb()
    {
        const string json = """
        {"id":938567,"title":"Teasing Master Takagi-san: The Movie",
         "overview":"L'ultima estate di Takagi e Nishikata alle scuole medie sta per cominciare."}
        """;

        Assert.Equal(
            "L'ultima estate di Takagi e Nishikata alle scuole medie sta per cominciare.",
            AnimeClickTmdbClient.ParseOverview(json));
    }

    [Fact]
    public void TramaAssenteONonTradotta_NonRestituisceNulla()
    {
        // TMDb risponde 200 con overview vuota quando la traduzione italiana non esiste:
        // e' un caso normale, non un errore, e non deve produrre una sinossi vuota.
        Assert.Null(AnimeClickTmdbClient.ParseOverview("""{"id":1,"overview":""}"""));
        Assert.Null(AnimeClickTmdbClient.ParseOverview("""{"id":1}"""));
        Assert.Null(AnimeClickTmdbClient.ParseOverview("""{"id":1,"overview":"   "}"""));
    }

    [Fact]
    public void RispostaMalformata_NonFaEsplodereNulla()
    {
        Assert.Null(AnimeClickTmdbClient.ParseOverview("non e' json"));
        Assert.Null(AnimeClickTmdbClient.ParseOverview(""));
    }

    [Theory]
    // Serve a decidere se la sinossi che si ha in mano e' gia' italiana o va tradotta.
    // I testi sono lunghi come le sinossi vere: il rilevatore e' fatto per restare incerto
    // sulle frasi brevi, e su quelle la domanda non si pone perche' non sono sinossi.
    [InlineData(
        "L'ultima estate di Takagi e Nishikata alle scuole medie sta per cominciare, e gia' in "
        + "modo commovente. Quando i due trovano un gattino abbandonato, decidono di lavorare "
        + "insieme per riuscire a prendersi cura di Hana, la loro nuova compagna, almeno finche' "
        + "non riusciranno a trovare la madre che non c'e' piu'.",
        true)]
    [InlineData(
        "Dopo un dicembre estenuante, Sakuta si sta rapidamente avvicinando alla fine del suo "
        + "secondo anno di liceo. Dato che Mai e' una studentessa del terzo anno, non hanno "
        + "molto tempo da trascorrere insieme prima che arrivi il diploma, mentre sua sorella "
        + "si sta lentamente avventurando di nuovo all'aperto.",
        true)]
    [InlineData(
        "The last summer of middle school is about to begin for Takagi and Nishikata, and it "
        + "starts in a moving way. When they find an abandoned kitten, they decide to work "
        + "together so that they can take care of her until they have found the mother that "
        + "she has lost.",
        false)]
    [InlineData(
        "Five hundred years have passed since the humans went extinct at the hands of the "
        + "fearsome Beasts, and the surviving races now live in floating islands because they "
        + "are afraid of what would happen if they went back to the surface of the world.",
        false)]
    public void RiconosceSeUnaSinossiEGiaItaliana(string testo, bool italiana)
        => Assert.Equal(italiana, AnimeClickMetadataLanguageDetector.IsItalian(testo));
}
