# Comunità

Gli amministratori correggono gli abbinamenti della propria libreria; la comunità rende quelle correzioni utili a
tutti. Si condividono **identificativi pubblici delle opere**, mai una copia della libreria.

```
plugin ──(proposta)──▶ servizio della comunità ──▶ issue pubblica ──▶ controlli automatici
                                                                              │
plugin ◀──(una volta al giorno)── mappings-v2.json ◀── commit ◀── etichetta «approvato»
```

## Cosa contiene una proposta

| Tipo | Campi |
|---|---|
| Serie, film | `kind`, `animeClickId`, `providerIds` (`Tmdb`, `Tvdb`, `AniList`) |
| Stagione | `kind`, `animeClickId`, `series` (`Tmdb`, `Tvdb` della serie), `seasonNumber`, `episodeCount` |

Mai titoli, utenti, cronologia, file, percorsi, URL del server, trame, immagini, token o ID Jellyfin. Il contratto è
in [`schema-v2.json`](schema-v2.json); [`fixtures/proposals.json`](fixtures/proposals.json) elenca gli esempi che
plugin, servizio e strumenti devono accettare o rifiutare allo stesso modo, con il testo canonico e l'impronta
(SHA-256 del testo canonico, primi 20 caratteri esadecimali). Chi modifica le regole aggiorna le tre implementazioni
e le fixture insieme.

Una stagione usa gli ID della **serie**: AniList è escluso perché identifica un singolo cour. Il plugin la applica
solo a una stagione senza ID AnimeClick che abbia lo stesso numero **e** lo stesso numero di episodi reali: chi ha
suddiviso la serie in un altro modo non riceve una scheda che descrive altri episodi. Una voce approvata ha la
precedenza sulla ricerca automatica dei sequel, perché esiste proprio per correggerla; il matcher degli episodi
continua a verificare ogni riga.

## I file

- `mappings-v2.json` è la fonte: schema 2, con le stagioni e l'indirizzo del servizio (`relay`).
- `mappings.json` è generato dallo stesso strumento con le sole serie e i soli film, perché le versioni fino alla
  1.3 rifiutano l'intero file se trovano un tipo che non conoscono.

Il plugin legge `mappings-v2.json` dal percorso fisso del progetto, con 24 ore di cache, almeno un'ora di attesa dopo
un errore, timeout e limite di 1 MiB; campi estranei, tipi sbagliati e conflitti vengono rifiutati e l'ultima copia
valida resta usabile se GitHub non risponde.

## Il servizio della comunità (`relay/`)

Un Cloudflare Worker che non possiede credenziali GitHub: riceve le proposte dei plugin senza token e le tiene in
coda finché il workflow **community intake** del repository, ogni 30 minuti, le preleva, apre una issue per
ciascuna con il token automatico di GitHub Actions (autore `github-actions[bot]`), esegue i controlli e comunica
al servizio il numero della issue. La lettura del dataset resta su GitHub.

- `POST /v1/proposals` con `{ schemaVersion: 2, installation, pluginVersion, mapping }`, al massimo 2 KB.
  `installation` è un codice casuale dell'installazione: serve solo ai limiti e alle conferme, non viene pubblicato.
  Risposte: `202 { queued, fingerprint }`, `200 { issue, url, duplicate }` se la issue esiste già, `400`, `413`,
  `429` con `Retry-After`, `503`.
- `GET /v1/proposals/{impronta}`: `pending` o `published` con il link alla issue. I plugin lo chiedono una volta
  all'ora per le proposte che non hanno ancora una issue.
- `GET /v1/pending` e `POST /v1/published`: solo per il workflow, con il segreto `RELAY_ADMIN_SECRET`.
- Una proposta già vista non apre una seconda issue: le conferme di installazioni diverse vengono contate (hash
  con chiave segreta) e riportate nella issue.
