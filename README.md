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

### Metadati
- **Titoli in italiano** (con opzione per titolo originale giapponese)
- **Trama/sinossi** in italiano
- **Generi** in italiano (Commedia, Fantascienza, Scolastico, ecc.)
- **Tag** (Shounen, Seinen, Mecha, Isekai, ecc.)
- **Anno di produzione** e **data premiere**
- **Valutazione community** AnimeClick (scala 1-10)
- **Stato serie** (completato → Ended, in corso → Continuing)
- **Studi di animazione**
- **Content rating** (se disponibile)

### Cast & Staff
- **Doppiatori giapponesi** (seiyuu) con nome del personaggio
- **Doppiatori italiani** con nome del personaggio
- **Registi**
- **Autori** (soggetto originale, sceneggiatura, series composition)
- **Compositori** (colonne sonore)

### Librerie Supportate
| Tipo | Metadati Testuali e Cast | Locandine e Art |
|------|----------|----------|
| 📺 Serie TV | ✅ | ❌ (Usa TMDB/Fanart) |
| 🎬 Film | ✅ | ❌ (Usa TMDB/Fanart) |
| 📅 Stagioni | ❌ | ❌ (Usa TMDB/Fanart) |
| 📝 Episodi | ✅ (Titoli Ita) | ❌ |

