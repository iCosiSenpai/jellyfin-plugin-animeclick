using System;
using System.IO;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// HTML catturato da www.animeclick.it il 2026-09-07, non scritto a mano.
///
/// Le fixture inline in <see cref="TestFixtures"/> descrivono il markup Bootstrap 3
/// che il sito serviva anni fa (<c>div.media</c>, <c>h4.media-heading</c>, <c>div.well</c>,
/// tabelle). Il sito e' passato a Bootstrap 5 e quelle classi non esistono piu': la suite
/// restava verde mentre in produzione ogni ricerca tornava zero candidati.
///
/// Questi file sono la controprova. Vanno riscaricati, non modificati a mano, quando
/// il sito cambia ancora: e' l'unico modo perche' un test possa accorgersene.
/// Dalle pagine sono stati tolti solo script, stili, svg, link e la nav — rumore che
/// il parser non legge. Il resto e' testuale.
/// </summary>
internal static class RealSiteFixtures
{
    /// <summary>/cerca?name=Seishun+Buta+Yarou — 11 risultati, anime + manga + novel + news.</summary>
    public static string Search => Load("search-seishun-buta-yarou.html");

    /// <summary>/anime/25493/seishun-buta-film — "Rascal Does Not Dream of a Dreaming Girl".</summary>
    public static string AnimePage => Load("anime-25493.html");

    /// <summary>/anime/25493/seishun-buta-film/personaggi — 12 personaggi, 10 doppiatori.</summary>
    public static string CharactersPage => Load("anime-25493-personaggi.html");

    /// <summary>/anime/25493/seishun-buta-film/staff — 71 crediti raggruppati per ruolo.</summary>
    public static string StaffPage => Load("anime-25493-staff.html");

    /// <summary>/anime/25493/seishun-buta-film/relazioni — opere collegate.</summary>
    public static string RelationsPage => Load("anime-25493-relazioni.html");

    private static string Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{fileName}' non copiata in output. Attesa in {path}.", path);
        }

        return File.ReadAllText(path);
    }
}
