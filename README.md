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
| 📺 Serie TV | ✅ | ❌ (Usa TMDB/Fanart) |
| 🎬 Film | ✅ | ❌ (Usa TMDB/Fanart) |
| 📅 Stagioni | ✅ (ID Provider) | ❌ (Usa TMDB/Fanart) |
| 📝 Episodi | ✅ (Titoli Ita) | ❌ |

### Funzionalità Tecniche
- **Cache locale** con TTL configurabile (default: 48h)
- **Rate limiting** integrato (default: 1 richiesta/secondo)
- **Identificazione manuale** tramite ID AnimeClick (formato: `72/naruto` dall'URL)
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
   - **Linux**: `~/.local/share/jellyfin/plugins/AnimeClick Metadata_0.2.0.0/`
   - **Docker**: `/config/plugins/AnimeClick Metadata_0.2.0.0/`
   - **Windows**: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata_0.2.0.0\`
3. Riavvia Jellyfin

> **💡 Altri miei plugin:** Nello stesso repository trovi anche [KometaThemes](https://github.com/iCosiSenpai/KometaTheme), che scarica automaticamente le sigle OP/ED degli anime da animethemes.moe.

## ⚙️ Configurazione

Dopo l'installazione, vai su **Dashboard → Plugin → AnimeClick Metadata** per configurare:

### Metadati
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Preferisci titolo italiano | ✅ | Usa il titolo italiano come nome della serie |
| Importa trama | ✅ | Importa la sinossi in italiano |
| Importa generi | ✅ | Importa i generi (Commedia, Fantascienza, ecc.) |
| Importa studi | ✅ | Importa gli studi di animazione |
| Importa valutazione | ✅ | Importa il rating community |
| Importa cast e staff | ✅ | Doppiatori, registi, autori, compositori |
| Importa tag | ✅ | Tag come Shounen, Seinen, Mecha |
| Importa titoli episodi | ✅ | Titoli italiani degli episodi da /episodi, con matching per progressivo di stagione |
| Crea collezioni automatiche | ❌ | Raggruppa sequel/prequel in BoxSet |
| Importa sigle OP/ED | ✅ | Aggiunge i nomi delle sigle come tag quando AnimeClick espone OP/ED strutturati |

### Cache & Performance
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Cache metadati (ore) | `48` | Durata cache dati scaricati |
| Cache negativa (ore) | `12` | Durata cache per risultati vuoti |
| Delay richieste (ms) | `1000` | Pausa tra richieste HTTP |

### Avanzate
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| URL base | `https://www.animeclick.it` | URL di AnimeClick |
| User-Agent | `AnimeClick-Jellyfin-Plugin/0.2.0.0` | Identificativo per le richieste |

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
Tuttavia, *non* è un database nato per fornire Locandine, Fanart o Sfondi ad alta risoluzione. Le copertine di AnimeClick sono spesso a bassa risoluzione, contengono loghi o presentano difetti di aspect-ratio.

**Per questo motivo, questo plugin SCARICA ESCLUSIVAMENTE I TESTI, I METADATI E LE FOTO DEI DOPPIATORI.**
È stato appositamente *rimosso* lo scaricatore di locandine per le serie e le stagioni.

Questa castrazione è **volontaria e mirata**: l'eccellenza si ottiene delegando l'estetica a colossi nati per quello.

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
| 🥇 | **AnimeClick** | Titoli italiani degli episodi |
| 🥈 | TheMovieDb | Fallback titoli inglesi |
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
