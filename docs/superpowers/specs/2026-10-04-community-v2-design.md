# Comunità per tutti — design

Data: 2026-10-04 · Stato: approvato per l'implementazione (sezioni 1 e 2 approvate; per il resto l'autore ha chiesto
di procedere senza altre revisioni).

## Obiettivo

Le correzioni che un amministratore fa nella propria libreria (per esempio «Saiki K. stagione 3 = scheda AnimeClick
26035») devono arrivare a tutti gli utenti del plugin. Deve poter contribuire anche chi non ha un account GitHub e la
comunità deve essere visibile nella pagina, non nascosta nell'ultima scheda.

Decisioni dell'autore:

- **Perimetro:** identità di serie e film (come oggi) più le **stagioni**. Niente struttura degli episodi, niente testi.
- **Invio:** un relay gratuito per tutti; chi ha un token GitHub può continuare a inviare a proprio nome.
- **Approvazione:** controlli automatici su ogni proposta, poi un clic dell'autore (etichetta `approvato`).
- **Consenso:** scelta nel setup tra *Chiedi ogni volta* (predefinito), *Sempre* e *Mai*; con *Chiedi* la proposta
  si invia solo dopo un clic su «Condividi».

## Dati

Due file nel repository, generati dalla stessa fonte (`tools/community_mappings.py`):

- `community/mappings.json` resta allo schema 1 (solo `Series` e `Movie`): le versioni ≤ 1.3 lo leggono con un
  controllo rigido e scarterebbero l'intero file se contenesse stagioni.
- `community/mappings-v2.json` è la fonte: schema 2, con stagioni e con l'indirizzo del relay.

```json
{
  "schemaVersion": 2,
  "relay": "https://animeclick-community.example.workers.dev/v1/proposals",
  "mappings": [
    { "kind": "Series", "animeClickId": "16615", "providerIds": { "Tmdb": "67676", "Tvdb": "313435" } },
    { "kind": "Season", "animeClickId": "26035", "series": { "Tmdb": "67676", "Tvdb": "313435" },
      "seasonNumber": 3, "episodeCount": 2 }
  ]
}
```

`relay` è facoltativo: finché manca, l'invio senza account resta in coda e la pagina lo dice. Pubblicarlo nel
dataset permette di attivare o spostare il relay senza una release del plugin; il plugin accetta solo un URL
`https` senza credenziali, query o frammento.

Regole di una stagione: `series` contiene solo `Tmdb` e/o `Tvdb` (gli ID AniList identificano un singolo cour, non
la serie), `seasonNumber` va da 0 a 100, `episodeCount` da 1 a 2000. Si applica a una stagione della libreria solo se:

1. la stagione non ha già un ID AnimeClick;
2. la serie ha almeno uno degli ID indicati e nessuno in contrasto;
3. il numero della stagione coincide **e** la stagione contiene esattamente `episodeCount` episodi reali;
4. il dataset non indica, per quella stagione, due schede diverse.

Chi ha suddiviso la serie in un altro modo non riceve un collegamento sbagliato. Una voce approvata ha la precedenza
sulla traversata automatica dei sequel, perché esiste proprio per correggerla; il matcher degli episodi continua a
verificare ogni riga. Le regole di serie e film non cambiano.

Uno schema JSON condiviso, `community/schema-v2.json`, descrive la proposta inviata; plugin, relay e Action ne
replicano le stesse regole e i test controllano che accettino e rifiutino gli stessi esempi
(`community/fixtures/`).

## Relay (Cloudflare Worker, `community/relay/`)

Un compito: ricevere proposte e aprire le issue. La lettura del dataset resta su `raw.githubusercontent.com`.

- `POST /v1/proposals` con `{ schemaVersion: 2, installation, pluginVersion, mapping }`, al massimo 2 KB.
  `installation` è un identificativo casuale di 32 caratteri esadecimali creato dal plugin, mai pubblicato.
- Risposte: `201 { issue, url, duplicate }`, `400` (schema), `413`, `429 { retryAfter }`, `503`.
- L'impronta (SHA-256 della proposta canonica) evita i doppioni: una proposta già presente non apre una nuova issue,
  aggiunge una **conferma** se arriva da un'installazione diversa (hash con chiave segreta, salvato in KV) e aggiorna
  la riga «Conferme» nella issue.
