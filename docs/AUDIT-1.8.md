# Verifiche 1.8.0.0

## Il 403 di AnimeClick

Un utente ha segnalato che il plugin «dà errore 403». La causa è stata riprodotta dal NAS il 2026-11-26: **ogni pagina HTML di AnimeClick risponde 403 con l'intestazione `cf-mitigated: challenge`** e la pagina «Just a moment...» di Cloudflare (`cType: 'managed'`), con lo User-Agent del plugin, con quello di un browser e senza User-Agent; `robots.txt` e le immagini rispondono normalmente. È una verifica anti-bot che il sito ha attivato per tutti, non un errore del plugin né della singola installazione.

Il plugin **non prova ad aggirarla** (nessuna imitazione di browser, nessun risolutore di challenge): la verifica è una scelta del sito. L'unica via per tornare a leggere le schede è che AnimeClick consenta lo User-Agent del plugin; nel frattempo il plugin deve degradare bene.

### Cosa succedeva prima

- `GetStringAsync` registrava un errore e rilanciava; ogni chiamante lo assorbiva a modo suo.
- La ricerca percorreva tutta la sua scala (query pulita, originale, semplificata, suffisso, parole iniziali, prefissi, parole distintive): **fino a una dozzina di richieste per titolo**, ognuna con l'attesa configurata fra le richieste e un avviso con lo stack trace.
- Il risultato vuoto non veniva messo in cache come negativo (corretto), ma alla pagina arrivava «nessun risultato: incolla il link», e l'anteprima e l'identificazione rispondevano «controlla il link».
- La cache **cancellava una voce scaduta al momento della lettura**: proprio quando il sito non risponde, la copia della scheda veniva persa, e una scansione scriveva i dati delle altre fonti al posto di titoli e trame italiani.
- Le riparazioni delle trame registravano «nessuna fonte», che esclude l'elemento per sette giorni anche dopo il ritorno del sito; le attività finivano «Completate» con zero errori.

### Cosa fa ora

- `AnimeClickAvailability` riconosce la verifica (intestazione `cf-mitigated`, oppure 403/429/503 servito da Cloudflare con i marcatori della pagina di challenge) e **sospende tutte le richieste ad AnimeClick** con un solo avviso nel log. Un solo tentativo di prova dopo 15 minuti, poi 30, un'ora… fino a sei ore (`AnimeClickCircuitBreaker`, ora con `TryEnter(out isProbe)` perché solo chi tiene la prova la restituisca, e `ProbeNow` per la verifica manuale). Le richieste già in coda quando arriva la verifica non la ripetono.
- `AnimeClickUnavailableException` (derivata da `HttpRequestException`) distingue il blocco: i chiamanti scrivono un messaggio di debug invece di un avviso per elemento.
- `AnimeClickAnimeCards` sostituisce dieci copie di «cache → pagina → parse → cache» e, se il sito non risponde (qualunque errore che non sia 404/410), **restituisce la copia scaduta**. Lo stesso per l'elenco episodi, le relazioni fra stagioni, la trama del singolo episodio e i risultati di ricerca. Una pagina senza titolo non entra più in cache.
- La cache non cancella più le voci scadute alla lettura; le toglie una pulizia giornaliera oltre la validità massima configurata o sei mesi, insieme ai file temporanei abbandonati (esclusi traduzioni, registro e comunità).
- Una trama non trovata mentre AnimeClick non risponde è un **errore da riprovare** (`animeclick-unavailable`), non «nessuna fonte»; un episodio con una propria pagina AnimeClick non viene tradotto dall'inglese nel frattempo.
- Le attività finiscono «parziali» spiegando il motivo; il rapporto diagnostico riporta lo stato (`AnimeClick`: bloccato, da quando, prossimo tentativo, ultimo stato HTTP).
- Nuove API: `GET Plugins/AnimeClick/SourceStatus` e `POST Plugins/AnimeClick/SourceStatus/Check` (amministratore). Le azioni che incontrano il blocco rispondono 503 con il motivo (`AnimeClickUnavailableResultAttribute`), invece di un 500.
- Pagina: la pillola di AnimeClick in **Inizio** e il primo passo della catena in **Fonti** diventano gialli; una scheda **AnimeClick** in Fonti mostra lo stato, il motivo e **Verifica AnimeClick** (compresa in «Verifica tutte»); anteprima, ricerca e identificazione mostrano il messaggio del server. Il riquadro delle novità (edizione 2) lo racconta una volta.