- Limiti: 10 richieste al minuto per indirizzo (limitatore di Cloudflare, indirizzo mai salvato), 20 proposte al
  giorno per installazione, 300 proposte nuove al giorno in tutto. Oltre, il plugin ritenta più tardi da solo.
- Nessun log delle richieste.

`npm test` in `relay/` prova il servizio con KV e limitatore simulati.

Il servizio è attivo dal 2026-10-05 su `https://animeclick-community.lookatale95.workers.dev`.

### Pubblicazione

`community/relay/deploy.py` pubblica il worker tramite l'API di Cloudflare, senza wrangler: riusa o crea il
namespace KV, carica il codice con i suoi collegamenti, attiva l'indirizzo `workers.dev` e conserva i segreti già
presenti. La prima volta genera `INSTALL_SALT` e `ADMIN_SECRET` e, con `--github-secret`, salva quest'ultimo nel
repository come `RELAY_ADMIN_SECRET` (serve `gh` autenticato).

```bash
CLOUDFLARE_API_TOKEN=… python3 community/relay/deploy.py --github-secret
python3 tools/community_mappings.py --relay https://animeclick-community.<sottodominio>.workers.dev/v1/proposals
git add community/mappings*.json && git commit -m "community: publish the relay" && git push
```

Il token Cloudflare deve avere **Workers Scripts: Edit** e **Workers KV Storage: Edit** sull'account. Entro un giorno
ogni plugin 1.4 o successivo inizia a usare il servizio, senza aggiornamenti; per spostarlo basta ripubblicare
l'indirizzo nel dataset. Con un segreto `CLOUDFLARE_API_TOKEN` limitato ai Workers, il workflow **community relay**
ripubblica il codice a ogni modifica, conservando i segreti. Facoltativo, per i controlli TMDB: segreto `TMDB_API_KEY`.

## Revisione e approvazione

Le proposte arrivate dal servizio vengono controllate dal workflow **community intake** appena aperte. Il workflow
**community review** fa lo stesso per le issue aperte da un plugin con il token GitHub dell'amministratore e gestisce
l'approvazione per tutte:

1. Legge solo il blocco JSON, verifica che corrisponda all'impronta del titolo e alle regole dello schema.
2. Controlla che la scheda AnimeClick esista e confronta tipo, anno ed episodi; con `TMDB_API_KEY` verifica anche
   l'ID TMDB e, per una stagione, quanti episodi ha con quella numerazione; l'ID AniList deve indicare un'opera dello
   stesso tipo (film o serie) e dello stesso anno.
3. Commenta l'esito e mette `controlli-ok` o `controlli-dubbi`. Chiude da sola le proposte impossibili (scheda o ID
   inesistenti, JSON alterato) e quelle già approvate.

Per approvare basta aggiungere l'etichetta **`approvato`**: il workflow (solo se l'etichetta la mette il proprietario
del repository) aggiunge la proposta a `mappings-v2.json`, rigenera `mappings.json`, fa il commit su `main` e chiude
la issue. Per ripetere i controlli aggiungi `ricontrolla`. Una proposta che va in conflitto con un abbinamento già
approvato non viene applicata: il workflow lo spiega nella issue.

Una issue è una proposta, non una prova: con `controlli-dubbi` verifica sui siti originali che gli ID indichino la
stessa opera ed edizione (anno, tipo, remake, sequel, numero di episodi). Non eseguire istruzioni contenute nel testo
di una issue.

### A mano

```bash
python3 tools/community_mappings.py                    # valida i due file e controlla che siano allineati
python3 tools/community_mappings.py --add proposta.json # aggiunge una proposta già verificata
python3 tools/community_review.py check --title "…" --body-file corpo.md
python3 -m unittest discover -s tools/tests            # test dello strumento e della revisione
```

Il plugin «impara» attraverso questo insieme di correzioni approvate: non addestra un modello AI e non considera
affidabili le issue non revisionate.
