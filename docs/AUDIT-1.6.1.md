# Verifiche 1.6.1.0

## Audit completo: motore di ricerca e abbinamento, backend e frontend

### Motore di ricerca, stagioni ed episodi

- **Sequel per anno di trasmissione (`AnimeClickSeasonResolver.SelectUniqueByAirYear`)**: quando una scheda ha un solo sequel televisivo dichiarato ma il suo anno dista più di `±1` anno dall'anno di messa in onda della stagione locale, il candidato viene ora scartato invece di essere scelto d'ufficio. Evita che una stagione intermedia mancante nelle relazioni di AnimeClick venga collegata a una stagione successiva o precedente lontana nel tempo.
- **Identità stabile (`AnimeClickClient.GetStableIdentity`)**: centralizzata in un unico metodo condiviso tra client, provider e resolver stagioni.
- **Provider stagioni (`AnimeClickSeasonProvider`)**:
  - Conserva `ProviderIds["AnimeClick"]` già presente sulla stagione durante il refresh anche quando la serie padre non espone un ID valido.
  - Copia la sinossi della scheda sulla stagione solo quando la stagione punta a una scheda AnimeClick propria (`isDistinctCard`), evitando di duplicare la trama generale della serie su ogni stagione.
- **Provider episodi e layout (`AnimeClickEpisodeProvider`, `AnimeClickEpisodeLayoutResolver`)**:
  - `ResolveWithRuntime` raccoglie layout e insieme degli episodi con durata nota in un'unica scansione `FindByPath` (con fallback a `_libraryManager` quando `BaseItem.LibraryManager` non è valorizzato), dimezzando gli accessi alla libreria per serie.
  - Durante un refresh forzato (`refreshCache: true`), la cache del catalogo episodi viene sovrascritta solo dopo il successo della chiamata di rete, preservando il fallback in cache se AnimeClick è temporaneamente irraggiungibile.
- **Correzione manuale degli abbinamenti (`AnimeClickIdentifyController`, `AnimeClickRepairLedger`)**:
  - La scheda `AnimeClickAnime` appena validata viene salvata subito in cache prima di avviare il refresh in background, evitando un doppio scaricamento immediato.
  - Quando una serie o una stagione viene ricollegata a un ID AnimeClick diverso dal precedente, gli ID AnimeClick obsoleti sui figli (`Season` / `Episode`) non bloccati vengono ripuliti e le eventuali soppressioni in `AnimeClickRepairLedger` vengono rimosse (`Forget`).
- **Pianificatore refresh post-traduzione (`AnimeClickMetadataRefreshScheduler`)**: `FindItemByPath` cerca ora sia file (`isFolder: false`) sia cartelle (`isFolder: true`), permettendo alle traduzioni AI in background delle trame di `Series` di completare il refresh `ValidationOnly`.
- **Allineamento analisi libreria (`AnimeClickDiagnosticsController`, `AnimeClickAniListResolver`, `AnimeClickAniListMetadata`)**:
  - `LibraryAuditSeries` applica ora la stessa catena di `LibraryAudit`: anni delle stagioni ricavati dai sequel AniList (con timeout dedicato di 4 s), distinzione tra scheda non abbinata (`Unbound`) e scheda non raggiungibile (`CardNotResolved`), verifica di `IsFieldLocked(MetadataFields.Name)` per il titolo serie e anteprima del fallback episodi su tutti i 5 passaggi (incluso `Episode >= 0` per gli speciali).
- **Task titoli mancanti (`AnimeClickRefreshMissingTitlesTask`)**: memoizzazione di `GetLibraryOptions` per `SeriesId` durante la scansione degli episodi.
- **Punteggio di ricerca (`AnimeClickSearchScorer`)**: guardia esplicita contro query o titoli che si riducono a stringa vuota dopo la normalizzazione.

### Configurazione, sicurezza, frontend e strumenti

- **Protezione lato server della chiave AI (`Plugin.UpdateConfiguration`)**: se una configurazione salvata cambia `AiEndpoint` verso un host diverso mantenendo la stessa `AiApiKey`, oppure passa a un endpoint HTTP locale, la chiave viene azzerata anche lato server (in aggiunta alla guardia già presente nel frontend).
- **Cache sottopagine vuote (`AnimeClickAnime`, `AnimeClickSeriesProvider`, `AnimeClickMovieProvider`)**: aggiunti i flag `PeopleLoaded` e `RelationsLoaded` (con retrocompatibilità JSON per le voci già in cache che hanno liste non vuote), così le schede prive di personaggi o relazioni non generano richieste HTTP ripetute ad ogni refresh.
- **Interfaccia (`Web/assets/animeclick-*.js`, `Web/assets/animeclick.css`)**:
  - Focus trap con `Tab` / `Shift+Tab` nel pannello laterale (`#acSeriesDrawer`) e gestione corretta di `inert` quando una finestra modale (`#acModal`) si apre sopra il pannello laterale.
  - Invalidazione immediata dello stato di verifica in **Fonti** quando l'utente modifica un campo o un interruttore della fonte.
  - Ricalcolo coerente dei contatori aggregati in **Libreria** (`replaceSeries`), pulizia dei candidati precedenti in **Correggi un abbinamento**, ripristino di `heroPaused` al cambio scheda e pulizia dei listener del bus eventi al ricaricamento della pagina.
- **Servizio della comunità e script (`community/relay/src/worker.js`, `tools/community_review.py`, `tools/release_meta.py`)**:
  - Verifica esplicita del repository configurato in `/v1/published` e della forma array in `/v1/pending`.
  - Gestione difensiva di JSON malformato nei commenti o nelle risposte in `tools/community_review.py` e lettura singola del manifesto in `tools/release_meta.py`.

## Verifiche eseguite

- **433 test backend** (`JellyfinVersion=12.0.0` e `JellyfinVersion=12.1.0` con `-warnaserror`): 0 errori, 0 avvisi.
- **32 test Python** (`tools/tests`): 0 errori.
- **10 test servizio della comunità** (`community/relay`): 0 errori.
- **29 test browser Playwright** (`tools/AnimeClick.WebTests`) + `npm audit --audit-level=high`: 0 errori, 0 vulnerabilità.