## Audit

Il codice è stato riletto per aree da quattro revisori indipendenti in sola lettura (client e cache, motore di ricerca e abbinamento, provider/API/attività, frontend e igiene del repository). Ogni segnalazione è stata verificata nel codice prima di correggerla. Per l'ottimizzazione e la rimozione del codice morto sono state seguite le skill `analyzing-dotnet-performance` e `csharp-refactoring` del repository ufficiale `dotnet/skills` (MIT): rimozione per riferimenti reali, compilazione con `-warnaserror` e test dopo ogni gruppo.

### Motore di ricerca e abbinamento

- **Sequel**: `AnimeClickAutomaticIdentification` confronta i titoli senza il marcatore della parte (`StripPart`), dato che il numero della parte è già verificato a parte. «Kingdom Season 2» / «Kingdom 2» valeva 40 su 80 ed era scartato. `CleanSearchQuery` toglie anche «3rd Season», «Third Season», «Parte N», «IV»; `GetSuffixQuery` non usa più i marcatori di stagione come suffisso distintivo («3rd Season» trovava terze stagioni di altre opere).
- **Vocali lunghe**: `AnimeClickSearchScorer.NormalizeTitle`, condiviso da punteggio e identificazione automatica, piega ou/oo → o e uu → u dopo aver tolto gli accenti: «Shōjo», «Shoujo» e «Shojo» coincidono.
- **«22/7»**: senza `explicitId`, un nome con la barra è un ID solo se dopo la barra c'è uno slug con lettere.
- **Data di uscita**: AnimeClick pubblica solo l'anno (`datePublished` = `AAAA-01-01` su tutte le schede); `ParsePremiereDate` la scarta e legge in UTC le date vere. Prima la data fittizia prendeva il posto di quelle di TMDB/AniList e, letta in ora locale, diventava il 31 dicembre dell'anno prima.
- **Cache della ricerca**: un risultato trovato da una variante dopo un errore di un tentativo precedente non va in cache; una pagina che non porta il modulo di ricerca è un errore, non «nessun risultato» in cache.
- **Richieste risparmiate**: relazioni in cache (`relations:v1::`) e riuso di quelle già lette dalla scheda; elenco episodi vuoto trattato come risposta per `NegativeCacheHours` e nella sessione; nessuna lettura della scheda nella catena delle trame quando gli ID esterni sono già noti; elenco letto senza i conteggi della scheda non messo in cache (mancherebbe il limite sulle righe); identificazione diretta che riusa la scheda valida in cache invece di sovrascriverla perdendo cast e relazioni.
- **Libreria**: la struttura delle stagioni di una serie è memorizzata per 30 secondi (`AnimeClickEpisodeLayoutResolver`): prima ogni episodio rileggeva tutti gli episodi della serie, N² letture.

### Bug corretti

