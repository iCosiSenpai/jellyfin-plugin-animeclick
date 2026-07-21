<div align="center">
  <img src="https://raw.githubusercontent.com/iCosiSenpai/jellyfin-plugin-animeclick/main/assets/banner-alt.png" alt="AnimeClick Metadata Plugin" />

  # AnimeClick Metadata Plugin for Jellyfin

  [![GitHub Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square&color=blue)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-purple?style=flat-square)](https://jellyfin.org/)
  [![License](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square)](LICENSE)
</div>

Provider Jellyfin per metadati anime in italiano da [AnimeClick.it](https://www.animeclick.it/). La versione 0.4 adotta un flusso **AnimeClick-first**: i dati italiani prodotti da AnimeClick restano autorevoli, mentre gli altri provider completano solo i campi mancanti.

> Lo scraping di AnimeClick è autorizzato dallo staff, rate-limited e supportato da cache locale.

## Funzionalità

### Metadati AnimeClick-first

- Titoli e trame in italiano per serie e film.
- Titoli episodi italiani con paginazione completa e matching multi-stagione.
- Generi, target, tag generici e opera d'origine.
- Nazionalità mappata come località di produzione.
- Studi, rating community, stato, durata e date quando semanticamente disponibili.
- Cast e staff con ruoli distinti: doppiatori, registi, autori, compositori, produttori, artisti, editor, coloristi e tecnici.
- Trailer, teaser, promo e PV YouTube solo quando esplicitamente etichettati; opening, ending e clip generiche non vengono promossi a trailer.
- Sigle OP/ED nei tag, se abilitate.

AnimeClick ha ordine `0` per i metadata provider di serie, film ed episodi. Uno snapshot effimero post-merge riapplica i campi prodotti nello stesso refresh rispettando `IsLocked` e `LockedFields`. Cast e staff restano AnimeClick-first tramite l'ordine dei provider; gli altri provider possono ancora aggiungere persone mancanti.

Con `OverwriteNonItalianFields=false` (default), AnimeClick non forza i campi neutrali che altri database possono gestire meglio. Se tutta la pipeline AnimeClick fallisce, il campo rimane disponibile ai provider Jellyfin successivi.

### Stagioni e franchise

Il resolver stagioni `v4`:

- segue un solo arco AnimeClick esplicitamente marcato `Sequel` per ogni passaggio di stagione;
- richiede formato Serie TV, similarità col franchise e cronologia compatibile;
- esclude OVA, special, spin-off e franchise laterali;
- si ferma senza scegliere quando il passaggio è ambiguo;
- distingue in cache risoluzioni positive, miss confermati ed errori transitori;
- gestisce sia episodi continui sulla stessa pagina sia pagine AnimeClick dedicate a ogni stagione.

Questo evita, ad esempio, di confondere serie dello stesso universo narrativo quando l'ordine cronologico delle relazioni non coincide con quello delle stagioni.

### Immagini

Le immagini AnimeClick hanno ordine `100` e sono un fallback deliberato. Fanart, AniList, TMDB o altri provider ad alta risoluzione devono restare prima di AnimeClick.

- Poster italiani per serie e film.
- Soglia `MinPosterWidth` configurabile; default 400 px.
- Probe dimensioni efficiente senza scaricare l'intera immagine.
- Foto persone best-effort.

## Sinossi episodi in italiano

AnimeClick pubblica i titoli degli episodi, ma normalmente non una trama per episodio. La funzione è opt-in e usa questa catena rigorosa:

1. TheTVDB `ita` — italiano nativo.
2. TMDB `it-IT` — italiano nativo.
3. TMDB `en-US` — fonte inglese preferita.
4. TheTVDB `eng` — seconda fonte inglese.
5. Ollama Cloud — traduzione EN→IT soltanto se le fonti italiane sono vuote.

Su timeout, errore HTTP, mapping assente o risposta vuota, il plugin restituisce `null` e lascia il campo invariato. Non viene mai scritto testo vuoto e non esiste uno switch silenzioso di modello.

Nel normale refresh, una traduzione Ollama non ancora in cache viene accodata senza attendere l'inferenza cloud. Il worker processa una richiesta alla volta, salva il risultato nella cache content-addressed e la sinossi viene applicata dal refresh successivo. Le sole anteprime diagnostiche amministrative possono eseguire esplicitamente una prova sincrona.

### Profilo cloud-only consigliato

Il NAS non esegue inferenza locale. Ollama viene contattato esclusivamente tramite HTTPS:

- endpoint: `https://ollama.com/api/chat`;
- modello predefinito: `gemma4:31b-cloud`;
- alternativa manuale: `qwen3.5:cloud` se l'account non abilita Gemma;
- `think=false` e `stream=false`;
- concorrenza globale: 1 richiesta;
- cache traduzioni predefinita: 87.600 ore.

La cache è content-addressed: fonte, campo, lingue, modello, endpoint, fingerprint unidirezionale del profilo API, versione prompt e hash del testo fanno parte della chiave. Una modifica del contenuto, del modello o della credenziale invalida automaticamente la traduzione anche con una durata lunga; la chiave in chiaro non viene mai salvata nel nome cache.

### Credenziali

- **TheTVDB**: facoltativa; abilita la sorgente `ita` e il fallback `eng`.
- **TMDB**: abilita `it-IT` e la fonte `en-US`.
- **Ollama Cloud**: necessario solo per tradurre una fonte inglese.

Le chiavi vengono salvate nel file XML di configurazione del plugin gestito da Jellyfin. Non sono incluse nei log applicativi, nelle risposte di anteprima o nei risultati mostrati dalla UI. Proteggi comunque l'accesso al volume `/config` e ai backup del server.

## Configurazione premium

La pagina plugin è organizzata in quattro aree:

- **Panoramica**: authority layer, stato TMDB/TVDB/Ollama e priorità effettiva dei provider letta da `Library/VirtualFolders`.
- **Metadati**: campi essenziali, arricchimento semantico, immagini e opzioni invasive richiudibili.
- **Fallback episodi**: onboarding cloud-only, credenziali, test connessione, anteprima traduzione e prova della catena reale.
- **Strumenti**: Identify & Refresh, invalidazione cache e parametri avanzati.

I test usano i valori correnti del form senza richiedere un salvataggio. L'anteprima della catena episodio usa invece la configurazione già persistita. Gli endpoint diagnostici richiedono un amministratore Jellyfin e non restituiscono API key.

## Ordine provider consigliato

Per le sole librerie anime:

| Tipo | Metadata provider | Image provider |
|---|---|---|
| Serie | AnimeClick per primo; provider esterni dopo | Provider HD prima, AnimeClick ultimo |
| Stagione | AnimeClick abilitato per propagare l'ID | Provider esterni |
| Episodio | AnimeClick per primo; TMDB/OMDb dopo | Provider esterni |
| Film anime | AnimeClick per primo; TMDB/AniList/OMDb dopo | Provider HD prima, AnimeClick ultimo |

Non è necessario abilitare AnimeClick nelle librerie Film o Serie TV non anime. La scheda Panoramica mostra posizione e stato per ogni tipo, filtrando dall'ordine i provider disabilitati.

## Installazione

### Repository Jellyfin

Aggiungi questo repository al catalogo plugin:

```text
https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
```

Installa **AnimeClick Metadata**, quindi riavvia Jellyfin.

### Installazione manuale

1. Scarica `AnimeClick.Plugin.zip` dalla [release più recente](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).
2. Estrai `AnimeClick.Plugin.dll` e `HtmlAgilityPack.dll` in una cartella dedicata:
   - Linux: `~/.local/share/jellyfin/plugins/AnimeClick Metadata_0.4.0.0/`
   - Docker: `/config/plugins/AnimeClick Metadata_0.4.0.0/`
   - Windows: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata_0.4.0.0\`
3. Riavvia Jellyfin.

Compatibilità: Jellyfin 10.11.x, ABI manifest `10.11.8.0`, runtime .NET 9.

## Aggiornamento da 0.3.x

La migrazione è stretta e idempotente:

- `gemma4:cloud` viene aggiornato a `gemma4:31b-cloud`;
- API key TMDB, TheTVDB e Ollama restano invariate;
- endpoint, toggle e modello personalizzato restano invariati;
- nessuna libreria o priorità provider viene modificata automaticamente.

Prima di un aggiornamento manuale è consigliato salvare una copia di `AnimeClick.Plugin.xml` e mantenere la cartella della versione precedente fino al completamento dello smoke test.

## Identificazione manuale

L'ID AnimeClick può essere numerico (`72`) o canonico (`72/naruto`). Nella scheda **Strumenti**, **Identifica e aggiorna** salva l'ID sull'elemento e avvia un refresh completo. L'opzione immagini elimina soltanto gli artwork remoti e li fa riscaricare dai provider abilitati; i file locali restano preservati.

## Cache e diagnostica

Le cache sono versionate per impedire il riuso di risultati incompatibili:

- ricerca `v3`;
- episodi `v4`;
- resolver stagioni `v4`;
- mapping TMDB/TVDB `v3` con miss confermati e single-flight;
- mapping AniList validato `v3` (titolo, anno e formato obbligatori);
- traduzioni `v3`, isolate per profilo credenziale.

La pulizia per ID invalida anche mapping esterni e traduzioni associate. La pulizia globale è disponibile nella scheda Strumenti.

## Build e validazione

```bash
dotnet restore AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj
dotnet build AnimeClick.Plugin.csproj -c Release --no-restore
dotnet run --project AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj -c Release --no-restore
dotnet publish AnimeClick.Plugin.csproj -c Release -o publish
```

Il workflow GitHub esegue build e harness, verifica che il tag `vMAJOR.MINOR.PATCH.REVISION` coincida con la versione del progetto e crea `AnimeClick.Plugin.zip`. La release nasce sempre come **draft**: prima della pubblicazione occorre aggiornare e verificare il manifest esterno con versione, MD5 e source URL esatti, quindi completare lo smoke test dell'asset. Solo dopo viene promossa manualmente a `latest`. Il pacchetto contiene soltanto il plugin e `HtmlAgilityPack.dll`.

## Fonti e attribution

| Fonte | Ruolo |
|---|---|
| [AnimeClick.it](https://www.animeclick.it/) | Fonte primaria italiana: titoli, trame, generi, tag, cast, staff, relazioni e multimedia |
| [TheTVDB](https://thetvdb.com/) | Sinossi episodi `ita`/`eng` opzionali |
| [TMDB](https://www.themoviedb.org/) | Sinossi episodi `it-IT`/`en-US` e fallback Jellyfin |
| [Ollama](https://ollama.com/) | Ultima traduzione cloud EN→IT |
| [AniList](https://anilist.co/) | Risoluzione ID e artwork tramite provider Jellyfin |

**TheTVDB:** Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).

This product uses the TMDB API but is not endorsed or certified by TMDB.

## Changelog

### 0.4.0.0 — AnimeClick authority e cloud fallback

- AnimeClick metadata provider a ordine 0 con authority layer post-merge lock-aware.
- Resolver stagioni `v4` con traversal di sequel espliciti, stop su ambiguità e ricerca anti-OVA.
- Nuovi tag/origine, località di produzione, trailer/PV e ruoli staff granulari.
- Catena sinossi TVDB ita → TMDB it-IT → EN → Ollama Cloud.
- `gemma4:31b-cloud`, gate globale 1, `think=false`, cache traduzioni content-addressed a lunga durata.
- Diagnostica e anteprime senza API key nelle risposte.
- Dashboard configurazione premium con verifica priorità librerie.
- Migrazione automatica limitata al vecchio modello predefinito.

Per le versioni precedenti consulta le [GitHub Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).

## Licenza

[GNU GPL v3](LICENSE)
