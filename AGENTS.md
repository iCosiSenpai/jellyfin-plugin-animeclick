# Istruzioni per gli agenti — AnimeClick per Jellyfin

## Aggiornamenti importanti: aggiungi sempre il passo di setup

La pagina del plugin ha un **setup guidato** versionato. Un'installazione nuova lo percorre tutto; chi aggiorna vede
**solo i passi aggiunti dopo l'ultimo setup completato** (`SetupCompletedVersion` nella configurazione).

Ogni rilascio che chiede una decisione o un'azione all'utente **deve** registrare il suo passo, altrimenti chi aggiorna
non se ne accorge. È importante, per esempio:

- una nuova chiave o fonte senza la quale una funzione resta spenta;
- una preferenza nuova o cambiata che modifica cosa viene scritto nella libreria;
- un cambio nei provider delle librerie, una migrazione da rivedere, un comportamento predefinito diverso.

Non serve per correzioni di bug, testi o grafica che non chiedono nulla all'utente.

Come si fa:

1. In `Web/assets/animeclick-setup.js` aumenta `SETUP_VERSION` di uno (mai riusare o abbassare un numero).
2. Aggiungi il passo a `STEPS` con `since: <nuova versione>`; usa `updateOnly: true` se ha senso solo per chi
   aggiorna (le novità). Il passo deve spiegare cosa cambia e lasciare scegliere: niente modifiche senza conferma.
3. Aggiungi in `tools/AnimeClick.WebTests/config.test.cjs` un test che apra la pagina con la
   `SetupCompletedVersion` precedente e verifichi che compaiano solo i passi nuovi.
4. Citalo nelle note di rilascio (`docs/releases/<versione>.md`).

## Struttura del frontend

JavaScript vanilla senza build, servito come risorse incorporate. `Configuration/configPage.html` carica nell'ordine
`core → settings → library → home → setup → app` (array `SCRIPTS`) e `Plugin.GetPages()` registra ogni file. Un test
.NET verifica che ogni script caricato sia registrato e incorporato: aggiungendo un file aggiorna entrambi.

- I controlli legati alla configurazione hanno `data-key="<Proprietà>"` e ID `ac<Proprietà>`.
- Il salvataggio scrive solo le chiavi cambiate e si blocca se un'altra sessione ha cambiato le stesse: non
  sostituire `commit()` con un salvataggio dell'intero oggetto.
- Una chiave AI salvata non deve mai seguire una destinazione diversa (regola replicata anche lato server).

## Comunità

Le regole di una proposta vivono in tre implementazioni: il plugin (`Services/AnimeClickCommunityData.cs`), il
servizio (`community/relay/src/proposal.js`) e gli strumenti (`tools/community_mappings.py`). Cambiandole, aggiorna
tutte e tre insieme a `community/schema-v2.json` e agli esempi di `community/fixtures/proposals.json`, che i test di
ciascuna leggono. `community/mappings.json` (schema 1) si rigenera con `python3 tools/community_mappings.py --write`
e non si modifica a mano: le versioni ≤ 1.3 lo leggono ancora. Il relay non ha credenziali GitHub: le issue le apre
il workflow `community-intake.yml`, che non va programmato su GitHub (60 giorni di inattività lo sospenderebbero): lo
avvia il NAS. Si ripubblica con `community/relay/deploy.py` (vedi `community/README.md`).

## Licenza

Dalla 1.6.0.0 il codice è AGPLv3 con i termini aggiuntivi di `NOTICE` (sezione 7). L'attribuzione e il collegamento
al codice sorgente nel piè di pagina (`#acAttribution`, `#acSourceLink`) sono obbligatori e un test li verifica: non
toglierli. Il `meta.json` dello ZIP (`tools/release_meta.py`) deve portare il `Name` del plugin, mai il nome del
pacchetto nel catalogo: Jellyfin raggruppa le versioni per nome (issue #2).

## Versione e verifiche

- La versione a quattro parti vive nel `.csproj`, in `configPage.html`, in `animeclick-core.js` e nello User-Agent di
  `PluginConfiguration.cs`; la CI controlla che coincidano. Procedura completa in `docs/RELEASING.md`.
- **Ogni release va pubblicata anche nel catalogo** `iCosiSenpai/iCosiSenpai-Plugins` (`manifest.json`), dopo aver
  verificato l'asset pubblico: è una regola del proprietario, non un passo facoltativo. Il catalogo contiene solo
  versioni per Jellyfin 12 o successivo; non rimettere versioni 0.x per Jellyfin 10.x, che sono obsolete.
- Test: `dotnet test AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj -c Release`, in
  `tools/AnimeClick.WebTests` `npm test`, `python3 -m unittest discover -s tools/tests` e, in `community/relay`,
  `npm test`. Le schermate finiscono in `test-results/` (ignorata da git).

## Il computer di sviluppo è il NAS di produzione

Compilazioni e server di prova competono con i servizi del NAS. Preferisci la CI di GitHub per le compilazioni
pesanti; in locale usa al massimo **un** Jellyfin usa-e-getta (`runtime-smoke.cjs`: solo loopback, mai la porta
8096, `--cpus`/`--memory` limitati, dati fuori da `/volume1`), rimuovilo subito e annotalo in
`/volume1/docker/AGENT.md`. Il container `jellyfin` di produzione si tocca solo su richiesta esplicita del
proprietario, seguendo `docs/RELEASING.md` e il registro del NAS.