### Funzionalità Tecniche
- **Cache locale** con TTL configurabile (default: 48h)
- **Rate limiting** integrato (default: 1 richiesta/secondo)
- **Identificazione manuale** tramite ID AnimeClick (formato: `72/naruto` dall'URL)
- **Link esterno** diretto alla pagina AnimeClick nella sidebar
- **Pagina di configurazione** completa nella dashboard Jellyfin

## 📦 Installazione

### Da Repository Plugin (consigliato)

1. In Jellyfin, vai su **Dashboard → Plugin → Repositories**
2. Aggiungi un nuovo repository con URL:
   ```
   https://raw.githubusercontent.com/iCosiSenpai/jellyfin-plugin-animeclick/main/manifest.json
   ```
3. Vai su **Catalogo**, cerca "AnimeClick" e installa
4. Riavvia Jellyfin

### Installazione Manuale

1. Scarica l'ultima release dalla [pagina Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
2. Estrai lo zip nella cartella plugin di Jellyfin:
   - **Linux**: `~/.local/share/jellyfin/plugins/AnimeClick Metadata/`
   - **Windows**: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata\`
   - **Docker**: `/config/plugins/AnimeClick Metadata/`
3. Riavvia Jellyfin

## ⚙️ Configurazione

Dopo l'installazione, vai su **Dashboard → Plugin → AnimeClick Metadata** per configurare:

### Metadati
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Preferisci titolo italiano | ✅ | Usa il titolo italiano come nome |
| Importa trama | ✅ | Importa la sinossi in italiano |
| Importa generi | ✅ | Importa i generi (Commedia, Fantascienza, ecc.) |
| Importa studi | ✅ | Importa gli studi di animazione |
| Importa valutazione | ✅ | Importa il rating community |
| Importa cast e staff | ✅ | Doppiatori, registi, autori, compositori |
| Importa tag | ✅ | Tag come Shounen, Seinen, Mecha |

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
| User-Agent | `AnimeClick-Jellyfin-Plugin/0.8` | Identificativo per le richieste |

## 🔍 Identificazione Manuale

Per identificare manualmente un anime:
1. Cerca l'anime su [AnimeClick.it](https://www.animeclick.it/)
2. Copia l'ID dall'URL (es. per `animeclick.it/anime/72/naruto` → `72/naruto` oppure anche solo `72`)
3. In Jellyfin, clicca sull'anime → **Identifica** → inserisci l'ID nel campo "AnimeClick"

> **Nota:** Puoi anche inserire solo l'ID numerico (es. `72`) e il plugin lo troverà automaticamente.

## 🧠 La Filosofia del Plugin (Configurazione Ideale 2026)

AnimeClick è in assoluto l'enciclopedia più completa per quanto riguarda i **Testi** (Sinossi, Titoli) e i **Doppiatori Italiani** nel mondo degli Anime.
Tuttavia, *non* è un database nato per fornire Locandine, Fanart o Sfondi ad alta risoluzione. Le copertine di AnimeClick sono spesso a bassa risoluzione, contengono loghi o presentano difetti di aspect-ratio.

**Per questo motivo, a partire dalla versione v0.8.0, questo plugin SCARICA ESCLUSIVAMENTE I TESTI, I METADATI E LE FOTO DEI DOPPIATORI.**
È stato appositamente *rimosso* lo scaricatore di locandine per le serie e le stagioni.

Questa castrazione è **volontaria e mirata**, perché nel 2026 l'eccellenza assoluta si ottiene delegando l'estetica a colossi nati per quello.

### 📚 La Configurazione Definitiva (Best Practices)

Per ottenere il risultato estetico e testuale perfetto su Jellyfin, configura la tua libreria Anime nel seguente modo. Ti serviranno plugin di terze parti:
- **TheMovieDb** (Incluso su Jellyfin)
- **Fanart.tv** (Aggiungilo dai Plugin, crea account gratuito per API key)
- **AniSearch / AniDB** (Opzionali per extra anime id)

#### 1. Scraper Metadati (Testi)
Imposta gli scaricatori nel seguente ordine:
🥇 `AnimeClick` (Attivalo e spostalo in cima. Lui farà il 90% del lavoro in italiano: tramutando la scheda in italiano e scaricando tutti i doppiatori).
🥈 `TheMovieDb` o `TheTVDB` (Lasciali come secondari: Jellyfin userà loro per colmare i buchi o prendere i trailer).

#### 2. Scraper Stagioni
🥇 `TheMovieDb` o `TheTVDB`
❌ `AnimeClick` (Da tenere ASSOLUTAMENTE DISATTIVATO. AnimeClick tratta le stagioni come serie separate, incasinando le cartelle).

#### 3. Scaricatori di Immagini (Locandine, Sfondi, Banner)
🥇 `Fanart.tv` (Testo pulito, alta definizione).
🥈 `TheMovieDb`
❌ `AnimeClick` (Opzione non più presente: non sporcherà più le vostre bellissime cover!)

In questo modo, quando esegui la scansione, **AnimeClick** metterà il testo magico in Italiano, e **TMDB/Fanart** metteranno i poster brillanti in 4K.

## 🔄 Risoluzione Problemi / ID Mancanti
Se usi l'opzione "Identifica" azzurra in Jellyfin e clicchi manualmente su un risultato "AnimeClick", Jellyfin **cancella** gli ID degli altri database americani per sicurezza. Se lo fai, *Fanart / TMDB smetteranno di trovare immagini per quell'anime* perché hanno perso il bersaglio!
Se ti succede: vai su Modifica Metadati e ri-incolla a mano l'ID TheMovieDb in fondo alla pagina (lo trovi cercando l'anime su themoviedb.org). Se invece lasci fare la "Scansione Libreria" in automatico a Jellyfin, lui conserverà entrambi gli ID perfettamente!

## 🔧 Build da Sorgente

```bash
git clone https://github.com/iCosiSenpai/jellyfin-plugin-animeclick.git
cd jellyfin-plugin-animeclick
dotnet restore
dotnet build -c Release
```

L'output sarà in `bin/Release/net9.0/`.

## 📋 Requisiti

- Jellyfin **10.11+**
- .NET **9.0** runtime

## 📝 Changelog

### v0.8.0 (Ottimizzazione e Stabilità)
- 🚀 **Initial Release**: Lancio ufficiale della nuova architettura stabile con supporto per Jellyfin 10.11+.
- ⚙️ **Rate Limiter Centralizzato**: `AnimeClickClient` con semafori asincroni estesi a tutte le chiamate (incluse le foto cast) per impedire comportamenti di tipo DDoS ed evitare blocchi dal server remoto.
- 📸 **Focus Doppiatori ed Estrazione Cast**: Il downloader generico per cover di Serie è stato dismesso: scarichiamo i testi magici e le foto cast da AnimeClick, delegando l'estetica (poster) in esclusiva ai plugin nativi come Fanart.tv/TMDB.
- 🚀 **Zero-Allocation**: Passaggio strutturale a `[GeneratedRegex]` nativo (.NET 9.0) azzerando l'impatto sulla CPU durante l'estrazione intensiva dei dati HTML.
- 🛡️ **Resilienza**: Sistema di cache potenziato, gestione avanzata dei cancellation token e dei timeout.

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
