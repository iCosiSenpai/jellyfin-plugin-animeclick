# Nuova interfaccia del plugin — design

Data: 2026-10-04 · Stato: approvato per l'implementazione (l'autore ha chiesto di procedere subito, senza mockup).

## Obiettivo

Rifare da zero la pagina del plugin dentro Jellyfin, con un'identità visiva AnimeClick/anime e una struttura che
renda chiare a colpo d'occhio la pagina Inizio e la Libreria. Nessuna funzione esistente va persa, il formato della
configurazione resta compatibile e la pagina continua a funzionare offline (niente CDN, niente font esterni).

Richieste dell'autore:

- **Primo avvio:** setup guidato per chi installa il plugin.
- **Aggiornamenti importanti:** setup mirato solo con le novità che richiedono un'azione; la regola è scritta in
  `AGENTS.md` perché nessun aggiornamento futuro se ne dimentichi.
- **Aperture successive:** vetrina con le locandine degli ultimi anime aggiornati da AnimeClick, cruscotto sotto.
- **Librerie:** il setup attiva AnimeClick nelle librerie scelte con un clic, mai senza conferma.
- Correggere i bug del backend trovati lungo la strada.

## Identità visiva

- Fondo notturno blu-inchiostro, rosso AnimeClick come accento primario, rosa sakura come secondario.
- Retini manga (screentone a puntini), linee cinetiche nella vetrina, etichette decorative in katakana accanto ai
  titoli di sezione (puramente decorative, `aria-hidden`).
- Locandine ovunque abbiano senso: vetrina, libreria, scelta dell'abbinamento.
- La mascotte del plugin accompagna setup e stati vuoti.
- Tipografia: il font di Jellyfin (Noto Sans) con titoli pesanti e corsivi; nessun font scaricato.
- Rispetto di `prefers-reduced-motion`, focus visibile, contrasto AA, layout senza scroll orizzontale da 320 px.

## Struttura

Navigazione orizzontale in alto (la dashboard di Jellyfin ha già una barra laterale):

| Sezione | Contenuto |
| --- | --- |
| **Inizio** | Vetrina (hero con sfondo dell'ultimo anime aggiornato + fila di locandine), cruscotto: salute della collezione (titoli e trame in italiano), attività in corso, fonti collegate, librerie, cose da fare. |
| **Libreria** | Due pannelli di riparazione (titoli episodio, trame) con avanzamento reale e interruzione; griglia di locandine con lo stato di ogni anime; filtri, ricerca, ordinamento; scheda di dettaglio con stagioni, cause, episodi e azioni. |
| **Preferenze** | Interruttori raggruppati: identità italiana, episodi, arricchimento, immagini, opzioni invasive. |
| **Fonti** | TMDB, Fanart, TheTVDB, traduzione AI: stato, chiave, verifica, elenco modelli, prova di traduzione. |
| **Strumenti** | Correggi un abbinamento (ricerca nella libreria e su AnimeClick con locandine, oppure link), verifica di un episodio, diagnostica delle fonti, cache, parametri di rete e layout episodi. |
| **Comunità** | Abbinamenti approvati, condivisione automatica, token, stato invii, anteprima ed esportazione. |

### Setup

- `SetupCompletedVersion` (nuovo campo di configurazione) registra l'ultimo setup completato.
- Installazione nuova (`0`): setup completo — benvenuto, fonti, preferenze, librerie, fine.
- Installazione esistente: la migrazione dello schema 2 la porta a `1`; il frontend mostra solo i passi con
  `since > SetupCompletedVersion` (per questa versione: nuova interfaccia + controllo librerie).
- Saltare il setup lo segna comunque come completato; si può riaprire da Strumenti.

## Backend

- **Bug:** l'analisi di una singola serie (`LibraryAuditSeries`) non calcolava i conteggi recuperabili, in attesa e
  non disponibili, che tornavano a zero dopo "Analizza questa serie".
- **Configurazione:** `SetupCompletedVersion` + migrazione schema 2. I passi dello schema 1 restano confinati alle
  installazioni sotto lo schema 1, così un timeout scelto a 30 s non viene riscritto.
- **Nuovi endpoint** (amministratore):
  - `GET Plugins/AnimeClick/Showcase?limit=` — serie e film con ID AnimeClick ordinati per ultimo aggiornamento dei
    metadati, con i dati per vetrina e locandine.
  - `GET Plugins/AnimeClick/Libraries` — librerie video con lo stato di AnimeClick per tipo (metadati/immagini,
    abilitato, primo nell'ordine).
  - `POST Plugins/AnimeClick/Libraries/Enable` — aggiunge AnimeClick ai metadati (primo) e alle immagini dei tipi
    pertinenti di una libreria. Le immagini vanno in testa solo se le immagini integrate TMDB/Fanart sono configurate,
    altrimenti in coda come riserva. Le altre fonti non vengono rimosse; le voci di tipo assenti non vengono create.

## Frontend

JavaScript vanilla senza build, file separati per responsabilità, caricati in ordine dalla pagina:

- `animeclick-core.js` — DOM, API, store, toast, modale, icone, componenti di modulo.
- `animeclick-settings.js` — mappatura configurazione, salvataggio con fusione e rilevamento conflitti, Preferenze,
  Fonti, Comunità, Strumenti.
- `animeclick-library.js` — analisi, unione titoli+trame per anime, griglia, dettaglio, attività.
- `animeclick-home.js` — vetrina e cruscotto.
- `animeclick-setup.js` — setup iniziale e di aggiornamento (registro dei passi).
- `animeclick-app.js` — shell, navigazione, ciclo di vita.

Il comportamento di salvataggio esistente resta identico: si scrivono solo le chiavi modificate, una modifica
concorrente alla stessa chiave blocca il salvataggio, una chiave AI salvata non segue mai una destinazione diversa,
la rimozione della chiave AI cancella anche la copia storica.

## Test

- .NET: conteggi della singola serie, migrazione schema 2, stato e attivazione delle librerie, vetrina.
- Web (Playwright): riscritti per la nuova interfaccia mantenendo ogni comportamento coperto prima, più setup,
  attivazione librerie e vetrina; schermate desktop e mobile.
