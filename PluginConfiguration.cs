using MediaBrowser.Model.Plugins;

namespace AnimeClick.Plugin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // ── Metadati ──
    /// <summary>Usa il titolo italiano come nome della serie.</summary>
    public bool PreferItalianTitle { get; set; } = true;

    /// <summary>
    /// Se true, AnimeClick sovrascrive anche i campi non-italiani (titolo originale,
    /// studio, rating, data, classificazione) che altri provider (AniList/TMDB/OMDb)
    /// gestiscono meglio. Se false (default), AnimeClick emette solo i campi localizzati
    /// (titolo IT, sinossi IT, generi IT, tag, cast) e lascia i buchi agli altri provider.
    /// </summary>
    public bool OverwriteNonItalianFields { get; set; } = false;

    /// <summary>
    /// Fornisci la locandina italiana di AnimeClick come immagine fallback per
    /// Series/Movie (priorità bassa: AniList/Fanart/altro vincono se hanno immagini).
    /// </summary>
    public bool EnableAnimeClickImages { get; set; } = true;

    /// <summary>
    /// Larghezza minima (px) della locandina AnimeClick. Sotto questa soglia il poster IT
    /// viene scartato e Jellyfin passa al provider immagini successivo (Fanart/TMDB/AniList).
    /// 0 = nessun filtro (comportamento storico). Default 400: evita locandine a bassa risoluzione
    /// da AnimeClick quando provider a priorità più alta (Fanart, AniList, TheMovieDb) possono fornire
    /// artwork migliore.
    /// </summary>
    public int MinPosterWidth { get; set; } = 400;

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

    // ── Sinossi episodi IT (TMDB + Ollama Cloud) ──
    /// <summary>
    /// Traduce le sinossi degli episodi in italiano. AnimeClick non pubblica sinossi
    /// per-episodio: il plugin recupera l'overview inglese da TMDB e la traduce in IT
    /// via Ollama Cloud. Disattivato di default (opt-in). Richiede TmdbApiKey e
    /// OllamaCloudApiKey. Senza traduzione, le sinossi restano in EN dai provider
    /// nativi Jellyfin (fill-gaps).
    /// </summary>
    public bool EnableEpisodeSynopsisTranslation { get; set; } = false;

    /// <summary>API key TMDB (themoviedb.org/settings/api). Lascia vuoto per disabilitare la fonte EN.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>API key Ollama Cloud (ollama.com/settings/keys). Lascia vuoto per disabilitare la traduzione.</summary>
    public string OllamaCloudApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint chat Ollama Cloud.</summary>
    public string OllamaCloudEndpoint { get; set; } = "https://ollama.com/api/chat";

    /// <summary>Modello cloud Ollama per la traduzione EN→IT.</summary>
    public string OllamaCloudModel { get; set; } = "gemma4:cloud";

    /// <summary>Timeout in secondi per una singola chiamata di traduzione Ollama.</summary>
    public int EpisodeTranslationTimeoutSec { get; set; } = 30;

    // ── TVDB (sinossi episodi IT dirette, senza traduzione) ──
    /// <summary>
    /// Abilita TheTVDB come fonte di sinossi episodi in italiano diretto. TVDB espone
    /// overview per-episodio tradotte in diverse lingue; quando la traduzione IT esiste
    /// viene usata direttamente (zero chiamate Ollama, zero compute sul NAS). Quando TVDB
    /// non ha la traduzione per un episodio, si ricade sul flusso TMDB EN + Ollama IT
    /// (se abilitato). Opt-in, richiede TvdbApiKey.
    /// </summary>
    public bool EnableTvdbSynopsis { get; set; } = false;

    /// <summary>API key TheTVDB v4 (thetvdb.com dashboard). Lascia vuoto per disabilitare la fonte TVDB.</summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>Codice lingua TVDB (3-char) per le sinossi episodi. Default "ita".</summary>
    public string TvdbLanguage { get; set; } = "ita";

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
    /// <summary>User-Agent per le richieste HTTP. Il valore di default viene sovrascritto a runtime
    /// con la versione dell'assembly per mantenere coerenza (vedi AnimeClickClient / Plugin).</summary>
    public string UserAgent { get; set; } = "AnimeClick-Jellyfin-Plugin/0.3.8.0 (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";
}
