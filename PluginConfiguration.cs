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

    // ── Sinossi episodi IT (AnimeClick + TVDB/TMDB + traduzione AI) ──
    /// <summary>
    /// Abilita la catena per le sinossi episodi: AnimeClick → TheTVDB ita →
    /// TMDB it-IT → TMDB en-US → TheTVDB eng → traduzione AI EN→IT.
    /// AnimeClick non richiede API key; l'AI traduce soltanto una sinossi inglese
    /// ottenuta da TMDB o TheTVDB. In caso di errore il campo resta invariato.
    /// </summary>
    public bool EnableEpisodeSynopsisTranslation { get; set; } = false;

    /// <summary>API key TMDB (themoviedb.org/settings/api). Lascia vuoto per disabilitare TMDB.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    // ── Traduzione AI ──
    /// <summary>
    /// Identificativo del servizio AI scelto, fra quelli di <see cref="Services.AnimeClickAiProviders"/>.
    /// Vuoto significa «non ancora scelto» e viene risolto dalla migrazione.
    /// </summary>
    public string AiProvider { get; set; } = string.Empty;

    /// <summary>Endpoint di chat del servizio AI. Precompilato scegliendo un provider.</summary>
    public string AiEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Modello da usare. Deliberatamente senza valore predefinito: i fornitori ritirano e
    /// rinominano i modelli, quindi l'elenco si chiede al servizio invece di indovinarlo.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>
    /// Chiave API del servizio AI. Vuota per un servizio in casa, che non autentica nulla:
    /// la chiave non viene mai inviata in chiaro.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Chiave storica del profilo Ollama. Conservata perché una configurazione salvata da una versione
    /// precedente lo contiene: viene travasato in <see cref="AiEndpoint"/> una volta sola.
    /// </summary>
    public string OllamaCloudApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint storico, vedi <see cref="OllamaCloudApiKey"/>.</summary>
    public string OllamaCloudEndpoint { get; set; } = "https://ollama.com/api/chat";

    /// <summary>Modello storico, vedi <see cref="OllamaCloudApiKey"/>.</summary>
    public string OllamaCloudModel { get; set; } = "gpt-oss:20b-cloud";

    /// <summary>
    /// Durata cache traduzioni in ore. Default 10 anni: una traduzione viene invalidata
    /// comunque se cambiano testo, fonte, lingua, modello, endpoint o prompt.
    /// </summary>
    public int TranslationCacheHours { get; set; } = 87600;

    /// <summary>
    /// Timeout in secondi per una singola chiamata di traduzione. È un tetto, non un'attesa:
    /// una risposta rapida non ci arriva nemmeno vicino.
    /// </summary>
    public int EpisodeTranslationTimeoutSec { get; set; } = 90;

    // ── TVDB (sinossi episodi IT dirette, senza traduzione) ──
    /// <summary>
    /// Abilita TheTVDB come fonte di sinossi episodi in italiano diretto. TVDB espone
    /// overview per-episodio tradotte in diverse lingue; quando la traduzione IT esiste
    /// viene usata direttamente (nessuna chiamata AI, nessun costo). Quando TVDB
    /// non ha la traduzione per un episodio, si ricade sul flusso TMDB EN + traduzione AI
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
    public string UserAgent { get; set; } = "AnimeClick-Jellyfin-Plugin/0.5.2.0 (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)";

    /// <summary>
    /// Schema of the persisted settings. One-time upgrades are gated on this rather than on whether
    /// a field looks empty: a value the interface can legitimately blank out is not a safe marker
    /// for "never migrated", and using one as such let a re-run overwrite the user's own settings.
    /// </summary>
    public int ConfigurationVersion { get; set; }

    /// <summary>
    /// Applies narrow one-time upgrades to persisted settings, then records the schema so they never
    /// run again. Credentials and anything the user typed are never overwritten.
    /// </summary>
    internal bool ApplyMigrations()
    {
        const int currentSchema = 1;
        if (ConfigurationVersion >= currentSchema)
        {
            return false;
        }

        // Move installs still on a previously shipped default to the current default.
        // Custom model names (anything the user typed) are deliberately left untouched.
        if (string.Equals(OllamaCloudModel, "gemma4:cloud", System.StringComparison.Ordinal)
            || string.Equals(OllamaCloudModel, "gemma4:31b-cloud", System.StringComparison.Ordinal))
        {
            OllamaCloudModel = "gpt-oss:20b-cloud";
        }

        // The shipped default used to be 30 seconds, which measurement showed was cutting healthy
        // requests: on a real library the slowest successful translation came back 120 ms under the
        // deadline, and every failure sat exactly on it. A longer budget costs nothing when the model
        // is quick, because it is a ceiling and not a delay. Gated on the schema, so a user who
        // deliberately chooses 30 keeps it instead of having it rewritten at every restart.
        if (EpisodeTranslationTimeoutSec == 30)
        {
            EpisodeTranslationTimeoutSec = 90;
        }

        // Translation used to be Ollama and nothing else, so the three settings that described it
        // were named after it. They are now one provider among many and the generic fields are what
        // the plugin reads — but a configuration saved by an earlier version only has the old ones,
        // and starting from blank would switch translation off on upgrade.
        //
        // Each field is carried across only when it is still empty. Overwriting unconditionally, as
        // the first version did, meant that a configuration whose provider had been blanked out —
        // which the page does when it fails to load the provider list — had its endpoint, model and
        // above all its API key replaced by the legacy values, empty on a recent install. That threw
        // the key away and switched translation off, silently.
        if (string.IsNullOrWhiteSpace(AiEndpoint))
        {
            AiEndpoint = OllamaCloudEndpoint?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(AiModel))
        {
            AiModel = OllamaCloudModel?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(AiApiKey))
        {
            AiApiKey = OllamaCloudApiKey ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(AiProvider))
        {
            AiProvider = AiEndpoint.Contains("ollama.com", System.StringComparison.OrdinalIgnoreCase)
                ? "ollama-cloud"
                : AiEndpoint.Contains("/api/chat", System.StringComparison.OrdinalIgnoreCase)
                    ? "ollama-local"
                    : Services.AnimeClickAiProviders.CustomId;
        }

        ConfigurationVersion = currentSchema;
        return true;
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
