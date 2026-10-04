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

Un Cloudflare Worker con un solo compito: ricevere le proposte dei plugin senza token e aprire una issue per ciascuna.

- `POST /v1/proposals` con `{ schemaVersion: 2, installation, pluginVersion, mapping }`, al massimo 2 KB.
  `installation` è un codice casuale dell'installazione: serve solo ai limiti e alle conferme, non viene pubblicato.
- Risposte: `201`/`200 { issue, url, duplicate }`, `400`, `413`, `429` con `Retry-After`, `503`.
- Una proposta già vista non apre una seconda issue: se arriva da un'altra installazione aggiunge una **conferma**
  (riga «Conferme» nella issue). Le installazioni sono salvate solo come hash con chiave segreta.
- Limiti: 10 richieste al minuto per indirizzo (limitatore di Cloudflare, indirizzo mai salvato), 20 proposte al
  giorno per installazione, 300 issue nuove al giorno in tutto. Oltre, il plugin ritenta più tardi da solo.
- Le issue contengono solo campi validati: nessun testo libero arriva al repository.
- Nessun log delle richieste (`observability` disattivata).

`npm test` in `relay/` prova il servizio con KV, limitatore e GitHub simulati.

### Attivazione (una volta, a cura del curatore del plugin)

Finché il servizio non è attivo, il plugin tiene le proposte in coda e la pagina lo dice; chi ha un token GitHub può
già inviare. Per attivarlo:

1. **Cloudflare** (piano gratuito): crea un account, poi in *Storage & Databases → KV* crea il namespace
   `animeclick-community` e copia il suo ID. Crea un token API con il modello *Edit Cloudflare Workers* e annota
   l'Account ID.
2. **GitHub, token del servizio**: crea un token *fine-grained* limitato al solo repository
   `jellyfin-plugin-animeclick`, permesso **Issues: Read and write** e nient'altro, scadenza un anno. Le issue
   risulteranno aperte dall'account che crea il token: per separarle dal tuo account personale puoi usare un account
   dedicato con accesso al repository.
3. **Segreti del repository** (*Settings → Secrets and variables → Actions*):
   - segreti `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `RELAY_GITHUB_TOKEN` (il token del passo 2) e
     `RELAY_INSTALL_SALT` (un testo casuale lungo, per esempio `openssl rand -hex 32`);
   - variabile `RELAY_KV_ID` con l'ID del namespace KV;
   - facoltativo, per i controlli TMDB delle proposte: segreto `TMDB_API_KEY`.
4. Avvia il workflow **community relay** (*Actions → community relay → Run workflow*). Il log del passo di
   pubblicazione mostra l'indirizzo, del tipo `https://animeclick-community.<nome>.workers.dev`.
5. Pubblica l'indirizzo nel dataset e fai il commit:

   ```bash
   python3 tools/community_mappings.py --relay https://animeclick-community.<nome>.workers.dev/v1/proposals
   git add community/mappings*.json && git commit -m "community: publish the relay" && git push
   ```

   Entro un giorno ogni plugin 1.4 o successivo inizia a usarlo, senza aggiornamenti. Per spostarlo basta
   ripetere il passo 5 con il nuovo indirizzo.

Ogni anno, prima della scadenza, rinnova `RELAY_GITHUB_TOKEN` e rilancia il workflow.

## Revisione e approvazione

Il workflow **community review** gira su ogni issue con il marcatore `mapping-<impronta>` nel titolo, che arrivi
dal servizio o da un plugin con token:

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
