using MediaBrowser.Model.Plugins;

namespace AnimeClick.Plugin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // ── Metadati ──
    /// <summary>Usa il titolo italiano come nome della serie.</summary>
    public bool PreferItalianTitle { get; set; } = true;

    /// <summary>Importa la sinossi/trama in italiano.</summary>
    public bool EnablePlot { get; set; } = true;

    /// <summary>Importa i generi in italiano.</summary>
    public bool EnableGenres { get; set; } = true;

    /// <summary>Importa gli studi di animazione.</summary>
    public bool EnableStudios { get; set; } = true;

    /// <summary>Importa il rating medio della community.</summary>
    public bool EnableCommunityRating { get; set; } = true;

    /// <summary>Importa cast e staff (doppiatori, registi, autori).</summary>
    public bool EnableCast { get; set; } = true;

    /// <summary>Importa tag (Shounen, Seinen, etc.).</summary>
    public bool EnableTags { get; set; } = true;

    /// <summary>Importa titoli italiani degli episodi dalla pagina /episodi.</summary>
    public bool EnableEpisodeTitles { get; set; } = true;

    /// <summary>Crea collezioni automatiche basate su sequel/prequel/spin-off.</summary>
    public bool EnableCollections { get; set; } = false;

    /// <summary>Importa nomi delle sigle (Opening/Ending) nei tag.</summary>
    public bool EnableThemeSongs { get; set; } = true;

    // ── Ricerca ──
    /// <summary>Numero massimo di risultati per ricerca.</summary>
    public int MaxSearchResults { get; set; } = 10;

    /// <summary>Filtra solo anime (esclude manga, novel, drama).</summary>
    public bool FilterToAnimeOnly { get; set; } = true;

    // ── Cache & Performance ──
    /// <summary>URL di base di AnimeClick.</summary>
    public string BaseUrl { get; set; } = "https://www.animeclick.it";

    /// <summary>Durata cache metadati in ore.</summary>
    public int CacheHours { get; set; } = 48;

    /// <summary>Durata cache negativa in ore (risultati vuoti).</summary>
    public int NegativeCacheHours { get; set; } = 12;

    /// <summary>Pausa in millisecondi tra richieste HTTP.</summary>
    public int RequestDelayMilliseconds { get; set; } = 1000;

    // ── Avanzate ──
    /// <summary>User-Agent per le richieste HTTP.</summary>
    public string UserAgent { get; set; } = "AnimeClick-Jellyfin-Plugin/0.2.0.0 (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";
}
