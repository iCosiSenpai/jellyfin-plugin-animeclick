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

    /// <summary>Importa tag (target, tag generici e opera d'origine).</summary>
    public bool EnableTags { get; set; } = true;

    /// <summary>Importa la nazionalità come località di produzione.</summary>
    public bool EnableProductionLocations { get; set; } = true;

    /// <summary>Importa trailer, teaser e PV YouTube esplicitamente etichettati.</summary>
    public bool EnableTrailers { get; set; } = true;

    /// <summary>Importa titoli italiani degli episodi dalla pagina /episodi.</summary>
    public bool EnableEpisodeTitles { get; set; } = true;

    /// <summary>
    /// Override avanzati del layout, uno per riga: anime-id=flat,
    /// anime-id=explicit oppure anime-id=13,24 con confini cumulativi.
    /// Le righe invalide vengono ignorate e il resolver resta in modalità automatica.
    /// </summary>
    public string EpisodeLayoutOverrides { get; set; } = string.Empty;

    /// <summary>Crea collezioni automatiche basate su sequel/prequel/spin-off.</summary>
    public bool EnableCollections { get; set; } = false;

    /// <summary>Importa nomi delle sigle (Opening/Ending) nei tag.</summary>
    public bool EnableThemeSongs { get; set; } = true;

    // ── Sinossi episodi IT (AnimeClick + TVDB/TMDB + Ollama Cloud) ──
    /// <summary>
    /// Abilita la catena per le sinossi episodi: AnimeClick → TheTVDB ita →
    /// TMDB it-IT → TMDB en-US → TheTVDB eng → Ollama Cloud EN→IT.
    /// AnimeClick non richiede API key; Ollama traduce soltanto una sinossi inglese
    /// ottenuta da TMDB o TheTVDB. In caso di errore il campo resta invariato.
    /// </summary>
    public bool EnableEpisodeSynopsisTranslation { get; set; } = false;

    /// <summary>API key TMDB (themoviedb.org/settings/api). Lascia vuoto per disabilitare TMDB.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>API key Ollama Cloud (ollama.com/settings/keys). Lascia vuoto per disabilitare la traduzione.</summary>
    public string OllamaCloudApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint chat Ollama Cloud.</summary>
    public string OllamaCloudEndpoint { get; set; } = "https://ollama.com/api/chat";

    /// <summary>Modello cloud Ollama predefinito per traduzioni brevi EN→IT.</summary>
    public string OllamaCloudModel { get; set; } = "gpt-oss:20b-cloud";

    /// <summary>
    /// Durata cache traduzioni in ore. Default 10 anni: una traduzione viene invalidata
    /// comunque se cambiano testo, fonte, lingua, modello, endpoint o prompt.
    /// </summary>
    public int TranslationCacheHours { get; set; } = 87600;

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
    public string UserAgent { get; set; } = "AnimeClick-Jellyfin-Plugin/0.4.5.0 (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";

    /// <summary>
    /// Applies narrow, idempotent upgrades to persisted settings. User-provided
    /// credentials and custom model names are deliberately left untouched.
    /// </summary>
    internal bool ApplyMigrations()
    {
        // Move installs still on a previously shipped default to the current default.
        // Custom model names (anything the user typed) are deliberately left untouched.
        if (string.Equals(OllamaCloudModel, "gemma4:cloud", System.StringComparison.Ordinal)
            || string.Equals(OllamaCloudModel, "gemma4:31b-cloud", System.StringComparison.Ordinal))
        {
            OllamaCloudModel = "gpt-oss:20b-cloud";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Forces every numeric setting into a usable range and the base URL into an absolute
    /// HTTP(S) form, returning whether anything had to be corrected. Applied both on load and
    /// on save, because the configuration endpoint accepts whatever the client sends and the
    /// only previous validation lived in the configuration page's JavaScript.
    /// Secrets, model names and boolean toggles are never touched.
    /// </summary>
    internal bool Sanitize()
    {
        var original = (
            MinPosterWidth,
            TranslationCacheHours,
            EpisodeTranslationTimeoutSec,
            MaxSearchResults,
            CacheHours,
            NegativeCacheHours,
            RequestDelayMilliseconds,
            BaseUrl);

        MinPosterWidth = ConfigurationLimits.Clamp(
            MinPosterWidth,
            ConfigurationLimits.MinPosterWidthMinimum,
            ConfigurationLimits.MinPosterWidthMaximum);
        TranslationCacheHours = ConfigurationLimits.Clamp(
            TranslationCacheHours,
            ConfigurationLimits.TranslationCacheHoursMinimum,
            ConfigurationLimits.TranslationCacheHoursMaximum);
        EpisodeTranslationTimeoutSec = ConfigurationLimits.Clamp(
            EpisodeTranslationTimeoutSec,
            ConfigurationLimits.TranslationTimeoutMinimum,
            ConfigurationLimits.TranslationTimeoutMaximum);
        MaxSearchResults = ConfigurationLimits.Clamp(
            MaxSearchResults,
            ConfigurationLimits.MaxSearchResultsMinimum,
            ConfigurationLimits.MaxSearchResultsMaximum);
        CacheHours = ConfigurationLimits.Clamp(
            CacheHours,
            ConfigurationLimits.CacheHoursMinimum,
            ConfigurationLimits.CacheHoursMaximum);
        NegativeCacheHours = ConfigurationLimits.Clamp(
            NegativeCacheHours,
            ConfigurationLimits.NegativeCacheHoursMinimum,
            ConfigurationLimits.NegativeCacheHoursMaximum);
        RequestDelayMilliseconds = ConfigurationLimits.Clamp(
            RequestDelayMilliseconds,
            ConfigurationLimits.RequestDelayMinimum,
            ConfigurationLimits.RequestDelayMaximum);
        BaseUrl = ConfigurationLimits.NormalizeBaseUrl(BaseUrl);

        return original != (
            MinPosterWidth,
            TranslationCacheHours,
            EpisodeTranslationTimeoutSec,
            MaxSearchResults,
            CacheHours,
            NegativeCacheHours,
            RequestDelayMilliseconds,
            BaseUrl);
    }
}
