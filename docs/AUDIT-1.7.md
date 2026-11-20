# Verifiche 1.7.0.0

## Audit completo di backend e frontend, anteprima scheda, manutenzione

L'audit ha riletto la pagina del plugin per intero (JavaScript, CSS, pagina di caricamento) contro le [Web Interface Guidelines](https://github.com/vercel-labs/web-interface-guidelines) e i due controller con i servizi che servono la pagina. Qui sotto ciò che è stato trovato e corretto, e ciò che è stato aggiunto.

### Difetti corretti nel backend

- **Sblocco più ampio del necessario (`AnimeClickLibraryQualityService.UnlockOverviewItemsAsync`)**: con `Unlock = true` il blocco su `Overview` veniva tolto a ogni elemento richiesto, anche quando la riparazione successiva lo scartava (trama italiana o di lingua incerta, importazione disattivata). L'elemento restava senza lucchetto e senza alcun miglioramento. Ora il blocco viene tolto solo se la trama è inglese o mancante e la funzione è attiva.
- **Sblocco di serie e stagioni per riparare i titoli degli episodi (`RepairSeriesTitles`)**: l'azione toglieva il blocco sul nome anche alla serie, alle stagioni e a tutti gli episodi, benché scriva soltanto i titoli degli episodi da riparare. Ora un episodio perde il blocco solo se ha bisogno di un titolo e nel momento in cui la riparazione lo prova: quelli non raggiunti in quella chiamata lo conservano (`AnimeClickTitleRepairService.RepairSeriesAsync`).
- **`UnlockItem` con campo non riconosciuto**: un valore di `Field` non interpretabile era trattato come «nessun campo», cioè come richiesta di togliere tutti i blocchi. Ora risponde 400.
- **Riparazione di una serie senza limite di tempo**: `RepairSeriesTitles` lavora dentro la richiesta che la pagina attende per 130 secondi; su una serie lunga con titoli da cercare in altre fonti o da tradurre la pagina andava in timeout senza esito. Ora non inizia nuovi episodi dopo 75 secondi, abbandona quello in corso dopo altri 15, garantisce almeno un episodio per chiamata e restituisce `RemainingCount` con `ContinueAfter`: la chiamata successiva riparte dall'episodio a cui si era fermata, invece di ricominciare da quelli appena trovati senza titolo.
- **Analisi della singola serie (`LibraryAuditSeries`)**: raggruppava gli episodi per solo numero di stagione, fondendo fra loro le stagioni senza numero; ora usa lo stesso raggruppamento dell'analisi completa.

### Pulizia del backend

- `LibraryAudit` e `LibraryAuditSeries` condividono `GroupBySeason` e `ClassifySeason`: le due analisi differiscono in come trovano la scheda e le sue righe (solo cache, oppure rete), non in come le giudicano, e il giudizio era scritto due volte.
- La regola «stessa destinazione» della chiave AI era ripetuta in `Plugin.UpdateConfiguration` e nel controller di diagnostica: ora è `AnimeClickAiTranslator.IsSameDestination`.
- `AnimeClickIdentifyController`: tre copie della lettura dei discendenti e due della richiesta di refresh sono diventate `GetDescendants` e `TryQueueRefresh`; rimossi dalla risposta due campi mai valorizzati (`DeletedImages`, `DownloadedImages`).
- I modelli di richiesta e risposta del controller di diagnostica sono in `Api/AnimeClickDiagnosticsModels.cs`; l'estratto di una trama è `AnimeClickMetadataText.Excerpt`, condiviso da vetrina e anteprima.
- Leggere e togliere un blocco di Jellyfin era scritto in tre punti: ora è `AnimeClickLocks`. La regola «una riparazione riscriverebbe questa trama, se non fosse bloccata» è una sola (`NeedsOverviewRepair`) e arriva alla pagina come `CanUnlock`, invece di essere ricalcolata nel browser.

### Nuove API

- `GET Plugins/AnimeClick/PreviewCard?animeClickId=&itemId=`: legge la scheda (prima dalla cache, altrimenti con una sola richiesta, poi la mette in cache) e restituisce titolo, titolo originale, anno, categoria, stato, episodi, immagine solo HTTPS e solo da origini ammesse, estratto della trama, generi e i titoli della libreria già abbinati alla stessa scheda. Non scrive nella libreria; rifiuta ciò che non è un ID o un link di AnimeClick (400) e le pagine che non sono una scheda (502, senza metterle in cache).
- `GET Plugins/AnimeClick/CacheStats`: numero di elementi, byte, più vecchio e più recente, per fonte (AnimeClick, TMDB, TheTVDB, AniList, Fanart, traduzioni, comunità, registro). Il gruppo lo decide il server accanto alle chiavi; legge solo gli attributi dei file.
- `GET Plugins/AnimeClick/DiagnosticReport`: rapporto pensato per essere pubblicato. La redazione è sicura per costruzione: una impostazione testuale è riportata com'è solo se è nell'elenco di quelle innocue (`AiProvider`, `AiModel`, `CommunitySharingMode`), quindi una chiave aggiunta in futuro risulta redatta senza toccare il codice. Un test imposta ogni stringa della configurazione a un valore sentinella e verifica che non compaia.

Il rapporto riporta delle attività solo stato e contatori: il loro messaggio, durante una riparazione, contiene il nome della serie in lettura. L'elenco «già usata da» dell'anteprima esclude la serie e le stagioni del titolo selezionato, perché il provider scrive la scheda della serie sulle stagioni senza scheda propria.

Tutte e tre richiedono un amministratore (`Policies.RequiresElevation`), come il resto delle API del plugin.

### Frontend: difetti corretti

