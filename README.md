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
| **TheTVDB** | Episodi, stagioni, trailer | Catalogo → Plugin |
| **AniSearch** | ID incrociati e copertine anime | Catalogo → Plugin |
| **AniDB** | ID incrociati e fallback | Catalogo → Plugin |
| **AniList** | ID incrociati extra | Catalogo → Plugin |
| **Screen Grabber** | Screenshot automatico episodi | Incluso in Jellyfin |
| **Embedded Image Extractor** | Estrae copertina dal file video | Incluso in Jellyfin |

---

### 📺 Libreria Anime TV

Vai su **Dashboard → Librerie → Anime TV → Gestisci libreria** e imposta:

#### Metadati Serie

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Titoli, trame, generi, cast, staff, rating in italiano |
| 🥈 | TheTVDB | Colma eventuali buchi, fornisce trailer |
| 🥉 | AniSearch | ID incrociati e fallback titoli |
| 4 | AniDB | ID incrociati e fallback |
| 5 | AniList | ID incrociati extra |
| 6 | Missing Episode Fetcher | Segnala episodi mancanti |
| 7 | TheMovieDb | Fallback finale |
| 8 | The Open Movie Database | Ultima risorsa |

#### Immagini Serie

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **TheTVDB** | Poster e banner puliti, aspect-ratio perfetto |
| 🥈 | **Fanart** | Sfondi HD, logo, artwork ad alta risoluzione |
| 🥉 | AniSearch | Copertine specifiche anime |
| 4 | AniDB | Fallback copertine |
| 5 | AniList | Fallback copertine |
| 6 | TheMovieDb | Ultima risorsa |

#### Metadati Stagioni

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | TheTVDB | Informazioni stagione (anno, overview) |
| 🥈 | AniDB | Fallback |
| 🥉 | **AnimeClick** | Risolve ID AnimeClick corretto per stagioni su pagine separate |

> **Nota:** Metti AnimeClick come terzo, non primo. Le stagioni non hanno metadati testuali da AnimeClick (sinossi, cast) — il SeasonProvider serve solo a impostare l'ID per la risoluzione corretta degli episodi.

#### Immagini Stagioni

| Priorità | Provider |
|:--------:|----------|
| 🥇 | TheTVDB |
| 🥈 | Fanart |
| 🥉 | AniDB |
| 4 | AniList |
| 5 | AniSearch |
| 6 | TheMovieDb |

#### Metadati Episodi

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Titoli italiani degli episodi |
| 🥈 | TheTVDB | Fallback titoli inglesi |
| 🥉 | AniDB | Fallback |
| 4 | TheMovieDb | Fallback |
| 5 | The Open Movie Database | Ultima risorsa |

#### Immagini Episodi

| Priorità | Provider |
|:--------:|----------|
| 🥇 | TheTVDB |
| 🥈 | TheMovieDb |
| 🥉 | The Open Movie Database |
| 4 | Screen Grabber |
| 5 | Embedded Image Extractor |

---

### 🎬 Libreria Anime Movie

Vai su **Dashboard → Librerie → Anime Movie → Gestisci libreria** e imposta:

#### Metadati Film

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **AnimeClick** | Titoli, trame, generi, cast, staff, rating in italiano |
| 🥈 | AniList | ID incrociati |
| 🥉 | AniDB | ID incrociati |
| 4 | TheTVDB | Fallback |
| 5 | TheMovieDb | Fallback |
| 6 | The Open Movie Database | Ultima risorsa |

#### Immagini Film

| Priorità | Provider | Ruolo |
|:--------:|----------|-------|
| 🥇 | **Fanart** | Poster HD, sfondi, logo |
| 🥈 | AniDB | Copertine specifiche anime |
| 🥉 | AniList | Copertine specifiche anime |
| 4 | TheTVDB | Fallback |
| 5 | TheMovieDb | Fallback |
| 6 | The Open Movie Database | Ultima risorsa |
| 7 | Embedded Image Extractor | Estrae copertina dal file video |
| 8 | Screen Grabber | Screenshot automatico dal video |

---

### 🧪 Risultato Finale

Con questa configurazione, quando esegui la scansione:

1. **AnimeClick** scrive tutto in italiano (titolo, trama, generi, cast, rating)
2. **TheTVDB / Fanart** scaricano poster, banner e sfondi in alta definizione
3. **AniDB / AniList / AniSearch** forniscono ID incrociati e fallback
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
