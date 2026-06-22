<div align="center">
  <img src="https://raw.githubusercontent.com/iCosiSenpai/jellyfin-plugin-animeclick/main/assets/banner-alt.png" alt="AnimeClick Metadata Plugin" />

  # AnimeClick Metadata Plugin for Jellyfin

  [![GitHub Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square&color=blue)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11+-purple?style=flat-square)](https://jellyfin.org/)
  [![License](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square)](LICENSE)
</div>

Plugin per [Jellyfin](https://jellyfin.org/) che fornisce **metadati anime in italiano** da [AnimeClick.it](https://www.animeclick.it/), la principale community italiana dedicata all'animazione giapponese.

> **Nota**: Questo plugin utilizza scraping etico del sito AnimeClick, autorizzato dallo staff. Tutte le richieste sono rate-limited e i dati vengono cacheati localmente.

## ✨ Funzionalità

### Metadati Testuali
- **Titoli in italiano** (con opzione per titolo originale giapponese)
- **Trama/sinossi** in italiano
- **Titoli episodi** in italiano con matching multi-stagione basato su normalizzazione AnimeClick
- **Sinossi episodi in italiano** (opzionale): AnimeClick non pubblica sinossi per-episodio, quindi il plugin può recuperare l'overview inglese dell'episodio da TMDB e tradurla in italiano via Ollama Cloud. Vedi la sezione [Sinossi episodi IT](#-sinossi-episodi-in-italiano-tmdb--ollama-cloud).
- **Generi** in italiano (Commedia, Fantascienza, Scolastico, ecc.)
- **Tag** (Shounen, Seinen, Mecha, Isekai, ecc.)
- **Anno di produzione** e **data premiere**
- **Valutazione community** AnimeClick (scala 1-10)
- **Stato serie** (completato → Ended, in corso → Continuing)
- **Studi di animazione**
- **Content rating** (se disponibile)
- **Sigle OP/ED** come tag, in modalita best-effort quando AnimeClick espone dati strutturati

### Cast & Staff
- **Doppiatori giapponesi** (seiyuu) con nome del personaggio
- **Doppiatori italiani** con nome del personaggio
- **Registi**
- **Autori** (soggetto originale, sceneggiatura, series composition)
- **Compositori** (colonne sonore)

### Immagini
- **Locandina italiana di fallback** per Serie TV e Film (priorità bassa: AniList/Fanart/TMDB vincono se hanno immagini; AnimeClick riempie solo i buchi). Attivabile con l'opzione *Usa locandina AnimeClick come fallback*.
- **Foto doppiatori/staff** (provider `AnimeClickPersonImageProvider`) per le entità Person.

### Collezioni Automatiche
- Rilevamento **sequel, prequel e spin-off** tramite la pagina relazioni di AnimeClick
- I titoli correlati vengono raggruppati in BoxSet

### Multi-Stagione
- **Stagioni sulla stessa pagina**: il parser normalizza numero assoluto, progressivo di stagione, stagione, URL dettaglio e ID episodio AnimeClick
- **Matching universale**: quando AnimeClick espone gruppi stagione, Jellyfin `S02E01` viene abbinato al progressivo della seconda stagione, non al vecchio episodio 1 della prima stagione
- **Stagioni su pagine separate**: per anime con pagine AnimeClick distinte (es. Sword Art Online → SAO II → Alicization), il plugin risolve automaticamente la pagina corretta di ogni stagione tramite le relazioni
- **Filtro spin-off**: titoli contenenti "Alternative", "Gaiden", "Spin-off" o "Bangai-hen" vengono esclusi dalla mappatura automatica
- **SeasonProvider**: imposta l'ID AnimeClick corretto sull'entità Season di Jellyfin

### Librerie Supportate
| Tipo | Metadati Testuali e Cast | Locandine e Art |
|------|----------|----------|
| 📺 Serie TV | ✅ | ✅ fallback (AnimeClick) — vince AniList/Fanart/TMDB se hanno immagini |
| 🎬 Film | ✅ | ✅ fallback (AnimeClick) — vince AniList/Fanart/TMDB se hanno immagini |
| 📅 Stagioni | ✅ (ID Provider) | ❌ (Usa TMDB/Fanart) |
| 📝 Episodi | ✅ (Titoli Ita + sinossi IT opzionale via TMDB+Ollama) | ❌ |

### Funzionalità Tecniche
- **Cache locale** con TTL configurabile (default: 48h) — copre metadati AnimeClick, ID TMDB risolti, overview episodi TMDB e traduzioni Ollama (per-episodio)
- **Rate limiting** integrato (default: 1 richiesta/secondo)
- **Merge "Italian-wins + fill-gaps"**: i provider Series/Movie/Episode girano con `Order=100` (per ultimi). AnimeClick vince sui campi localizzati (titolo, trama, generi, tag, cast, titoli episodi IT) e lascia i campi language-neutral (titolo originale, studio, rating, date) ai provider a monte quando `OverwriteNonItalianFields=false` (default). Empty-guard su tutti i campi per non svuotare mai dati già presenti.
- **ID AniList risolto a ogni scan** (GraphQL by-title) e scritto su `ProviderIds["AniList"]`, così l'image provider AniList trova le copertine automaticamente.
- **ID TMDB risolto on-demand** (search/tv by titolo originale + anno) per la traduzione delle sinossi episodi.
- **Identificazione manuale** tramite ID AnimeClick (formato: `72/naruto` dall'URL) + pulsante **Identify & Refresh** (vedi sotto)
- **Link esterno** diretto alla pagina AnimeClick nella sidebar
- **Pagina di configurazione** completa nella dashboard Jellyfin
- **Diagnostica admin** per provare lookup, preview episodi normalizzati e pulizia mirata della cache

## 📦 Installazione

### Da Repository Plugin (consigliato)

1. In Jellyfin, vai su **Dashboard → Plugin → Repositories**
2. Aggiungi un nuovo repository con URL:
   ```
   https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
   ```
3. Vai su **Catalogo**, cerca "AnimeClick Metadata" e installa
4. Riavvia Jellyfin

### Installazione Manuale

1. Scarica l'ultima release dalla [pagina Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
2. Estrai lo zip nella cartella plugin di Jellyfin:
   - **Linux**: `~/.local/share/jellyfin/plugins/AnimeClick Metadata_0.2.8.0/`
   - **Docker**: `/config/plugins/AnimeClick Metadata_0.2.8.0/`
   - **Windows**: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata_0.2.8.0\`
3. Riavvia Jellyfin

> **💡 Altri miei plugin:** Nello stesso repository trovi anche [KometaThemes](https://github.com/iCosiSenpai/KometaTheme), che scarica automaticamente le sigle OP/ED degli anime da animethemes.moe.

## ⚙️ Configurazione

Dopo l'installazione, vai su **Dashboard → Plugin → AnimeClick Metadata** per configurare:

### Metadati
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Preferisci titolo italiano | ✅ | Usa il titolo italiano come nome della serie |
| Sovrascrivi campi non-italiani | ❌ | Se attivo, AnimeClick sovrascrive anche titolo originale, studio, rating, data e classificazione (campi che AniList/TMDB/OMDb gestiscono meglio). Se spento (default), AnimeClick emette solo i campi localizzati (titolo, trama, generi, tag, cast) e lascia i buchi agli altri provider (fill-gaps). |
| Importa trama | ✅ | Importa la sinossi in italiano |
| Importa generi | ✅ | Importa i generi (Commedia, Fantascienza, ecc.) |
| Importa studi | ✅ | Importa gli studi di animazione |
| Importa valutazione | ✅ | Importa il rating community |
| Importa cast e staff | ✅ | Doppiatori, registi, autori, compositori |
| Importa tag | ✅ | Tag come Shounen, Seinen, Mecha |
| Importa titoli episodi | ✅ | Titoli italiani degli episodi da /episodi, con matching per progressivo di stagione |
| Usa locandina AnimeClick come fallback | ✅ | Fornisce la locandina italiana di AnimeClick come immagine fallback per Serie/Film (priorità bassa: AniList/Fanart/TMDB vincono se hanno immagini) |
| Crea collezioni automatiche | ❌ | Raggruppa sequel/prequel in BoxSet |
| Importa sigle OP/ED | ✅ | Aggiunge i nomi delle sigle come tag quando AnimeClick espone OP/ED strutturati |

### Sinossi episodi in italiano (TMDB + Ollama Cloud)
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Traduci le sinossi degli episodi in italiano | ❌ | Recupera l'overview EN dell'episodio da TMDB e la traduce in IT via Ollama Cloud. Opt-in. |
| TMDB API key | *(vuoto)* | API key TMDB gratuita — vedi tutorial sotto |
| Ollama Cloud API key | *(vuoto)* | Bearer key Ollama Cloud — vedi tutorial sotto |
| Endpoint Ollama Cloud | `https://ollama.com/api/chat` | Endpoint chat Ollama Cloud |
| Modello cloud | `gemma4:cloud` | Modello Ollama Cloud per la traduzione EN→IT (dropdown con modelli consigliati + personalizzato) |
| Timeout traduzione (sec) | `30` | Timeout per una singola chiamata di traduzione |

### Ricerca
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Risultati massimi per ricerca | `10` | Numero massimo di risultati per ricerca su AnimeClick (1-25) |
| Filtra solo anime | ✅ | Esclude manga, novel e drama dai risultati di ricerca |

### Cache & Performance
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Cache metadati (ore) | `48` | Durata cache dati scaricati (metadati, ID TMDB, overview TMDB, traduzioni Ollama) |
| Cache negativa (ore) | `12` | Durata cache per risultati vuoti |
| Delay richieste (ms) | `1000` | Pausa tra richieste HTTP |

### Avanzate
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| URL base | `https://www.animeclick.it` | URL di AnimeClick |
| User-Agent | `AnimeClick-Jellyfin-Plugin/0.2.8.0` | Identificativo per le richieste |

### Diagnostica
La pagina plugin include strumenti admin per:
- testare il ranking lookup con titolo e anno;
- vedere episodi normalizzati con numero assoluto, progressivo di stagione e ID episodio;
- pulire in modo mirato cache episodi, mappa stagioni e metadati AnimeClick.

## 🔍 Identificazione Manuale

Per identificare manualmente un anime:

1. Cerca l'anime su [AnimeClick.it](https://www.animeclick.it/)
2. Copia l'ID completo dall'URL: per `animeclick.it/anime/72/naruto` → `72/naruto`
3. In Jellyfin, clicca sull'anime → **Identifica** → inserisci l'ID nel campo "AnimeClick"

> **Nota:** Puoi anche inserire solo l'ID numerico (es. `72`) e il plugin lo troverà automaticamente tramite ricerca.

### ⚠️ Identify → Save → Refresh (Jellyfin 10.11.x)

Jellyfin 10.11.x ha un comportamento subdolo: quando identifichi un item via
"Identify → Save", l'API `POST /Items/RemoteSearch/Apply` salva l'ID provider
sull'item ma **NON** triggera un metadata refresh. Risultato: lo spinner termina
in un secondo e l'item resta con titolo/cast/descrizione vuoti o vecchi fino a
quando non clicchi manualmente "Refresh & replace" o non riavvii la libreria.

Inoltre, **se l'ID provider è già lo stesso che stai salvando** (es. hai già
identificato l'item in passato e stai solo ri-applicando lo stesso ID), Jellyfin
considera la modifica un no-op e non fa partire nessun refresh automatico.

Questo plugin (dalla v0.2.2.0) risolve il problema con un pulsante dedicato
**"Identify & Refresh"** nella pagina di configurazione del plugin (sezione
*Diagnostica*):

1. Apri Dashboard → Plugin → AnimeClick Metadata
2. Scorri fino a **Diagnostica → Identify & Refresh**
3. Inserisci:
   - **Item ID Jellyfin**: l'UUID dell'item (lo trovi nell'URL della pagina del film)
   - **AnimeClick ID**: l'ID nel formato `numeroslug` (es. `4371/hanasaku-iroha-movie-2013`)
4. (Opzionale) Spunta **Sostituisci anche le immagini** se vuoi che il plugin
   cancelli le immagini remote esistenti (poster, backdrop, logo) prima del
   refresh, così gli ImageFetchers (Fanart, AniList, TMDB, OMDb) possono
   riscaricare artwork migliore. Le immagini locali (`folder.jpg`,
   `poster.jpg`, `backdrop.jpg` nella cartella del film) sono sempre
   preservate.
5. Clicca **Identify & Refresh**: il plugin salva l'ID e triggera immediatamente
   un `MetadataRefreshOptions { MetadataRefreshMode = FullRefresh, ReplaceAllMetadata = true, ReplaceAllImages = <come checkbox> }`
6. Attendi qualche secondo (3-5 secondi per un film con cast, 5-10 secondi
   se hai spuntato anche le immagini): titolo italiano, trama, generi, cast
   e staff compaiono sulla pagina dell'item, e (se richiesto) le copertine
   vengono aggiornate.

C'è anche un pulsante **Stato Identify** per verificare velocemente se un item
ha già l'ID provider AnimeClick impostato.

> **Alternativa manuale**: clicca sul film → ⋮ → "Refresh & replace metadata".
> Funziona anche senza il pulsante del plugin, ma devi farlo esplicitamente.
> Il pulsante del plugin replica esattamente quel flusso (incluso il wipe
> delle immagini remote) senza dover navigare nel menu contestuale.

## 🧠 La Filosofia del Plugin (Configurazione Ideale 2026)

AnimeClick è in assoluto l'enciclopedia più completa per quanto riguarda i **Testi** (Sinossi, Titoli) e i **Doppiatori Italiani** nel mondo degli Anime.
Tuttavia, *non* è un database nato per fornire Locandine, Fanart o Sfondi ad alta risoluzione: le copertine di AnimeClick sono spesso a bassa risoluzione, contengono loghi o presentano difetti di aspect-ratio.

Il plugin segue quindi un modello **"Italian-wins + fill-gaps"**:

- **Authority italiana** sui campi localizzati: titolo, trama, generi, tag, cast/doppiatori, titoli episodi. AnimeClick vince su questi campi (gira con `Order=100`, per ultimo).
- **Fill-gaps sui campi language-neutral**: titolo originale, studio, rating, data, classificazione sono lasciati ad AniList/TMDB/OMDb quando `OverwriteNonItalianFields=false` (default). AnimeClick non li sovrascrive se non glielo chiedi esplicitamente.
- **Fallback immagini** (Series/Movie): il plugin fornisce la locandina italiana di AnimeClick **solo come ultima risorsa**, con priorità bassa (`Order=100`): AniList/Fanart/TMDB vincono se hanno immagini, AnimeClick riempie i buchi quando nessuno consegna. Disattivabile con *Usa locandina AnimeClick come fallback*.
- **Foto doppiatori**: provider `AnimeClickPersonImageProvider` per le entità Person (priorità alta, `Order=0`).
- **Sinossi episodi IT (opzionale)**: AnimeClick non ha sinossi per-episodio; il plugin può recuperarne l'overview inglese da TMDB e tradurla via Ollama Cloud (feature opt-in, vedi sotto).

### 🌐 La Mia Configurazione (plugin facoltativi ma utili per il fallback)

> **Queste sono le impostazioni esatte del mio server Jellyfin NAS nel 2026.**
> Puoi copiarle pari-pari: sono il risultato di mesi di test su centinaia di anime.
>
> AnimeClick fa il grosso del lavoro da solo — i plugin sotto servono come safety net per quando un anime non è presente su AnimeClick, per arricchire gli ID incrociati, e per le immagini.

| Plugin | Perché lo uso | Dove trovarlo |
|--------|---------------|---------------|
| **Fanart.tv** | Poster, banner e sfondi in HD | Catalogo → Plugin (richiede API key gratuita) |
| **TheMovieDb** | Fallback metadati e immagini | Incluso in Jellyfin |
| **AniSearch** | ID incrociati e copertine anime | Catalogo → Plugin |
| **AniList** | ID incrociati extra | Catalogo → Plugin |
| **Screen Grabber** | Screenshot automatico episodi | Incluso in Jellyfin |
| **Embedded Image Extractor** | Estrae copertina dal file video | Incluso in Jellyfin |

> **Plugin rimossi (2026):** TheTVDB e AniDB sono stati rimossi permanentemente dal mio server. TheTVDB causava duplicati fantasma nella stagione Specials; AniDB sovrascriveva il numero stagione corretto (rilevato dalla struttura cartelle `Season 01/`) con `Season=0` (Specials), creando episodi fantasma.

---

### 📺 Libreria Anime TV

Vai su **Dashboard → Librerie → Anime TV → Gestisci libreria** e imposta:

#### Metadati Serie

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Titoli, trame, generi, cast, staff, rating in italiano |
| 🥈 | AniSearch | ID incrociati e fallback titoli |
| 🥉 | AniList | ID incrociati extra |
| 4 | TheMovieDb | Fallback finale |
| 5 | The Open Movie Database | Ultima risorsa |

#### Immagini Serie

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **Fanart** | Sfondi HD, logo, artwork ad alta risoluzione |
| 🥈 | AniSearch | Copertine specifiche anime |
| 🥉 | AniList | Fallback copertine |
| 4 | TheMovieDb | Ultima risorsa |
| 5 | **AnimeClick** | Fallback locandina IT (solo se nessuno sopra ha immagini) |

#### Metadati Stagioni

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Risolve ID AnimeClick corretto per stagioni su pagine separate |
| 🥈 | TheMovieDb | Informazioni stagione (anno, overview) |

> **Nota:** Metti AnimeClick come terzo, non primo. Le stagioni non hanno metadati testuali da AnimeClick (sinossi, cast) — il SeasonProvider serve solo a impostare l'ID per la risoluzione corretta degli episodi.

#### Immagini Stagioni

| Priorità | Provider |
|:--------:|----------|
| 🥇 | Fanart |
| 🥈 | AniList |
| 🥉 | AniSearch |
| 4 | TheMovieDb |

#### Metadati Episodi

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Titoli italiani degli episodi + sinossi IT (opzionale, via TMDB+Ollama) |
| 🥈 | TheMovieDb | Fallback titoli/sinossi inglesi |
| 🥉 | The Open Movie Database | Ultima risorsa |

#### Immagini Episodi

| Priorità | Provider |
|:--------:|----------|
| 🥇 | TheMovieDb |
| 🥈 | The Open Movie Database |
| 🥉 | Screen Grabber |
| 4 | Embedded Image Extractor |

---

### 🎬 Libreria Anime Movie

Vai su **Dashboard → Librerie → Anime Movie → Gestisci libreria** e imposta:

#### Metadati Film

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AniList** | Studios, OfficialRating, generi, ID incrociati (eseguito per primo: riempie i campi che AnimeClick non copre) |
| 🥈 | TheMovieDb | Fallback Studios, generi, Overview |
| 🥉 | The Open Movie Database | Ultimo fallback per Studios e metadata di base |
| 4 | **AnimeClick** | Overlay di Titoli, Trama, Generi, Cast, Staff, Sigle in italiano (eseguito per ultimo: vince su TMDB/OMDb per i testi) |

#### Immagini Film

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **Fanart** | Poster HD, sfondi, logo |
| 🥈 | AniList | Copertine specifiche anime |
| 🥉 | TheMovieDb | Fallback |
| 4 | The Open Movie Database | Ultima risorsa |
| 5 | Embedded Image Extractor | Estrae copertina dal file video |
| 6 | Screen Grabber | Screenshot automatico dal video |
| 7 | **AnimeClick** | Fallback locandina IT (solo se nessuno sopra ha immagini) |

---

### 🧪 Risultato Finale

Con questa configurazione, quando esegui la scansione:

1. **AnimeClick** scrive tutto in italiano (titolo, trama, generi, cast, rating)
2. **Fanart / TMDB** scaricano poster, banner e sfondi in alta definizione
3. **AniList / AniSearch** forniscono ID incrociati e fallback
4. **AnimeClick SeasonProvider** risolve la pagina corretta per ogni stagione
5. **AnimeClick EpisodeProvider** assegna i titoli italiani a ogni episodio

Nessun conflitto, nessuna copertina a bassa risoluzione, tutto in italiano dove disponibile.

## 🔄 Risoluzione Problemi / ID Mancanti

Se usi l'opzione "Identifica" in Jellyfin e clicchi manualmente su un risultato "AnimeClick", Jellyfin **cancella** gli ID degli altri database americani per sicurezza. Se lo fai, *Fanart / TMDB smetteranno di trovare immagini per quell'anime* perché hanno perso il bersaglio!
Se ti succede: vai su Modifica Metadati e ri-incolla a mano l'ID TheMovieDb in fondo alla pagina (lo trovi cercando l'anime su themoviedb.org). Se invece lasci fare la "Scansione Libreria" in automatico a Jellyfin, lui conserverà entrambi gli ID perfettamente!

## 🌐 Sinossi episodi in italiano (TMDB + Ollama Cloud)

AnimeClick pubblica **titoli** episodi in italiano ma **non le sinossi** per-episodio. AniList non espone `Episode.description`. Per riempire le sinossi degli episodi in italiano, il plugin può (feature opt-in):

1. recuperare l'**overview inglese** dell'episodio da **TMDB** (`tv/{id}/season/{s}/episode/{e}`);
2. **tradurla in italiano** via **Ollama Cloud**.

Il carico sul NAS è minimo: solo HTTPS in uscita verso TMDB e Ollama, **zero compute locale**. I risultati sono cacheati per-episodio (chiavi `tmdbId::`, `tmdbEp::`, `episodeSynopsisIT::`), quindi al secondo refresh di una stagione non si fa nessuna HTTP verso TMDB/Ollama.

### Step 1 — API key TMDB
1. Registrati su [themoviedb.org](https://www.themoviedb.org/) (gratis).
2. Vai su **Settings → API** ([themoviedb.org/settings/api](https://www.themoviedb.org/settings/api)) e richiedi una chiave di tipo **Personal / Developer** (gratis, uso non commerciale).
3. In Jellyfin: **Dashboard → Plugin → AnimeClick Metadata → TMDB API key** e incolla la chiave.

### Step 2 — API key Ollama Cloud
1. Registrati su [ollama.com](https://ollama.com/) (gratis).
2. Crea una API key su [ollama.com/settings/keys](https://ollama.com/settings/keys).
3. In Jellyfin: **Dashboard → Plugin → AnimeClick Metadata → Ollama Cloud API key** e incolla la chiave. L'endpoint default è `https://ollama.com/api/chat` (auth `Authorization: Bearer <key>`).

### Step 3 — Scegli il modello
Dal dropdown **Modello cloud per la traduzione** scegli un modello Ollama Cloud. Consigliati per traduzione anime EN→IT:

- **`gemma4:cloud`** (default) — solido multilingua, buon equilibrio qualità/latenza.
- **`minimax-m2.1:cloud`** — descrizione Ollama: "exceptional multilingual capabilities".
- **`qwen3.5:cloud`**, **`gpt-oss:cloud`** — alternative generaliste affidabili.

Puoi anche selezionare **Personalizzato…** e inserire qualsiasi tag `nome:cloud` del catalogo ([ollama.com/search?c=cloud](https://ollama.com/search?c=cloud)).

> **⚠️ Free vs Premium — onestà importante.** I modelli cloud Ollama **non sono gated per singolo modello**: la distinzione è per **tier di abbonamento** ([ollama.com/pricing](https://ollama.com/pricing)). Ollama **non pubblica** una lista per-modello free/premium, quindi il dropdown li raggruppa per "consigliati per la traduzione" / "altri" anziché per tier. In pratica:
> - **Free ($0)**: concurrency 1, limiti di utilizzo sessione (reset 5h) + settimanale (reset 7gg) — "light usage". Sufficenti per traduzioni cached brevi come le sinossi episodi (testi corti, una-tantum per episodio grazie alla cache).
> - **Pro ($20/mese)**: 3 cloud models concorrenti, ~50× più utilizzo, accesso a modelli più grandi.
> - **Max ($100/mese)**: 10 concorrenti, 5× più uso del Pro.
>
> Per il primo scan di una stagione da 25 episodi: ~25 chiamate TMDB + ~25 chiamate Ollama (con `Delay richieste` a 1s → ~50s una-tantum). Sul Free va benissimo perché la cache evita di rifarlo.

### Come funziona (e quando non funziona)
- Il plugin risolve l'ID TMDB della serie cercando per **titolo originale (romaji) + anno** (fallback titolo italiano), cached per serie.
- Per ogni episodio fetcha l'overview EN da TMDB (cached per episodio) e la traduce via Ollama (cached per episodio+modello).
- Se la traduzione ha successo → `result.Item.Overview` viene settato in italiano. La sinossi IT viene popolata **anche quando il titolo episodio è generico** ("Episodio 3"): la sinossi ha valore indipendente dal titolo.
- Su qualsiasi fallimento (TMDB 404, no key, timeout Ollama) → nessuna eccezione, l'Overview è lasciato agli altri provider (AniList/TMDB nativi Jellyfin) in inglese (fill-gaps).

### Caveat
- Il mapping stagione/episodio anime↔TMDB a volte non coincide (specialmente per anime long-running con numbering non standard). Se TMDB risponde 404 per `season/{s}/episode/{e}`, quell'episodio resta senza sinossi IT (gli altri provider riempiono in EN).
- Serve che TMDB abbia l'anime nel proprio DB con overview episodi in inglese. Non tutti gli anime le hanno complete.
- Senza `Traduci le sinossi degli episodi` attivo (default), le sinossi episodi restano in inglese dai provider nativi Jellyfin (fill-gaps) — il plugin scrive solo i titoli IT.

## 🔧 Build da Sorgente

```bash
git clone https://github.com/iCosiSenpai/jellyfin-plugin-animeclick.git
cd jellyfin-plugin-animeclick
dotnet restore
dotnet publish -c Release -o pub
```

L'output sarà in `pub/`.

## 📋 Requisiti

- Jellyfin **10.11+**
- .NET **9.0** runtime

## 📝 Changelog

### v0.2.8.0 (Merge fill-gaps + immagini fallback + sinossi episodi IT via TMDB/Ollama)
- 🧠 **Merge "Italian-wins + fill-gaps"**: nuovo toggle `OverwriteNonItalianFields` (default **false**). AnimeClick emette solo i campi localizzati (titolo IT, sinossi IT, generi IT, tag, cast) e lascia i campi language-neutral (titolo originale, studio, rating, data, classificazione, stato) ai provider a monte (AniList/TMDB/OMDb). Empty-guard su `Name`/`Overview`/`OriginalTitle` per non svuotare mai campi già popolati. Quando il toggle è attivo, si recupera il comportamento precedente (AnimeClick sovrascrive tutto).
- 🖼️ **Image provider fallback per Series/Movie**: nuovo `AnimeClickAnimeImageProvider` (`IRemoteImageProvider`, `Order=100`) che fornisce la locandina italiana di AnimeClick (campo `ImageUrl` già estratto dal parser ma prima inutilizzato) come **ultima risorsa** — AniList/Fanart/TMDB vincono se hanno immagini, AnimeClick riempie solo i buchi. Toggle `EnableAnimeClickImages` (default on). Non blocca alcun provider (nessuna `SetImage`).
- 🌐 **Sinossi episodi in italiano (opzionale) via TMDB + Ollama Cloud**: AnimeClick non pubblica sinossi per-episodio e AniList non espone `Episode.description`, quindi il plugin recupera l'overview EN dell'episodio da TMDB e la traduce in IT via Ollama Cloud. Opt-in via `EnableEpisodeSynopsisTranslation` (default off) + `TmdbApiKey` + `OllamaCloudApiKey`. Nuovi servizi `AnimeClickTmdbClient` (search/tv + season/episode) e `AnimeClickOllamaTranslator` (POST `/api/chat` con Bearer auth). Cache per-episodio (`tmdbId::`, `tmdbEp::`, `episodeSynopsisIT::`). La sinossi IT viene popolata anche per titoli episodio generici. Su qualsiasi fallimento → fill-gaps (altri provider in EN).
- ⚙️ **Config page**: nuove sezioni "Sinossi episodi in italiano (TMDB + Ollama Cloud)" e toggle `OverwriteNonItalianFields` / `EnableAnimeClickImages`. Dropdown modello Ollama con modelli consigliati + personalizzato (gating per tier Free/Pro/Max spiegato nel README).
- 🧪 **Nuovi unit test** (no-network): TMDB URL building + response parsing (`ParseFirstTvId`, `ParseEpisodeOverview`), Ollama `StripHtml` + `BuildRequestBody` + `ParseTranslatedContent`.
- 📝 **README**: riconciliata la sezione "Filosofia del Plugin" con il nuovo modello (Italian-wins + fill-gaps + fallback locandina + traduzione opzionale); nuovo tutorial TMDB+Ollama; tabelle config e Librerie Supportate aggiornate; bump riferimenti versione a 0.2.8.0.

### v0.2.7.0 (AniList ID risolto durante ogni scan, non solo nell'Identify manuale)
- 🔧 **Nuovo servizio `AnimeClickAniListResolver`**: la risoluzione dell'AniList ID per titolo (via AniList GraphQL) è stata estratta da `AnimeClickIdentifyController` in un servizio riutilizzabile, registrato in DI. Niente più logica duplicata (DRY).
- 🖼️ **Fix immagini/metadati mancanti su scan automatico**: `AnimeClickSeriesProvider` e `AnimeClickMovieProvider` ora chiamano il resolver in `GetMetadata` e scrivono `ProviderIds["AniList"]` sull'item quando AnimeClick non fornisce ID incrociati (le pagine AnimeClick **non** espongono link MAL/AniList/AniDB/TMDB). Prima questo avveniva SOLO premendo il pulsante "Identify & Refresh"; ora succede a ogni scansione della libreria, così l'ImageFetcher AniList trova le copertine in automatico e i provider a valle hanno un ID stabile per i refresh successivi. La ricerca usa il titolo originale (romaji) quando disponibile, con fallback al titolo italiano.
- 🧩 **Nota Fanart per i Film**: Fanart.tv per i film è indicizzato per TMDB ID, non AniList. Per le copertine dei film abilita anche **TheMovieDb** come metadata fetcher nella libreria "Anime Movie" (Dashboard → Librerie → Anime Movie → downloader metadati Film) così Jellyfin risolve un TMDB ID a monte.
- ♻️ **Refactor controller**: `AnimeClickIdentifyController.EnsureAniListIdAsync` delega al servizio condiviso; rimossi gli helper privati `EscapeGraphQL`/`ParseAniListIdFromSearch` (ora `internal static` nel servizio, coperti da unit test).
- ✅ **Nuovi unit test**: `ParseAniListIdFromSearch` (match, `Media:null`, payload di errore, whitespace) e `EscapeGraphQL` (quote/backslash).

### v0.2.6.0 (Fix response undefined + force image download)
- 🐛 **Fix response body showing `undefined` for every field**: ASP.NET Core serializes C# PascalCase DTOs in camelCase (`itemId`, `name`, `animeClickId`, …) but the v0.2.5.0 client JavaScript was reading `resp.ItemId`, `resp.Name`, … (PascalCase) — every field came back `undefined` even though the backend returned 200 OK with the correct data. v0.2.6.0 client reads both casings (`resp.itemId || resp.ItemId`) and pretty-prints the `DownloadedImages` list (Type:Provider:Url) so the operator can see exactly what was downloaded.
- 🐛 **Fix `no remote images available` for all 5 image types**: v0.2.5.0 created `new RemoteImageQuery(providerName: string.Empty)` which filters by provider with empty name (= none), so `GetAvailableRemoteImages` returned `[]` for Primary/Backdrop/Logo/Art/Thumb. v0.2.6.0 uses `providerName: null` (= all providers) and `IncludeDisabledProviders = true`, so every enabled ImageFetcher is now queried.
- 🔧 **New `EnsureAniListIdAsync`**: queries AniList GraphQL by title (`{ Media(search: "Hanasaku Iroha Home Sweet Home", type: ANIME) { id } }`) and stores the resulting AniList ID on the item before downloading images. Without an AniList ID the AniList ImageFetcher can't do its lookup. AnimeClick provides the Italian/Anime metadata but no TMDB/IMDB/AniList IDs, so this fallback is what makes the rest of the image chain work for items that the user only identified via AnimeClick.
- 📋 **Minimal JSON parsing** for the AniList response: `ParseAniListIdFromSearch` (no System.Text.Json dependency) extracts the `id` field by string matching, keeping the plugin's dependency footprint small.
- 🛡️ **No new dependencies**: IHttpClientFactory is injected via the standard ASP.NET Core DI (no new `using`).

### v0.2.5.0 (Force-download images from Fanart/AniList/TMDB)
- 🔧 **Bypass Jellyfin 10.11.x broken image-refresh path**: `MetadataRefreshOptions.ReplaceAllImages` doesn't exist in Jellyfin 10.11.11, so even with `ReplaceAllMetadata=true` Jellyfin does NOT reliably re-download remote images. v0.2.5.0 fixes this by calling `IProviderManager.GetAvailableRemoteImages` + `IProviderManager.SaveImage` directly inside the `IdentifyAndRefresh` flow, asking each enabled ImageFetcher (Fanart, AniList, TheMovieDb, OMDb) for the best image of each type and saving it explicitly.
- 🖼️ **Forced download of 5 image types**: 1 Primary (poster), up to 3 Backdrops, 1 Logo, 1 Art, 1 Thumb. Provider priority: **Fanart > AniList > TheMovieDb > OMDb > Embedded Image Extractor**. Ties broken by community rating desc, then by pixel area desc.
- 🔍 **New `DownloadedImages` field in `IdentifyAndRefreshResponse`**: returns a list of `Type:Provider:Url` entries for every image saved, so the operator can see exactly what Fanart / AniList / TMDB / OMDb returned and which won.
- ⚠️ **Behaviour change**: as of v0.2.5.0, `IdentifyAndRefresh` ALWAYS wipes existing remote images and downloads fresh ones from the enabled ImageFetchers, regardless of the `ReplaceAllImages` checkbox. The checkbox is now a legacy flag kept for backwards compatibility; use it to opt out by setting it to false.
- 🛠️ **IndexOfProvider helper** + **DownloadBestRemoteImagesAsync** in `AnimeClickIdentifyController.cs`: reads `RemoteImageQuery` (providerName=""), sorts by priority, saves via `_providerManager.SaveImage(item, url, type, imageIndex, ct)`.

### v0.2.4.0 (AnimeClick runs LAST so Studios come from AniList/TMDB)
- 🔧 **`IHasOrder` portato da 0 a 100** su `MovieProvider`, `SeriesProvider`, `EpisodeProvider`. AnimeClick ora gira DOPO AniList / TheMovieDb / OMDb / OMDb nella catena metadata, così i campi che AnimeClick non popola (Studios, OfficialRating, Genres vuoti, …) vengono riempiti PRIMA dai provider a monte, e poi AnimeClick overlay dei testi italiani (Name, Overview, Genres, Tags, Cast).
- 🔧 **Mapping difensivo**: `Genres` e `Studios` ora si applicano solo se la sorgente AnimeClick ha davvero qualcosa (`source.Genres.Count > 0` e `source.Studios.Count > 0`). Prima il plugin sovrascriveva sempre con array vuoto, perdendo i dati che AniList/TMDB avevano appena inserito.
- 🔍 **Log diagnostico `leaving fields for downstream providers`**: alla fine di `GetMetadata` il plugin logga quali field NON ha popolato (Genres, Studios, OfficialRating) così dal log si vede a colpo d'occhio se i provider a monte hanno materiale da usare.
- 🛡️ **No regressioni**: i campi già popolati da AnimeClick (Name italiano, Overview italiano, CommunityRating AnimeClick, Cast, Sigle) continuano a essere applicati per ultimi e a vincere su TMDB/AniList.

### v0.2.3.0 (Fix Identify & Refresh non riscarica le immagini)
- 🐛 **Fix "Identify & Refresh" non sostituisce le immagini esistenti**: l'endpoint `POST /Plugins/AnimeClick/IdentifyAndRefresh` adesso accetta il flag opzionale `ReplaceAllImages` (default `false`). Quando `true`, prima del refresh il plugin rimuove tutte le immagini remote (poster, backdrop, logo, art, banner, thumb, disc, box) lasciando intatte solo le immagini locali (folder.jpg, poster.jpg, backdrop.jpg nella cartella del film). Il refresh successivo fa riscaricare le copertine dagli ImageFetchers configurati (Fanart, AniList, TMDB, OMDb, Embedded Image Extractor, Screen Grabber).
- 🛠️ **Pulsante "Sostituisci anche le immagini"** nella sezione Diagnostica della configPage: checkbox che quando spuntata passa `ReplaceAllImages=true` all'endpoint. Di default deselezionato per non sovrascrivere artwork curato dall'utente.
- 🔍 **Endpoint diagnostico `GET /Plugins/AnimeClick/AvailableRemoteImages?itemId=...&type=Primary`**: elenca le immagini remote disponibili per un item da ogni ImageFetcher abilitato (utile per capire cosa può scaricare Fanart/AniList/TMDB).
- 📋 **Wipe remote images intelligente**: enumera i tipi supportati (Primary, Backdrop, Logo, Art, Banner, Thumb, Disc, Box, BoxRear), preserva `IsLocalFile=true`, e itera dall'ULTIMO indice al primo per evitare shift degli indici durante la cancellazione.
- 📝 README: aggiornata la sezione Identify → Save → Refresh per spiegare il flag `ReplaceAllImages` e quando usarlo.

### v0.2.2.0 (Fix Identify → Save → Refresh)
- 🐛 **Fix Identify & Save non popola i metadati**: Jellyfin 10.11.x salva l'ID provider con Identify → Save ma non triggera automaticamente un metadata refresh. Aggiunto endpoint custom `POST /Plugins/AnimeClick/IdentifyAndRefresh` che salva l'ID e triggera immediatamente un full refresh (`MetadataRefreshMode = FullRefresh`, `ReplaceAllMetadata = true`).
- 🛠️ **Pulsante Identify & Refresh** nella pagina di configurazione del plugin (Diagnostica) con input per Item ID Jellyfin + AnimeClick ID. Risolve in un click il problema "spinner brevissimo, metadata non aggiornati".
- 🩺 **Pulsante Stato Identify** per verificare se un item ha già l'ID AnimeClick impostato (utile per diagnosi su Hanasaku Iroha: Home Sweet Home, et similia).
- 🔍 **Log diagnostico esplicito** all'ingresso di `MovieProvider.GetMetadata`, `SeriesProvider.GetMetadata`, `EpisodeProvider.GetMetadata`, `SeasonProvider.GetMetadata` con `Name`, `ProviderId`, `Year`, `Path`. Permette di verificare se il provider viene effettivamente chiamato dopo Identify/Refresh.
- 📝 Documentazione del flusso Identify → Save → Refresh e della limitazione Jellyfin 10.11.x in README.

### v0.2.1.0 (Fix Special Fantasma)
- 🐛 **Fix Special/OVA/OAD su stagioni regolari**: il parser rileva episodi Special dal titolo e forza `SeasonNumber=0`
- 🐛 **Blocco fallback assoluto**: l'EpisodeMatcher non applica più il fallback absolute per titoli Special su richieste di stagione regolare
- 🛡️ **Safety net EpisodeProvider**: controllo su `ParentIndexNumber > 0` per prevenire assegnazioni errate
- 📝 **Nota importante**: TheTVDB e AniDB sono stati rimossi dal mio server personale (causavano duplicati fantasma nella stagione Specials). Vedi sezione "La Mia Configurazione" aggiornata.

### v0.2.0.0 (Diagnostica e matching episodi universale)
- **Matching episodi universale**: normalizza numeri assoluti e progressivi di stagione AnimeClick, evitando fallback errati agli episodi S1 quando esiste un gruppo stagione
- **Diagnostica admin**: aggiunti endpoint e UI per lookup preview, preview episodi normalizzati e pulizia cache mirata
- **Ricerca piu robusta**: scoring per titolo esatto, anno e tipo, con penalita per Movie/Special quando Jellyfin cerca una serie
- **Cache versionata**: chiavi episodi e season-map aggiornate con pulizia mirata dalla configurazione
- **OP/ED best-effort**: trailer/PV-only viene segnalato come diagnostica, senza fingere discovery riuscita di sigle

### v0.1.2.0 (Fix Multi-Stagione)
- 🔧 **Parsing stagioni**: riconoscimento formato `S{N} Ep. {M}` su pagine episodi multi-stagione
- 🔗 **Stagioni su pagine separate**: risoluzione automatica via relazioni AnimeClick per catene di sequel
- 🚫 **Filtro spin-off**: esclusione automatica di Alternative, Gaiden, Spin-off, Bangai-hen
- 📅 **SeasonProvider**: nuovo provider per impostare l'ID AnimeClick corretto sull'entità Season
- 🐛 **Fix sidebar relazioni**: parsing `h5.media-heading` e `<span>` description (prima trovava solo la prima relazione)
- 📦 **Bundle snellito**: rimosse DLL Microsoft.Extensions conflittuali (Jellyfin le fornisce già)

### v0.1.1.0 (Allineamento)
- 🔄 Allineamento versione con catalogo KometaThemes

### v0.1.0.0 (Initial Release)
- 🚀 Prima release stabile con supporto Jellyfin 10.11+
- ⚙️ **Rate Limiter Centralizzato**: `AnimeClickClient` con semafori asincroni su tutte le richieste
- 📸 **Focus Doppiatori**: download foto cast da AnimeClick, estetica delegata a Fanart.tv/TMDB
- 🚀 **Zero-Allocation**: `[GeneratedRegex]` nativo .NET 9.0
- 🛡️ **Resilienza**: cache potenziata, cancellation token, timeout

## 🙏 Attribuzione

<div align="center">
  <a href="https://www.animeclick.it/">
    <img src="https://www.animeclick.it/bundles/accommon/images/ac-logoB.jpg" alt="AnimeClick.it" width="400" />
  </a>
</div>

I metadati sono forniti da **[AnimeClick.it](https://www.animeclick.it/)**, gestito dall'associazione culturale no-profit [Associazione NewType Media](http://www.antme.it/).

Questo plugin non è affiliato con AnimeClick. Lo scraping è stato autorizzato dallo staff di AnimeClick per uso non commerciale.

## 📄 Licenza

[GPL-3.0-or-later](LICENSE)
