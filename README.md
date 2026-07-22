<div align="center">
  <img src="assets/banner.png" alt="Banner AnimeClick Metadata Plugin per Jellyfin" width="100%" />

  # AnimeClick Metadata Plugin for Jellyfin

  [![Version](https://img.shields.io/badge/version-0.4.1.0-e85d04?style=flat-square)](#stato-della-versione)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-7b68ee?style=flat-square)](https://jellyfin.org/)
  [![GitHub Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square&color=blue)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
  [![License](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square)](LICENSE)
</div>

Provider Jellyfin per metadati anime in italiano da [AnimeClick.it](https://www.animeclick.it/). Il plugin segue una strategia **AnimeClick-first**: protegge i dati italiani che AnimeClick possiede e lascia agli altri provider il compito di riempire soltanto i campi mancanti.

La `0.4.1.0` aggiunge un resolver episodi/stagioni prudente per numerazioni irregolari e mantiene la funzione più utile per un catalogo italiano: sinossi episodio da TVDB/TMDB con traduzione **inglese → italiano tramite Ollama Cloud** solo come ultima risorsa.

> Lo scraping di AnimeClick è autorizzato dallo staff, limitato nel ritmo delle richieste e supportato da cache locale.

## In breve

- Titoli, trame, generi, tag, cast e staff in italiano.
- Titoli episodio anche con più stagioni, special, OVA, episodi 0, decimali, suffissi e range.
- Match fail-safe: se due episodi sono plausibili, il plugin non scrive un titolo potenzialmente errato.
- Poster AnimeClick come fallback dopo i provider di artwork ad alta risoluzione.
- Sinossi episodio native in italiano da TheTVDB/TMDB.
- Traduzione Ollama Cloud EN→IT opzionale, asincrona e memorizzata a lungo.
- Dashboard di configurazione con test provider, stato delle priorità, diagnostica e override avanzati.

## Avvio rapido

1. [Installa il plugin](#installazione) e riavvia Jellyfin.
2. Nella configurazione del plugin lascia attiva la strategia conservativa predefinita.
3. Nelle sole librerie anime metti **AnimeClick per primo tra i metadata provider** e **ultimo tra gli image provider**.
4. Se vuoi le sinossi episodio, configura almeno TMDB oppure TheTVDB; aggiungi Ollama solo per tradurre una fonte inglese.
5. Esegui un refresh dei metadati della serie. Una traduzione Ollama nuova può richiedere un secondo refresh perché viene elaborata fuori dal percorso principale.

Per la maggior parte delle librerie non serve alcun override: il resolver ricava automaticamente il layout dalla pagina AnimeClick e dalla struttura reale di Jellyfin.

## Metadati AnimeClick-first

AnimeClick ha ordine `0` per i metadata provider di serie, film ed episodi. Uno snapshot effimero post-merge riapplica i campi italiani prodotti nello stesso refresh, rispettando `IsLocked` e `LockedFields` di Jellyfin.

### Campi supportati

- titolo italiano e titolo episodio;
- trama italiana di serie e film;
- generi, target, tag e opera d'origine;
- nazionalità come località di produzione;
- cast e staff con ruoli granulari;
- trailer, teaser, promo e PV YouTube esplicitamente etichettati;
- nomi di opening ed ending nei tag;
- stato, durata, date, studi e rating quando semanticamente disponibili.

Con `OverwriteNonItalianFields=false`, valore predefinito, AnimeClick non forza i campi neutrali che database come TMDB, AniList o OMDb possono gestire meglio. Se AnimeClick non trova un dato, il campo resta disponibile ai provider successivi.

### Immagini

Le immagini AnimeClick hanno ordine `100`: sono un fallback deliberato.

- Lascia Fanart, AniList, TMDB o altri provider HD prima di AnimeClick.
- `MinPosterWidth=400` scarta per impostazione predefinita poster troppo piccoli.
- Il probe legge le dimensioni senza scaricare inutilmente l'intera immagine.
- Le foto persona sono best-effort.

## Ordine provider consigliato

Configura AnimeClick soltanto nelle librerie anime.

| Tipo | Metadata provider | Image provider |
|---|---|---|
| Serie | AnimeClick per primo; provider esterni dopo | Provider HD prima, AnimeClick ultimo |
| Stagione | AnimeClick abilitato per propagare l'ID | Provider esterni |
| Episodio | AnimeClick per primo; TMDB/OMDb dopo | Provider esterni |
| Film anime | AnimeClick per primo; TMDB/AniList/OMDb dopo | Provider HD prima, AnimeClick ultimo |

La scheda **Panoramica** legge `Library/VirtualFolders` e mostra la posizione effettiva del provider per ogni tipo. Un provider presente nell'ordine ma disabilitato non viene considerato attivo.

## Resolver episodi e stagioni v5

AnimeClick non usa sempre lo stesso schema: alcune pagine sono piatte, altre separano le stagioni, altre ancora usano schede sequel distinte. Possono inoltre comparire OVA o special tra episodi normali. Il resolver v5 conserva prima i dati originali e decide il mapping soltanto quando conosce il contesto Jellyfin.

### Principio di sicurezza

Il resolver preferisce un **miss sicuro** a un metadata sbagliato. Se il punteggio è basso o due candidati sono troppo vicini, non scrive il titolo e lascia proseguire gli altri provider.

Le evidenze principali, dalla più forte alla più prudente, sono:

1. ID episodio AnimeClick già salvato da un match precedente;
2. range esplicito per file multi-episodio (`IndexNumberEnd`);
3. override manuale della serie;
4. scheda AnimeClick specifica di un sequel/stagione;
5. gruppi stagione espliciti presenti nella pagina;
6. confini reali e contigui ricavati dalla libreria Jellyfin;
7. timeline globale canonica degli episodi regolari;
8. split uniforme dichiarato, usato solo con confidenza bassa e corroborazione di titolo o topology;
9. titolo Jellyfin come conferma, mai come unica scorciatoia numerica pericolosa.

Il layout Jellyfin viene ricalcolato dalla serie corrente: non viene mantenuta una copia in memoria che potrebbe diventare obsoleta dopo rinomini, nuovi file o cambi di stagione.

### Numerazioni gestite

| Caso | Comportamento |
|---|---|
| Pagina piatta o stagioni esplicite | Il resolver confronta timeline, gruppi e struttura Jellyfin |
| Stagioni non uniformi, per esempio `13 + 11` | Usa i confini reali invece di dividere il totale a metà |
| Numerazione assoluta o che riparte da 1 | Valuta ordinale di stagione e numero raw senza distruggere l'originale |
| S0, OVA, OAD, ONA, recap, bonus, extra, PV | Restano fuori dall'ordinale degli episodi regolari |
| Episodio `0` o prologo | Conservato come special e abbinato solo con coordinate sicure |
| Decimali `12.5` e suffissi `12A`/`12B` | Conservati ma non forzati su un numero intero ambiguo; l'ID provider può fissarli |
| Range `1-2` | Abbinato solo a un file Jellyfin con lo stesso range |
| Label `S01E01`, `2x03`, `Stagione 3 Episodio 02` | Normalizzate senza perdere la label originale |
| Righe duplicate, invertite o ripetute in paginazione | Deduplicate e ordinate tramite coordinate raw/source order |
| Libreria parziale o con buchi | Il confine incompleto richiede ulteriori evidenze, per esempio il titolo |
| Numeri duplicati o candidati equivalenti | Match rifiutato come ambiguo |

Special, decimali, suffissi e range non spostano l'ordinale degli episodi regolari. Questo evita che un OVA inserito tra E12 ed E13 faccia assegnare a tutti gli episodi successivi il titolo sbagliato.

### Catalogo raw e cache

La cache `episodes:raw:v5` contiene soltanto il catalogo estratto da AnimeClick: label originale, coordinate raw, ordine sorgente, conteggi dichiarati e fingerprint deterministico. Il mapping verso Jellyfin viene invece ricalcolato a ogni richiesta.

Conseguenze pratiche:

- cambiare la struttura delle cartelle o i numeri stagione non richiede normalmente di cancellare la cache;
- cambiare un override ha effetto sul mapping successivo;
- se è cambiata proprio la pagina AnimeClick prima della scadenza della cache, usa **Strumenti → Svuota tutta la cache**.

### Override avanzati

Usali soltanto per una serie verificata che il resolver automatico non può interpretare. Nella configurazione, sezione **Strumenti → Resolver episodi e stagioni**, inserisci una riga per serie:

```text
# I commenti iniziano con #
72=flat
123=explicit
456=13,24
789=12,24,36
```

| Sintassi | Significato |
|---|---|
| `anime-id=flat` | Jellyfin rappresenta la serie come una sola stagione piatta |
| `anime-id=explicit` | Accetta soltanto i gruppi stagione espliciti di AnimeClick |
| `anime-id=13,24` | Confini cumulativi: S1 termina a 13, S2 termina a 24 |
| `anime-id=12,24,36` | Tre stagioni da 12 episodi nella timeline globale |

L'ID può essere numerico (`72`) o canonico (`72/naruto`). I confini devono essere interi crescenti. Righe non valide vengono ignorate; se lo stesso ID compare più volte, vince la prima riga valida. Un override sbagliato può impedire un match corretto, quindi lascia il campo vuoto quando non è necessario.

## Sinossi episodio in italiano: TVDB, TMDB e Ollama

AnimeClick pubblica normalmente i titoli degli episodi, non una trama per ogni episodio. Questa funzione è **opt-in** e segue una catena rigida:

| Ordine | Fonte | Lingua | Usa Ollama? |
|---:|---|---|---|
| 1 | TheTVDB | `ita` | No |
| 2 | TMDB | `it-IT` | No |
| 3 | TMDB | `en-US` | Solo se serve tradurre |
| 4 | TheTVDB | `eng` | Solo se TMDB EN è vuoto |
| 5 | Ollama Cloud | EN → IT | Ultima risorsa |

La priorità è sempre una traduzione umana già disponibile. Ollama non viene chiamato quando TheTVDB o TMDB restituiscono una sinossi italiana.

### Cosa succede nei casi comuni

- **TVDB ha `ita`:** la sinossi viene applicata subito; TMDB e Ollama non servono.
- **TVDB non ha italiano, TMDB ha `it-IT`:** viene usato TMDB senza AI.
- **Esiste solo una sinossi inglese e Ollama è configurato:** il lavoro viene accodato; il refresh corrente lascia il campo invariato.
- **Non esiste neppure una fonte inglese:** Ollama non può inventare una trama e il campo resta invariato.
- **Timeout, HTTP error o risposta vuota:** nessun testo vuoto viene scritto e gli altri provider possono continuare.

### Perché la traduzione è asincrona

Una scansione Jellyfin non deve aspettare l'inferenza cloud. Su cache miss il flusso normale è:

1. il refresh trova la sinossi inglese;
2. crea un job deduplicato nella coda in memoria;
3. il worker elabora una traduzione alla volta;
4. il risultato viene scritto nella cache locale;
5. un refresh successivo legge la traduzione e la applica subito.

La coda è limitata a 256 elementi, elimina i duplicati e usa backoff dopo un errore: circa 5 minuti per un fallimento rapido e 15 minuti per timeout o risposta lenta. La pulizia amministrativa della cache impedisce che un job iniziato con un vecchio profilo pubblichi un risultato dopo l'invalidazione.

Le anteprime amministrative sono l'eccezione: **Test e anteprima traduzione** può eseguire intenzionalmente una chiamata sincrona end-to-end.

### Configurazione consigliata

1. Attiva **Fallback sinossi episodi**.
2. Inserisci una API key TMDB: copre `it-IT` e fornisce la sorgente `en-US`.
3. Facoltativo: attiva TheTVDB e inserisci la relativa key per provare prima `ita` e poi `eng`.
4. Inserisci la key Ollama Cloud solo se vuoi tradurre le fonti inglesi.
5. Lascia il modello consigliato `gemma4:31b-cloud` e l'endpoint `https://ollama.com/api/chat`.
6. Salva, esegui i test provider e avvia un refresh episodio.
7. Se il log indica `ollama-deferred`, attendi il worker e ripeti il refresh.

La disponibilità dei modelli, le quote e gli eventuali costi dipendono dal piano associato al tuo account Ollama; il plugin non può garantirli.

### Profilo cloud-only

Il plugin non esegue modelli sul NAS e non richiede GPU locale.

- endpoint predefinito: `https://ollama.com/api/chat`;
- modello predefinito: `gemma4:31b-cloud`;
- alternativa manuale prevista: `qwen3.5:cloud`, se disponibile per l'account;
- endpoint accettato soltanto in HTTPS, senza credenziali nell'URL, query o fragment;
- `stream=false` e `think=false`;
- una sola traduzione contemporanea nell'intero processo;
- timeout configurabile tra 5 e 120 secondi;
- nessun cambio silenzioso di modello.

Il prompt chiede una traduzione naturale da catalogo, preserva nomi propri, vieta aggiunte e richiede soltanto il testo tradotto.

### Cache delle traduzioni

Il valore predefinito è `87.600` ore, circa dieci anni, ma non rende eterno un risultato non più pertinente. La chiave `translation:v3` include:

- provider e identità della fonte;
- campo e lingue;
- modello ed endpoint;
- fingerprint unidirezionale della API key;
- versione del prompt;
- hash del testo sorgente ripulito.

Una modifica del testo, modello, endpoint, credenziale o prompt produce automaticamente una nuova chiave. La API key in chiaro non viene usata come nome file.

### Privacy

Quando abiliti i provider esterni:

- TheTVDB e TMDB ricevono le richieste necessarie a risolvere serie ed episodio, insieme alle rispettive API key;
- Ollama riceve via HTTPS il prompt fisso e la sola sinossi inglese ripulita da tradurre, oltre al modello e al bearer token;
- file video, percorso locale del media e credenziali Jellyfin non vengono inviati a Ollama;
- le chiavi sono salvate nel file XML di configurazione gestito da Jellyfin: proteggi il volume `/config` e i backup;
- API key e testo della sinossi non vengono inclusi nei messaggi diagnostici ordinari.

Il footer della pagina di configurazione carica i banner Buy Me a Coffee e PayPal dai CDN ufficiali con referrer disabilitato; logo e banner del progetto sono invece inclusi nel plugin.

## Installazione

### Catalogo plugin Jellyfin

Aggiungi il repository seguente al catalogo plugin:

```text
https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
```

Installa **AnimeClick Metadata** e riavvia Jellyfin. La release disponibile nel catalogo può essere precedente alla versione descritta dal branch di sviluppo.

### Installazione manuale

1. Scarica `AnimeClick.Plugin.zip` dalla [release più recente](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).
2. Crea una cartella dedicata sotto la directory plugin di Jellyfin.
3. Estrai almeno `AnimeClick.Plugin.dll` e `HtmlAgilityPack.dll` nella cartella.
4. Riavvia Jellyfin e verifica il caricamento nei log.

Percorsi tipici per `0.4.1.0`:

- Linux: `~/.local/share/jellyfin/plugins/AnimeClick Metadata_0.4.1.0/`
- Docker: `/config/plugins/AnimeClick Metadata_0.4.1.0/`
- Windows: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata_0.4.1.0\`

**Compatibilità:** Jellyfin `10.11.x`, ABI manifest `10.11.8.0`, target runtime `.NET 9`.

## Aggiornamento

Prima di un aggiornamento manuale salva una copia di `AnimeClick.Plugin.xml` e conserva temporaneamente la cartella precedente.

Da `0.4.0.0` a `0.4.1.0`:

- la cache raw episodi passa a `v5`; i mapping vecchi non vengono riutilizzati;
- il nuovo campo `EpisodeLayoutOverrides` parte vuoto;
- chiavi TMDB, TheTVDB e Ollama restano invariate;
- nessuna libreria, priorità provider o struttura stagione viene modificata automaticamente.

La migrazione storica da `gemma4:cloud` a `gemma4:31b-cloud` è stretta e idempotente. Un modello personalizzato non viene sostituito.

## Identificazione manuale e diagnostica

L'ID AnimeClick può essere numerico (`72`) o canonico (`72/naruto`). In **Strumenti → Identifica e aggiorna** puoi associare l'ID a un elemento Jellyfin e richiedere un refresh.

La diagnostica episodio espone coordinate raw, ordinale globale/special, fingerprint del catalogo, strategia, confidenza e motivazione del match. Queste informazioni aiutano a capire se serve davvero un override.

La pulizia globale della cache invalida metadati, cataloghi raw, mapping esterni e traduzioni. La coda Ollama viene sincronizzata con l'invalidazione per evitare la ripubblicazione di risultati obsoleti.

## Risoluzione problemi

### Non trovo la serie

- Verifica che sia una libreria anime e che AnimeClick sia abilitato per il tipo corretto.
- Prova **Identifica e aggiorna** con l'ID preso dall'URL AnimeClick.
- Controlla `BaseUrl`, connettività e rate limit nei log.

### Il titolo episodio è vuoto

- Un campo vuoto può essere un fail-safe intenzionale: cerca nel log `strategy`, `confidence` e `reason`.
- Verifica numeri stagione/episodio e `IndexNumberEnd` per file doppi.
- Controlla se OVA, E0, decimali o suffissi hanno creato un caso ambiguo.
- Usa un override solo dopo aver confermato il layout reale.

### La sinossi italiana non compare

1. Verifica che il fallback sia attivo e che stagione/episodio siano positivi.
2. Esegui i test TheTVDB, TMDB e Ollama dalla configurazione.
3. Se la fonte italiana è assente ma esiste l'inglese, cerca `ollama-deferred` nel log.
4. Attendi il worker e ripeti il refresh.
5. Dopo un errore rispetta il backoff di 5/15 minuti oppure correggi la configurazione.

### Ollama non si connette

- L'endpoint deve essere HTTPS e senza query, fragment o credenziali incorporate.
- Il profilo UI cloud-only richiede un modello con tag `cloud`.
- Dopo aver cambiato endpoint reinserisci la API key.
- Verifica quota e disponibilità del modello nel tuo account Ollama.
- Riduci il timeout soltanto se accetti più retry differiti.

### Il poster non appare

- Controlla `EnableAnimeClickImages`.
- Abbassa temporaneamente `MinPosterWidth` per capire se l'immagine è stata scartata.
- Ricorda che AnimeClick deve restare dopo i provider immagini HD.

## Cache versionate

Le principali famiglie sono separate per evitare il riuso di risultati incompatibili:

- ricerca `v3`;
- catalogo raw episodi `v5`;
- resolver schede stagione/sequel `v4`;
- mapping TMDB/TVDB `v3`, con miss confermati e single-flight;
- mapping AniList validato `v3`;
- traduzioni `v3`, isolate per contenuto e profilo.

I file sono JSON scritti tramite file temporaneo e sostituzione atomica. Elementi corrotti o troncati vengono eliminati e trattati come cache miss.

## Build e validazione

```bash
dotnet restore AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj
dotnet build AnimeClick.Plugin.csproj -c Release --no-restore
dotnet run --project AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj -c Release --no-restore
dotnet publish AnimeClick.Plugin.csproj -c Release -o publish
```

Su una macchina che dispone soltanto di un runtime .NET successivo puoi eseguire l'harness con roll-forward esplicito:

```bash
DOTNET_ROLL_FORWARD=Major dotnet run \
  --project AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj \
  -c Release --no-restore
```

L'harness corrente copre 32 regressioni, comprese numerazioni speciali, range, split `13+11`, duplicati ambigui, titoli Pilot/Prologo, topology parziale, override e deduplica della paginazione.

Il workflow GitHub verifica che un tag `vMAJOR.MINOR.PATCH.REVISION` coincida con la versione del progetto. Una release nasce sempre come **draft**: manifest esterno, MD5, source URL e smoke test dell'asset devono essere verificati prima della pubblicazione manuale.

## Fonti e attribution

| Fonte | Ruolo |
|---|---|
| [AnimeClick.it](https://www.animeclick.it/) | Fonte primaria italiana per serie, film e titoli episodio |
| [TheTVDB](https://thetvdb.com/) | Sinossi episodio opzionali `ita`/`eng` |
| [TMDB](https://www.themoviedb.org/) | Sinossi episodio `it-IT`/`en-US` e fallback Jellyfin |
| [Ollama](https://ollama.com/) | Ultima traduzione cloud EN→IT |
| [AniList](https://anilist.co/) | Risoluzione ID e artwork tramite provider Jellyfin |

**TheTVDB:** Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).

This product uses the TMDB API but is not endorsed or certified by TMDB.

## Sostieni il progetto

<div align="center">
  <a href="https://buymeacoffee.com/iCosiSenpai">
    <img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Sostieni iCosiSenpai su Buy Me a Coffee" height="50" />
  </a>
  &nbsp;&nbsp;
  <a href="https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ">
    <img src="https://www.paypalobjects.com/en_US/i/btn/btn_donateCC_LG.gif" alt="Fai una donazione con PayPal" height="47" />
  </a>
</div>

Puoi anche aprire una [issue](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues) con log anonimizzati e un esempio riproducibile.

## Stato della versione

### 0.4.1.0 — in sviluppo

- Resolver episodi v5 con catalogo raw separato dal mapping Jellyfin.
- Supporto fail-safe per special, E0, decimali, suffissi, range, righe invertite e confini non uniformi.
- Override `flat`, `explicit` e confini cumulativi configurabili dalla UI.
- Diagnostica estesa con strategia, confidenza, coordinate raw e fingerprint.
- Hero con banner repository, logo più grande e footer di supporto responsive.
- README operativo con guida completa alla pipeline TVDB/TMDB/Ollama EN→IT.

Per le versioni pubblicate consulta le [GitHub Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).

## Licenza

[GNU GPL v3](LICENSE)
