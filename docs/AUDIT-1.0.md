# Audit tecnico — major 1.0.0.0

Data: 21 settembre 2026. Base: `a18a26a` (0.5.8.0). Ambito: provider Jellyfin, identificazione, parser, episodi/stagioni, client di rete, cache, traduzioni, riparazioni, API amministrative, configurazione, frontend, build, documentazione e distribuzione.

Questo è un audit del codice accompagnato da test riproducibili e prove runtime, non una certificazione di assenza di difetti. Le verifiche sul sito coprono campioni reali, non l’intero catalogo AnimeClick. Non sono stati modificati il server Jellyfin o la libreria in produzione.

## Compatibilità verificata, non presunta

Le [note ufficiali Jellyfin 12](https://jellyfin.org/posts/jellyfin-release-12.0/) descrivono il nuovo numero di versione, il cambio del runtime e l’incompatibilità dei plugin compilati per 10.11. La migrazione usa i pacchetti ufficiali `Jellyfin.Controller` e `Jellyfin.Model` 12.0.0, target `net10.0`, SDK 10.0.401 nella verifica locale e ABI minima 12.0.0.0.

La matrice compila ed esegue i test anche contro i pacchetti 12.1.0. In aggiunta la stessa DLL compilata contro 12.0 viene caricata in server portabili ufficiali 12.0.0 e 12.1.0, con dati nuovi, porte localhost dedicate e nessun media di produzione. Questo verifica anche il caricamento binario: compilare separatamente contro due SDK non sarebbe sufficiente.

La promessa di compatibilità riguarda queste versioni verificate. Le versioni future richiedono nuove prove, in particolare quando Jellyfin cambia ABI o runtime.

## Difetti e interventi

| Area | Problema riscontrato | Intervento e riscontro |
|---|---|---|
| Identificazione automatica | Prendere il primo risultato poteva associare titoli diversi o remake ambigui | Gate separato dalla ricerca manuale: riscontri sul titolo/slug, anno, parti e distanza dal secondo candidato; test di accettazione e rifiuto |
| Titoli numerici | Una query interamente numerica poteva essere interpretata come ID | Lookup diretto riservato agli ID espliciti o con slug; “86” resta un titolo |
| Cache ricerca | Titoli ripuliti uguali potevano condividere risultati pur indicando stagioni diverse | Chiavi versionate con input completo, anno, tipo e limite risultati |
| Formati | OVA/OAV/ONA scartati fra le serie | Inclusi fra i formati episodici; special non assimilati indiscriminatamente |
| Identificazione manuale | Flusso autonomo di cancellazione/scaricamento immagini e manipolazione metadati | Validazione pagina prima del salvataggio, controlli su GUID/tipo/blocchi, refresh attraverso `IProviderManager`, immagini opt-in |
| Concorrenza dell’identificazione | L’ID o il blocco potevano cambiare durante il recupero remoto | Ricontrollo dopo la rete e risposta di conflitto senza salvare; test con modifica e blocco concorrenti |
| Fusione metadati | Uno snapshot sullo stesso percorso poteva ripristinare i dati di un ID appena cambiato | Confronto dell’identità numerica anche negli snapshot legati al percorso; slug canonici compatibili |
| Link esterni | Gli ID degli episodi erano usati in URL `/anime/` | Composizione validata `/episodio/` per le puntate; niente link anime per persone o tipi non supportati |
| Pagine remote | HTTP 200 e un titolo generico potevano essere scambiati per una scheda | Richiesta di struttura anime riconoscibile; test su pagine di errore/pubblicità e fixture reali |
| Redirect immagini | Il controllo del primo URL non copriva il redirect automatico | Client senza redirect impliciti, validazione di ogni destinazione, tetto di redirect e budget totale |
| Credenziali delle API | Redirect automatici potevano inoltrare header o richieste sensibili | Client dedicato senza redirect per AI, TMDB, TVDB e AniList; body limitato anche nella diagnostica TVDB |
| Rete | Retry anche su errori permanenti, client non rilasciati in alcuni percorsi | Retry circoscritto a errori transitori; dispose e limiti di risposta/timeout |
| Rate limit | Un avviso di rallentamento poteva attendere sincronicamente dietro una richiesta già sospesa | Scadenza protetta da lock breve; attesa asincrona fuori dal lock e ricontrollo della scadenza |
| Cache su disco | Sostituzione di caratteri produceva collisioni, ad esempio `a/b` e `a_b` | Hash dell’input originale per chiavi modificate/lunghe e marker riservato; test di lettura distinta e pulizia per prefisso |
| Scadenza dei metadati | Serie e film riscrivevano la cache dopo ogni lettura con cast/multimedia attivi, rinnovandone indefinitamente il TTL | L’arricchimento conserva il timestamp originale; un test verifica la scadenza anche dopo la riscrittura |
| Riparazioni | Un esito “nessuna fonte” poteva sopprimere tentativi per sette giorni anche dopo una nuova configurazione | Fingerprint non reversibile delle fonti rilevanti; invalidazione dell’esito quando cambiano |
| Traduzioni | Coda e traduttore non usavano esattamente lo stesso testo normalizzato/troncato | Normalizzazione condivisa, trim profilo, troncamento Unicode sicuro e test della chiave identica |
| AI disabilitata | Recupero di fonti inglesi non utilizzabili e stato “nessuna fonte” fuorviante | Controllo della disponibilità del traduttore prima delle richieste inglesi; stato disabilitato distinto |
| Configurazione nuova | Il profilo legacy poteva assegnare un modello predefinito non scelto | Migrazione solo quando esiste un profilo legacy; nuovo profilo vuoto e test di idempotenza |
| Scelta dell’utente | Mancava un interruttore AI indipendente e una rimozione esplicita della chiave | Disattivazione senza perdita del profilo; rimozione anche della copia legacy |
| Frontend | Prima pagina tecnica, numerosi controlli simultanei, identificazione tramite GUID | Cinque sezioni orientate alle attività, dettagli progressivi, ricerca locale del titolo, aiuti e azioni essenziali |
| Caricamento UI | Errori di caricamento potevano lasciare campi utilizzabili con valori incompleti | Modulo inattivo fino al caricamento, retry, gestione errori degli asset |
| Salvataggio UI | Valori zero persi, preferenze riaperte sovrascritte, profilo perso se mancava l’elenco servizi | Zero preservati, dirty state conservato, opzione del provider salvato sempre mantenuta |
| Concorrenza UI | Un salvataggio poteva sovrascrivere modifiche di un altro amministratore | Rilettura, merge dei soli campi modificati, rifiuto dei conflitti rilevati; non è un compare-and-swap atomico del server |
| Destinazione AI | Il cambio di endpoint rischiava di riutilizzare una chiave destinata ad altro servizio | Richiesta di nuova chiave o rimozione esplicita; controllo server già presente mantenuto; validazione IPv6 allineata |
| Accessibilità | Navigazione e layout poco adatti a tastiera/mobile | Tab con ARIA e frecce/Home/End, pannelli nascosti semanticamente, target tattili, test 320/390/1280 px |
| Test | Un test dei valori predefiniti eseguiva soltanto `Assert(true)` | Istanziazione reale della configurazione e asserzioni sui valori |
| Build/release | Framework e workflow fermi a .NET 9, note release obsolete | .NET 10, matrice server, test browser, dipendenze di sviluppo bloccate, note versionate, pubblicazione draft e catalogo verificato |
| Avvisi distribuiti | NOTICE citava MIT ma non includeva il testo della licenza della DLL HtmlAgilityPack | Aggiunti testo dal tag ufficiale 1.11.71 e copyright dai metadati NuGet, nel NOTICE distribuito |

## Parti conservate e controllate

I matcher di episodi e stagioni, la gestione dei titoli segnaposto, le sinossi editoriali, gli override, la conservazione della numerazione, i blocchi, i refresh mirati, l’invalidazione delle traduzioni in coda e il circuit breaker AniList non sono stati riscritti senza necessità. Sono coperti dalle suite di regressione esistenti, estese dai casi della major.

Le attività pianificate mantengono annullamento, limiti/batch e ricontrollo dell’idoneità. Gli endpoint amministrativi mantengono `RequiresElevation`. Nessun nuovo servizio cloud è obbligatorio, nessuna chiave reale è necessaria per la suite e non è stato aggiunto alcun servizio di telemetria.

## Verifiche riproducibili

- **210 test xUnit** con warning trattati come errori contro ciascuna delle versioni 12.0.0 e 12.1.0 (suite iniziale: 165 test). I risultati della release sono consultabili nella [CI](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/actions/workflows/build.yml).
- Build Release del plugin e dell’harness; pacchetto compilato contro l’ABI minima, senza DLL del server.
- **11 test browser Chromium passati** sui file reali HTML/CSS/JS: home, tastiera, caricamento fallito/riprova, stato del modulo, errori di salvataggio, zero TTL, merge e conflitti, provider non disponibile, credenziali, identificazione tramite titolo, testo ostile, assenza di ID duplicati e overflow.
- Runtime su Jellyfin 12.0.0 e 12.1.0: plugin attivo, configurazione letta/salvata, elenco servizi, audit su libreria vuota, ID invalidi, 401 senza login e 403 per utente non amministratore; login e salvataggio nel vero web client Jellyfin.
- Durante il test runtime sono comparsi un `CancelledError` del web client ospitante durante la navigazione e messaggi server `Token is required` per `/socket` prima del login. Lo script segnala separatamente l’annullamento; qualsiasi altra eccezione browser causa fallimento. Le richieste di configurazione del plugin sono autenticate e verificate separatamente. Non si dichiara quindi un log Jellyfin interamente privo di errori: questi messaggi riguardano la navigazione/autenticazione dell’host, non il caricamento del plugin.
- Prove live AnimeClick, distanziate e limitate: ricerca “Seishun Buta Yarou”; film `25493/seishun-buta-film` (1 episodio) e serie `23809/seishun-buta-yarou-wa-bunny-girl-senpai-no-yume-o-minai` (13 episodi), con un campione di sinossi per scheda. Harness senza anomalie su questi due campioni.
- Fixture del sito reale già presenti nella suite: ricerca, pagina anime, personaggi, staff e relazioni, oltre alle fixture sintetiche per numerazioni problematiche.
- Audit NuGet transitivo e npm; il report descrive le vulnerabilità conosciute dalle fonti al momento dell’esecuzione, non tutti i difetti possibili.
- Controllo diff, versioni embedded, asset del pacchetto e successiva corrispondenza checksum fra release e catalogo.

Comandi principali in [README](../README.md). Il test runtime in `tools/AnimeClick.WebTests/runtime-smoke.cjs` inizializza un server **usa-e-getta** e non deve essere puntato alla produzione. Il codice applica una guardia localhost, rifiuta la porta 8096 e richiede una nuova installazione oppure un resume esplicito del proprio server di prova.

## Limiti e rischi residui

1. **Nessuna garanzia “zero bug”.** Non è stato eseguito un penetration test esterno, una verifica formale né un test di carico prolungato su una libreria completa. Le prove runtime usano Linux x64; Windows e macOS non sono stati eseguiti.
2. **Fonti mutevoli.** AnimeClick può cambiare HTML o limitare l’accesso; due campioni live non rappresentano tutti i formati. Il parser rifiuta pagine non riconoscibili, ma non può dimostrare la correttezza di ogni dato editoriale.
3. **Identificazione prudente.** Titoli tradotti senza alias condivisi, date mancanti o nomenclature particolari possono richiedere l’ID manuale. Aumentare indiscriminatamente la permissività reintrodurrebbe falsi positivi.
4. **Servizi facoltativi.** I test verificano adattatori, parsing e politiche, non tutte le combinazioni di account/modello. Non sono state usate chiavi di produzione né eseguite prove a pagamento. Nessuna garanzia sulla qualità di ogni traduzione o sulla disponibilità dei fornitori.
5. **Confini di sicurezza.** L’amministratore può scegliere un BaseUrl o un endpoint locale: questi sono input privilegiati. Il controllo dei domini non è un firewall e non equivale a una protezione universale dal DNS rebinding. Proteggere rete, account amministrativi e configurazioni.
6. **Concorrenza.** Il merge della pagina riduce le sovrascritture, ma l’API di configurazione Jellyfin non offre qui una precondizione atomica: due scritture esattamente simultanee possono ancora competere. Evitare modifiche contemporanee dello stesso profilo.
7. **Cache e costi.** Le chiavi ambigue precedenti non vengono riusate; alcuni risultati saranno ricalcolati. Una pulizia o un cambio di profilo può provocare nuove chiamate e costi AI. La cache non è crittografata e non sostituisce un backup.
8. **Accessibilità.** Sono stati verificati tastiera, etichette e layout in Chromium; non è una certificazione WCAG né una prova completa con screen reader o tutti i browser.
9. **Produzione.** Nessuna migrazione massiva, ri-identificazione o sostituzione delle immagini è stata eseguita sul NAS. La pubblicazione della major e l’installazione sul server sono operazioni distinte.

## Distribuzione

La procedura è descritta in [RELEASING](RELEASING.md): CI verde, draft, verifica ZIP, pubblicazione, download pubblico e confronto checksum, quindi aggiornamento del catalogo mantenendo le vecchie ABI. Un risultato locale non viene confuso con una release effettivamente pubblicata.