- Il salvataggio automatico prima di «Esegui la catena» falliva in silenzio se l'errore non era di validazione.
- Le azioni sulle trame di una serie inviavano tutti gli ID in una richiesta: oltre i cento il server li scartava senza che la pagina lo dicesse. Ora partono a gruppi (`queueSynopses`).
- «Sblocca e completa trame» contava anche le trame bloccate che non potevano migliorare.
- Dopo una rilettura o una riparazione la libreria veniva ridisegnata due volte.
- Un'anteprima ancora in arrivo viene scartata se il campo della scheda cambia o si sceglie un altro titolo, e il pulsante torna disponibile.
- «Copia» del rapporto funziona anche su HTTP verso l'indirizzo di rete del server, dove gli appunti asincroni del browser non esistono.
- Con più di un titolo scelto in rapida successione, il menu delle stagioni poteva essere aggiornato dalla risposta sbagliata: ora la risposta è scartata se il menu non è più nella pagina.

### Frontend: accessibilità e interazione

- **Errori accanto al campo**: `ui.fieldError` imposta `aria-invalid`, collega il messaggio con `aria-describedby` insieme al testo di aiuto e sposta il focus; il messaggio sparisce quando il valore cambia.
- **Movimento**: la vetrina ha un comando pausa/riprendi (WCAG 2.2.2) e non ruota con `prefers-reduced-motion`; lo scorrimento programmato passa da `AC.dom.reveal`, che rispetta la stessa preferenza; la barra indeterminata si anima con `transform`.
- **Focus**: l'anello resta un'ombra, ma con un `outline` trasparente che la modalità ad alto contrasto di Windows colora; i comandi hanno `scroll-margin` per non finire sotto la barra di salvataggio.
- **Tocco**: 44 px minimi per pulsanti, schede, filtri e campi con puntatore impreciso, area di 36 px attorno ai segnalini della vetrina, `touch-action: manipulation`, `overscroll-behavior: contain` in pannello e finestre.
- **Messaggi**: chiudibili, sospesi mentre il puntatore o il focus sono sopra.
- **Moduli**: attributo `name` su ogni campo, chiavi API non proposte ai gestori di password, `inputmode` numerico, segnaposto che mostrano un esempio.
- **Semantica**: titoli dei passi come intestazioni, finestra di conferma con `h2`, stagioni del pannello di dettaglio come elenco con il motivo scritto (prima solo in un `title`), pulsanti delle righe episodio con il nome dell'episodio, giapponese marcato `lang="ja"`, marchio `translate="no"`.
- **Tema**: `color-scheme: dark` per barre di scorrimento, selettori e controlli nativi; `text-wrap: balance` sui titoli; aree sicure per messaggi e barra di salvataggio.

### Pulizia del frontend

- `buildTools` (230 righe con funzioni annidate) è diviso in `buildFixCard`, `buildProbeCard`, `buildMaintenanceCard`, `buildAdvancedCard`; `renderDetail` (250 righe) in intestazione, titoli, trame e abbinamento, con un solo aiutante per le azioni (`act`) e uno per i pulsanti delle trame (`synopsisAction`) al posto di sei blocchi quasi identici.
- Lo sblocco di una scheda, il ritorno alla libreria e lo scaricamento di un file JSON avevano ciascuno due o tre copie.
- Date relative e dimensioni passano da `Intl`. Il pulsante occupato che trasforma un errore in messaggio è `ui.run`; la copertina di un altro sito è un'opzione di `ui.poster`; la scelta ricordata e ancora valida è `AC.memory.pick`.
- Le statistiche della cache vengono richieste al massimo una volta al minuto, e un'anteprima superata viene annullata anche sul server invece di essere solo ignorata.

### Revisione delle modifiche

Le modifiche sono passate da tre revisioni indipendenti prima del rilascio: aderenza alle regole del progetto, aderenza a quanto dichiarato in queste note, sicurezza. La revisione di sicurezza non ha trovato vulnerabilità e ha segnalato il nome della serie nel messaggio delle attività, tolto dal rapporto. Le altre due hanno trovato il pulsante dell'anteprima che poteva restare disattivato, il falso avviso di doppione per le stagioni della stessa serie, la mancanza di un punto di ripresa nella riparazione a tranche e il limite di tempo che non copriva un singolo episodio lento: tutto corretto e coperto da test.

## Verifiche eseguite

- **453 test backend** (`JellyfinVersion=12.0.0` in locale, `12.0.0` e `12.1.0` nella CI, con `-warnaserror`): 0 errori, 0 avvisi. 17 sono nuovi (`AnimeClickMaintenanceTests`): sblocco mirato di trame e titoli, punto di ripresa e limite di tempo della riparazione, anteprima della scheda, statistiche della cache, redazione del rapporto.
- **46 test browser Playwright** (`tools/AnimeClick.WebTests`): 0 errori, 0 ID duplicati, 0 overflow orizzontali (`320px`, `390px`, `1280px`). 11 sono nuovi: anteprima, manutenzione, rapporto, vetrina, novità, scelte ricordate, tranche, gruppi di trame, bersagli touch.
- **32 test Python** (`tools/tests`) e **10 test del servizio della comunità** (`community/relay`): 0 errori.
- **Prova runtime isolata** (`runtime-smoke.cjs`) su Jellyfin 12.4.0 con la DLL compilata per l'ABI minima 12.0: caricamento del plugin, configurazione, API amministrative comprese le tre nuove, rifiuto per l'utente non amministratore, setup guidato e pagina reale; nessun errore nel log del server. Un solo container su `127.0.0.1`, con limiti di CPU e memoria, rimosso a fine prova. L'ultimo controllo dello script aspettava un testo della scheda Comunità non più mostrato da quando il servizio di invio è attivo: aggiornato.