- **Sblocco a cascata** (decisione del proprietario, 2026-11-26): `IdentifyAndRefresh` e `RefreshItem` con `Unlock`, e `UnlockItem` chiamato dalla pagina, toglievano ogni lucchetto dell'elemento e di tutte le sue stagioni ed episodi; il refresh completo accodato subito dopo sovrascriveva così i titoli di episodio bloccati a mano. Ora si sblocca solo l'elemento scelto (`UnlockItemRequest.IncludeChildren` vale `false` se non richiesto esplicitamente); gli episodi bloccati non vengono toccati, nemmeno nell'ID della scheda precedente.
- `AnimeClickTitleRepairService`: il lucchetto del nome veniva tolto prima di sapere se esisteva un titolo; ora nello stesso salvataggio che lo scrive, e ripristinato se il salvataggio fallisce.
- `AnimeClickLocks.Release`: sbloccare un campo di un elemento bloccato per intero lasciava l'elemento senza alcun blocco; ora diventa «tutti i campi bloccati tranne questo».
- Cast parziale: doppiatori e staff si aggiungono solo quando entrambe le pagine sono arrivate.
- `AnalisiSerie`/`RepairSeriesTitles`: un blocco durante l'analisi faceva perdere l'esito della riparazione appena scritta; ora l'analisi usa l'elenco scaduto o riporta la stagione come non in cache.
- TheTVDB: token invalidato sul 401 anche per testi di episodi, testi dell'opera e generi; chiave rifiutata in pausa 15 minuti invece di essere riprovata e registrata per ogni elemento (la verifica manuale toglie la pausa).
- AniList: User-Agent e `Accept` anche nella ricerca dell'identità; prova del breaker restituita solo da chi la tiene.
- Traduzione AI: troncamento riconosciuto anche con `finish_reason: length` (OpenAI e compatibili) e `done_reason: length` (Ollama).
- Date di TMDB e TheTVDB lette in UTC (`AdjustToUniversal`): l'anno non cambia più col fuso del server.
- Comunità: la pausa di un'ora dopo un download fallito non scatta più per un annullamento; la copia precedente torna utilizzabile durante un'interruzione (la cancellazione alla lettura la perdeva); il dataset validato resta in memoria finché il file non cambia.
- Registro riparazioni: gli esiti degli ultimi secondi vengono scritti allo spegnimento.
- `ImageDimensions`: non chiude più lo stream del chiamante; un JPEG su stream non riposizionabile non viene letto a un offset sbagliato.
- `AnimeClickMetadataRefreshScheduler`: le prenotazioni scadute vengono tolte invece di accumularsi per tutta la vita del processo.
- Frontend: la lista «Da fare» non si ridisegna più a ogni aggiornamento delle attività (perdeva il focus e riabilitava un pulsante occupato); pannello laterale e finestre si chiudono lasciando la pagina; «Interrompi» non torna attivo mentre l'attività si sta fermando; l'ultimo passo del setup non salva due volte; i salvataggi parziali vanno in fila; testo dell'ordine delle immagini corretto (AniList mancava); errori accanto al campo anche nelle ricerche, nell'anteprima della traduzione, nella correzione e nel setup.

### Codice e file morti rimossi