- Limiti: 20 proposte al giorno per installazione, 60 per IP (limitatore di Cloudflare, IP mai salvato), 300 issue
  nuove al giorno in tutto; oltre, `429` e il plugin ritenta con la coda che ha già.
- Le issue contengono solo campi validati: nessun testo libero.
- Credenziale: token GitHub fine-grained limitato a questo repository e alle sole issue, nei segreti del Worker.
- Pubblicazione da GitHub Actions (`community-relay.yml`) solo se i segreti Cloudflare esistono.

## Revisione (`community-review.yml` + `tools/community_review.py`)

- **Issue aperta** con il marcatore `mapping-<impronta>`: estrae il JSON, lo valida e verifica che la scheda
  AnimeClick esista; con le chiavi TMDB/TheTVDB tra i segreti controlla anche tipo, anno ed episodi della stagione.
  Scrive un commento con l'esito e mette `controlli-ok` o `controlli-dubbi`; chiude da sola (non pianificata) una
  proposta impossibile (schema non valido, scheda o ID inesistenti).
- **Etichetta `approvato`** (solo chi ha permessi di scrittura può metterla): aggiunge la proposta al dataset,
  rigenera i due file, fa il commit su `main`, commenta e chiude la issue come completata. Un gruppo di concorrenza
  serializza i commit.

## Plugin

- Configurazione: `CommunitySharingMode` (`Ask`, `Always`, `Never`; predefinito `Ask`). Migrazione allo schema 3:
  chi aveva `EnableCommunitySharing = true` passa a `Always`. `EnableCommunitySharing` resta solo come vecchio campo
  letto dalla migrazione.
- Lettura: `mappings-v2.json` con le stesse protezioni di oggi (24 h di cache, 1 MiB, rifiuto di campi estranei,
  attesa dopo un errore). Serie e film come prima; stagioni nel provider delle stagioni, con il conteggio degli
  episodi letto dalla libreria.
- Invio: proposte di serie, film e **stagioni**. Con un token GitHub l'invio va diretto a GitHub come oggi; altrimenti
  al relay del dataset. Ogni proposta inviata viene ricordata (impronta, issue, stato) e una volta al giorno lo stato
  delle issue aperte viene letto dall'API pubblica di GitHub; una proposta presente nel dataset è «approvata».
- Endpoint nuovi: `Community/Summary` (numeri per Inizio), `Community/Proposals`, `Community/Share` (invio dopo il
  clic). `IdentifyAndRefresh` restituisce l'anteprima della proposta quando la modalità è `Ask`.

## Interfaccia

- **Setup:** passo «Comunità» (`SETUP_VERSION` 3): «Usa gli abbinamenti della comunità» preselezionato e la scelta
  Chiedi/Sempre/Mai, con l'anteprima di cosa parte e di cosa non parte mai. Chi aggiorna vede solo questo passo.
- **Dopo una correzione:** «Questa correzione può aiutare altri utenti. Condividi · Non ora · Non chiedere più».
- **Inizio:** tessera Comunità con abbinamenti disponibili, quelli presenti nella libreria e lo stato delle proprie
  proposte.
- **Scheda Comunità:** come funziona, proposte con link alle issue, impostazioni; il token GitHub in «Avanzate».

## Privacy

Partono solo ID numerici pubblici, numero della stagione, numero di episodi, versione del plugin e l'identificativo
casuale dell'installazione (che il relay non pubblica). Mai titoli, percorsi, utenti, URL del server o token. Come
per qualsiasi servizio web, Cloudflare vede l'indirizzo IP di chi invia.

## Avvio

Il dataset oggi è vuoto. L'autore esporta i collegamenti della propria libreria con «Esporta i collegamenti della
libreria», li passa da `tools/community_review.py` e approva quelli puliti: la 1.4 parte con abbinamenti reali.

## Verifiche

- Test .NET: schema 2 e retro-compatibilità, regole delle stagioni (conteggio, conflitti, ID mancanti, stagione già
  identificata), migrazione della modalità, scelta del canale di invio, anteprima e invio dopo il clic, stato delle
  proposte.
- Test web: passo del setup mostrato a chi parte da `SetupCompletedVersion` 2, richiesta dopo la correzione,
  tessera di Inizio, scheda Comunità.
- Test del relay con `node:test` (KV, limitatore e GitHub simulati) e test Python della revisione e dello strumento.
