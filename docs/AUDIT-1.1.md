# Audit AnimeClick 1.1.0.0 — 2026-10-02

Il lavoro riguarda l’interfaccia della libreria, le attività di recupero, la protezione dei metadati esistenti e una nuova condivisione facoltativa degli abbinamenti. Mantiene le protezioni di riconoscimento, rete, cache e traduzione documentate nell’[audit della major](AUDIT-1.0.md).

## Problemi e correzioni

| Problema rilevato | Risultato |
|---|---|
| Titoli e sinossi mescolati con molti controlli e nessuna fase leggibile | Analisi locale automatica, viste distinte, serie espandibili, caricamento ed errori espliciti |
| Il recupero dei titoli misurava l’accodamento di refresh generali, senza attendere la scrittura effettiva | Risoluzione con il matcher reale e salvataggio sequenziale del solo nome; progressione riferita agli esiti |
| Un titolo già compilato e diverso da AnimeClick poteva apparire da correggere | Titoli esistenti conservati; candidati limitati ai nomi vuoti, segnaposto o derivati dal file |
| I controlli delle attività non mostravano fase e contatori o potevano riavviare un lavoro | Stato sul server, polling con sospensione fuori pagina, avvio idempotente, interruzione |
| Le sinossi ferme potevano essere riaccodate nello stesso lavoro | ID tentati ricordati per esecuzione; attesa soltanto degli elementi effettivamente accettati nella coda |
| Mancava una raccolta verificabile di correzioni fra installazioni | Proposte GitHub automatiche dopo una correzione manuale, opt-in e soli ID pubblici; dataset revisionato separatamente |
| Cache amministrativa e invii duraturi non devono avere lo stesso ciclo di vita | Coda e ricevute in `AnimeClickCommunity`, conservate durante la pulizia della cache |
| L’harness costruiva diversamente dal plugin i link con ID numerici | Stessa normalizzazione e costruzione URL del client di produzione |

La riparazione dei titoli rilegge l’elemento dopo la ricerca e controlla nome, blocchi, identificativi, numerazione, percorso e abilitazione del provider. Una modifica manuale concorrente prevale. Un errore di salvataggio ripristina il nome in memoria. Una lista episodi incompleta non viene usata per la riparazione. Le liste risolte vengono condivise fra gli episodi della stessa esecuzione, anche con cache disattivata.

## Condivisione e riconoscimento

Lettura e invio sono indipendenti e disattivati di default. Le proposte contengono esclusivamente `kind`, `animeClickId`, `providerIds` con TMDB/TVDB/AniList numerici; film e serie sono distinti, stagioni escluse. Nessuna chiave dell’autore viene distribuita. Il token dell’amministratore è diretto solo a `api.github.com`, con redirect disabilitati. I dati proposti sono pubblici e associati al suo account GitHub: il pannello lo dichiara prima dell’attivazione.

La coda è persistente, limitata a 100 proposte, con backoff e cinque errori massimi; ricevute e ricerca dei marker riducono i duplicati anche dopo un riavvio. La disattivazione viene ricontrollata prima della pubblicazione. Dati persistiti corrotti non bloccano gli invii validi e vengono filtrati prima delle richieste.

Il dataset revisionato ha schema e dimensione limitati, rifiuta campi estranei, usa cache e timeout e non sostituisce gli ID AnimeClick esistenti. I conflitti impediscono suggerimenti automatici. Il registro parte vuoto: non sono stati inventati né approvati abbinamenti dalla libreria privata. L’esportazione è locale e non pubblica nulla; per includerla nel registro serve comunque la revisione.

## Verifiche

- **232 test backend** con `JellyfinVersion=12.0.0`, ripetuti con `12.1.0`, warning trattati come errori. Comprendono protezione dei titoli, modifiche concorrenti, blocchi e provider disabilitato, stato delle attività, payload pubblico, opt-out, persistenza, deduplicazione e coda corrotta.
- **17 test Playwright**: navigazione e tastiera, viewport stretto, preferenze e valori numerici incluso zero, salvataggi concorrenti e falliti, identificazione manuale, gestione dei segreti, analisi e recuperi della libreria, errori parziali, progressione, stop e impostazioni Comunità.
- **Jellyfin 12.0.0 e 12.1.0 reali, isolati**: DLL compilata contro l’ABI minima, caricamento attivo, impostazioni, API, rifiuto degli accessi non amministrativi e pagina nel browser; attività titoli realmente completata sulla libreria vuota di prova. Nessun media o database di produzione montato.
- **Harness** compilato senza warning; lettura aggiornata della scheda AnimeClick 25493 tramite quattro richieste al sito, nessuna anomalia di parsing. I test backend coprono i casi di numerazione e abbinamento più articolati.
- Validazione del registro della comunità, controllo sintattico JavaScript e `git diff --check`. Audit NuGet transitivo e npm senza vulnerabilità note segnalate al momento della verifica.
- Prima dell’installazione, riferimento locale in sola lettura dei 5.764 elementi film/serie/stagioni/episodi sul NAS. Il controllo di produzione verifica installazione e conservazione dei dati; non avvia recuperi massivi.

## Limiti della verifica

Le richieste GitHub sono simulate nei test: nessuna issue reale è stata creata per collaudare la condivisione. Un token deve possedere i permessi effettivi sul repository; un errore appare nello stato e non annulla la correzione locale. La deduplicazione non è una transazione con GitHub e la sua indicizzazione può ritardare.

Non sono stati invocati servizi AI a pagamento né provati account esterni personali. I test verificano configurazione e comportamento delle integrazioni con risposte controllate. Una fonte priva di titolo o trama non può essere completata dal plugin senza ulteriori dati.

L’avanzamento conta elementi verificati, non stima il tempo residuo. I dettagli delle attività sono in memoria e si azzerano al riavvio. L’interruzione delle sinossi non può ritirare i refresh già consegnati al server. Il recupero delle sinossi resta limitato a venti lotti da cento elementi con un’attesa massima di cinque minuti per lotto; un risultato parziale è dichiarato.

Una prova runtime su libreria vuota non certifica ogni abbinamento della libreria reale. Per rispettare la richiesta di conservarla, le scritture sono verificate con test mirati e l’installazione sul NAS è controllata senza avviare modifiche di massa. Nessuna garanzia di compatibilità con future major Jellyfin o di correttezza semantica universale.