- API: `GET AvailableRemoteImages` (con `RemoteImagesResponse`, `AnimeClickRemoteImageInfo`, `ParseImageType`), `GET TestEpisodes` (con `EpisodesDiagnosticResponse`, `EpisodeDiagnosticItem`), l'alias `POST TestOllama`. `TestEpisodeTitle` resta: è un'API manuale documentata.
- Configurazione: `FilterToAnimeOnly`, mai letta (le configurazioni salvate che la contengono restano valide).
- Provider: il ramo «solo TMDB» delle trame di serie e film, irraggiungibile perché il ripiego sui testi è sempre registrato, con `AnimeClickTmdbClient.GetItalianOverviewAsync` (che saltava il throttle di TMDB) e `ParseOverview`; `UrlFormatString` nelle tre `IExternalId` (non fa parte dell'interfaccia di Jellyfin 12).
- Servizi: overload inutilizzati di `ResolveEpisodeOverviewAsync`, `LoadAsync`, `GetEpisodeOverviewAsync` (en-US), `GetEpisodeItalianOverviewAsync`, `ParseEpisodeOverviewPage`, `TryQueueByPath`, `GetKnownRuntimeMinutes`; il parametro mai letto `cacheKey` di `ResolveTmdbTvIdAsync` e `currentTitle` di `ClassifyEpisode` (i cui rami erano diventati costanti); i campi `AnimeClickEpisode.AirDate` e `AnimeClickAnime.RatingCount`; blocchi `if` vuoti nel parser; una condizione sempre vera nel matcher.
- Frontend: `AC.bus.off`, `AC.api.url`, `AC.settings.describeMapping`, `AC.settings.save`, `AC.setup.version`, `AC.setup.steps`, alias inutilizzati; regole CSS e variabili mai prodotte; `data-require` di componenti emby non usati.
- File: `community/relay/wrangler.toml` (il servizio si pubblica con `deploy.py`, senza wrangler, e descriveva un segreto GitHub che il relay non ha più), `docs/superpowers/` (due specifiche di progetto superate, non referenziate). `.gitignore` copre anche `TestResults/`, `*.trx`, `playwright-report/`, `.idea/`, `.env`, `.claude/settings.local.json`.

### Interfaccia: verifica di coerenza

Lo stato di AnimeClick nella pagina è stato preparato con `ui-consistency:adjusting`, scritto con `implementing` e controllato con `verifying` da un agente separato, tarato prima su una copia con una differenza piantata (un colore scritto a mano al posto della classe di tono: trovata). Il primo controllo ha trovato otto differenze, tutte corrette (fra cui l'aggiornamento dello stato finito nella ricerca della libreria invece che in quella di AnimeClick, e il 503 riconosciuto dal solo codice invece che da `blocked`). Il secondo controllo ne lascia aperta una, riportata qui con le parole del controllore: *«The blocked badge in Fonti and the pill in Inizio name the same state two ways»* — il badge dice «Verifica Cloudflare», la pillola «Bloccata da Cloudflare». Non è stata corretta una terza volta, come prevede il processo. Non valutato: ciò che solo il rendering mostra (larghezze, a capo, griglia delle sei schede) e il contrasto contro una soglia.

### Lasciato com'è, di proposito

- `TestEpisodeTitle` e l'overload a due argomenti di `ClassifyEpisode` (usato dai test).
- I campi `Generation` della coda delle traduzioni, scritti e mai letti: codice concorrente delicato, guadagno nullo.
- L'unificazione dei provider di serie e film in una base comune (circa 300 righe duplicate) e di un unico aiutante per le richieste JSON di TMDB/TheTVDB/Fanart/AniList: utili, ma un rifacimento strutturale da fare a parte, con i suoi test.
- Il cancello globale delle scritture in cache (`MutationGate`): serializza tutte le scritture, ma sono piccole; un lock lettori/scrittori va valutato con misure.

## Verifiche eseguite

- **491 test backend** (`JellyfinVersion=12.0.0`, `-warnaserror`; 12.0 e 12.1 nella CI): 0 errori, 0 avvisi. Nuovi: riconoscimento della verifica, sospensione e prova, prova fallita che raddoppia l'attesa, verifica manuale, errore di rete durante la prova, scheda scaduta usata durante il blocco, pagina senza titolo non in cache, scala della ricerca fermata al primo blocco, sequel, vocali lunghe, «22/7», data di uscita, pulizia della cache, sblocco per campo, esito «errore» invece di «nessuna fonte», troncamento AI per tutti i formati, sblocco dei titoli solo con un titolo, identificazione con sblocco che lascia bloccati gli episodi.
- **52 test browser Playwright** (`tools/AnimeClick.WebTests`): 0 errori. Nuovi test per lo stato di AnimeClick in Inizio e Fonti con verifica manuale, anteprima e ricerca durante il blocco, lista «Da fare» stabile durante gli aggiornamenti, «Interrompi» durante l'arresto, ultimo passo del setup, chiusura di pannello e finestre.
- **Test Python** (`tools/tests`, 32) e **servizio della comunità** (`community/relay`, 10): 0 errori.
- **Harness** (`tools/AnimeClick.Harness`): compila.
- **Coerenza dell'interfaccia**: modifiche visibili preparate e controllate con le skill ui-consistency (adjusting, implementing, verifying), con un controllore separato tarato su una differenza piantata.
- Nessuna prova runtime su un Jellyfin isolato in questa sessione: AnimeClick risponde comunque con la verifica, e il comportamento è coperto dai test con risposte registrate dal sito reale (intestazioni e corpo della challenge).
