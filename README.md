<div align="center">
  <img src="https://raw.githubusercontent.com/iCosiSenpai/jellyfin-plugin-animeclick/main/assets/banner-alt.png" alt="AnimeClick Metadata Plugin" />

  # AnimeClick Metadata Plugin for Jellyfin

  [![GitHub Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square&color=blue)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11+-purple?style=flat-square)](https://jellyfin.org/)
  [![License](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square)](LICENSE)
</div>

Plugin per [Jellyfin](https://jellyfin.org/) che fornisce **metadati anime in italiano** da [AnimeClick.it](https://www.animeclick.it/), la principale community italiana dedicata all'animazione giapponese.

> Scraping etico del sito AnimeClick, autorizzato dallo staff. Tutte le richieste sono rate-limited e i dati vengono cacheati localmente.

## ✨ Funzionalità

**Metadati testuali**
- Titoli in italiano (con opzione per il titolo originale giapponese)
- Trama/sinossi in italiano
- Titoli episodi in italiano con matching multi-stagione
- Sinossi episodi in italiano (opzionale, vedi [sezione dedicata](#-sinossi-episodi-in-italiano))
- Generi, tag, anno di produzione, valutazione community, stato serie, studi di animazione, sigle OP/ED

**Cast & staff**
- Doppiatori giapponesi (seiyuu) e italiani con nome del personaggio
- Registi, autori, compositori

**Immagini**
- Locandina italiana di fallback per Serie e Film (priorità bassa: AniList/Fanart/TMDB vincono se hanno immagini; AnimeClick riempie solo i buchi)
- Foto doppiatori/staff per le entità Person

**Collezioni e stagioni**
- Rilevamento sequel/prequel/spin-off e raggruppamento in BoxSet (sperimentale)
- Stagioni sulla stessa pagina o su pagine separate risolte automaticamente tramite le relazioni AnimeClick
- Filtro spin-off (titoli con "Alternative", "Gaiden", "Spin-off", "Bangai-hen" esclusi dalla mappatura)

### Librerie supportate

| Tipo | Metadati | Immagini |
|------|----------|----------|
| 📺 Serie TV | ✅ | ✅ fallback |
| 🎬 Film | ✅ | ✅ fallback |
| 📅 Stagioni | ✅ (ID Provider) | ❌ usa TMDB/Fanart |
| 📝 Episodi | ✅ (titoli IT + sinossi IT opzionali) | ❌ |

## 📦 Installazione

### Da Repository Plugin (consigliato)

1. **Dashboard → Plugin → Repositories**, aggiungi:
   ```
   https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
   ```
2. **Catalogo** → cerca "AnimeClick Metadata" → installa → riavvia Jellyfin

### Installazione manuale

1. Scarica l'ultima release dalla [pagina Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
2. Estrai lo zip nella cartella plugin:
   - **Linux**: `~/.local/share/jellyfin/plugins/AnimeClick Metadata_0.2.11.0/`
   - **Docker**: `/config/plugins/AnimeClick Metadata_0.2.11.0/`
   - **Windows**: `%APPDATA%\jellyfin\plugins\AnimeClick Metadata_0.2.11.0\`
3. Riavvia Jellyfin

> **Altri miei plugin:** nello stesso repository trovi anche [KometaThemes](https://github.com/iCosiSenpai/KometaTheme), che scarica automaticamente le sigle OP/ED degli anime da animethemes.moe.

## ⚙️ Configurazione

La pagina di configurazione usa un layout **dashboard premium glassmorphism** con header sticky, tab bar iconata a pillole e card raggruppate. È organizzata in **4 schede**: Overview, Metadati, Sinossi e Strumenti.

### Overview
Scheda iniziale con:
- **Stato connessione provider**: 3 tile TMDB / Ollama Cloud / TheTVDB con badge ✓/✗ e dettagli espandibili.
- **Azioni rapide**: Identifica & Aggiorna, Svuota cache metadati.
- **Cosa è attivo**: riepilogo chips delle opzioni abilitate.
- **Ricerca impostazioni live**: filtra e evidenzia i match in tempo reale su tutte le schede.

### Metadati
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Preferisci titolo italiano | ✅ | Usa il titolo italiano come nome della serie |
| Sovrascrivi campi non-italiani | ❌ | Se attivo, sovrascrive anche titolo originale, studio, rating, data (campi che AniList/TMDB/OMDb gestiscono meglio). Spento = fill-gaps, lascia i buchi agli altri provider |
| Importa trama / generi / studi / valutazione / cast / tag / titoli episodi / sigle | ✅ | Campi localizzati in italiano da AnimeClick |
| Usa locandina AnimeClick come fallback | ✅ | Locandina IT come ultima risorsa (AniList/Fanart/TMDB vincono se hanno immagini) |
| Crea collezioni automatiche | ❌ | Raggruppa sequel/prequel in BoxSet (sperimentale) |

### Sinossi episodi in italiano

| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Abilita sinossi episodi in italiano | ❌ | Abilita il recupero. Fonte preferita: TVDB (IT diretto). Fallback: TMDB EN + Ollama IT |
| Usa TheTVDB (fonte preferita) | ❌ | Sinossi IT dirette, zero compute. Richiede API key TVDB |
| TheTVDB API key / Lingua | *(vuoto)* / `ita` | API key TVDB v4 + codice lingua 3-char |
| TMDB API key | *(vuoto)* | API key TMDB (fonte EN per il fallback) |
| Ollama Cloud API key / Endpoint / Modello | *(vuoto)* / `…/api/chat` / `gemma4:cloud` | Traduzione EN→IT per il fallback |
| Timeout traduzione (sec) | `30` | Timeout per una singola chiamata Ollama/TVDB |
| **Test TMDB / Ollama / TVDB** | — | Card "Stato connessione provider" con badge ✓/✗ + dettagli espandibili: validano le credenziali inserite senza salvare |

### Ricerca
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Risultati massimi per ricerca | `10` | Numero massimo di risultati per ricerca (1-25) |
| Filtra solo anime | ✅ | Esclude manga, novel e drama |

### Cache & Avanzate
| Opzione | Default | Descrizione |
|---------|---------|-------------|
| Cache metadati (ore) | `48` | Durata cache dati scaricati |
| Cache negativa (ore) | `12` | Durata cache per risultati vuoti |
| Delay richieste (ms) | `1000` | Pausa tra richieste HTTP |
| URL base | `https://www.animeclick.it` | URL di AnimeClick |
| User-Agent | `AnimeClick-Jellyfin-Plugin/0.2.11.0` | Identificativo per le richieste |

### Strumenti
La scheda **Strumenti** contiene:
- **Identifica & Aggiorna**: applica subito i metadati AnimeClick a un item dopo l'Identify (workaround bug Jellyfin 10.11.x). Output umanizzato ✓/✗ con riepilogo immagini scaricate.
- **Svuota cache metadati**: rimuove con un clic tutti i dati cacheati localmente per forzare un refresh fresco al prossimo scan.

## 🌐 Sinossi episodi in italiano

AnimeClick pubblica i **titoli** episodi in italiano ma **non le sinossi** per-episodio. Per riempirle, il plugin usa due fonti in cascata (feature opt-in):

1. **TheTVDB (fonte preferita)** — espone le sinossi degli episodi già tradotte in italiano. Quando la traduzione esiste, viene usata direttamente: zero chiamate Ollama, zero compute sul NAS.
2. **TMDB + Ollama Cloud (fallback)** — se TVDB non ha la traduzione, il plugin recupera l'overview inglese da TMDB e la traduce in italiano via Ollama Cloud.

Il carico sul NAS è minimo: solo HTTPS in uscita, zero compute locale. I risultati sono cacheati, quindi al secondo refresh di una stagione non si fa nessuna HTTP.

> **Vuoi il massimo con il minimo sforzo?** Abilita solo TheTVDB (Step 1): ottieni sinossi IT dirette senza toccare Ollama. Aggiungi TMDB + Ollama solo per coprire gli episodi che TVDB non ha tradotto.

### Setup

1. **TheTVDB**: registrati su [thetvdb.com](https://www.thetvdb.com/signup), crea una API key v4 dalla dashboard, incollala in **TheTVDB API key** e spunta **Usa TheTVDB come fonte preferita**. La lingua resta `ita` (codice 3-char; puoi mettere `eng`, `jpn`, ecc.).
2. **TMDB**: registrati su [themoviedb.org](https://www.themoviedb.org/), richiedi una chiave **Personal/Developer** su [Settings → API](https://www.themoviedb.org/settings/api), incollala in **TMDB API key**.
3. **Ollama Cloud**: registrati su [ollama.com](https://ollama.com/), crea una API key su [/settings/keys](https://ollama.com/settings/keys), incollala in **Ollama Cloud API key**. Endpoint default `https://ollama.com/api/chat` (auth Bearer).
4. **Modello**: dal dropdown scegli un modello Ollama Cloud. Consigliati per la traduzione EN→IT: `gemma4:cloud` (default, solido multilingua), `minimax-m2.1:cloud`, `qwen3.5:cloud`, `gpt-oss:cloud`. Puoi anche selezionare **Personalizzato…** e inserire qualsiasi tag `nome:cloud` del [catalogo](https://ollama.com/search?c=cloud).

> **⚠️ Free vs Pro**: i modelli cloud Ollama sono gated per **tier di abbonamento** ([ollama.com/pricing](https://ollama.com/pricing)), non per singolo modello. Il **Free** ($0) ha concurrency 1 e limiti di utilizzo sessione/settimanali — sufficienti per traduzioni cached brevi come le sinossi (una-tantum per episodio grazie alla cache). Il **Pro** ($20/mese) sblocca modelli più grandi e ~50× più utilizzo. Per il primo scan di una stagione da 25 episodi: con TVDB basta 1 chiamata paginata per tutta la serie; in fallback TMDB+Ollama sono ~50 chiamate (~50s una-tantum).

### Come funziona (e quando non funziona)
- **TVDB prima**: il plugin risolve l'ID TVDB cercando per titolo originale (romaji) + anno (fallback titolo italiano), cached per serie; poi fetcha la lista episodi tradotta (cached per serie+lingua, una chiamata paginata) e cerca la sinossi IT per stagione/episodio. Se esiste → `Overview` settato in IT.
- **Fallback TMDB + Ollama**: se TVDB non ha la traduzione (o è disabilitato), risolve l'ID TMDB, fetcha l'overview EN (cached per episodio) e la traduce via Ollama (cached per episodio+modello).
- Su qualsiasi fallimento (404, no key, timeout) → nessuna eccezione, l'Overview è lasciato agli altri provider in inglese (fill-gaps).

### Caveat
- Il mapping stagione/episodio anime↔TVDB/TMDB a volte non coincide (specialmente per anime long-running con numbering non standard). Se nessuna fonte ha la sinossi per `S/E`, quell'episodio resta senza sinossi IT.
- L'endpoint translation di TVDB a volte ritorna 404 anche quando la traduzione esiste sul sito (issue noto TVDB) — il plugin usa l'endpoint combined `/series/{id}/episodes/default/{lang}`, più affidabile.
- Serve che almeno una fonte abbia l'anime nel proprio DB.

## 🔍 Identificazione manuale

1. Cerca l'anime su [AnimeClick.it](https://www.animeclick.it/)
2. Copia l'ID completo dall'URL: per `animeclick.it/anime/72/naruto` → `72/naruto`
3. In Jellyfin, clicca sull'anime → **Identifica** → inserisci l'ID nel campo "AnimeClick"

> Puoi anche inserire solo l'ID numerico (es. `72`) e il plugin lo troverà tramite ricerca.

### ⚠️ Identify → Save → Refresh (Jellyfin 10.11.x)

Jellyfin 10.11.x ha un comportamento subdolo: quando identifichi un item via "Identify → Save", l'API salva l'ID provider sull'item ma **NON** triggera un metadata refresh. Lo spinner termina in un secondo e l'item resta con titolo/cast/descrizione vuoti o vecchi finché non fai manualmente "Refresh & replace" o non riavvii la libreria. Inoltre, se l'ID provider è già lo stesso che stai salvando, Jellyfin considera la modifica un no-op.

Questo plugin risolve il problema con il pulsante **Identifica & Aggiorna** (scheda **Strumenti**):

1. Apri **Dashboard → Plugin → AnimeClick Metadata → scheda Strumenti → card Identifica & Aggiorna**
2. Inserisci **Item ID Jellyfin** (l'UUID dell'item, nell'URL della pagina) e **AnimeClick ID** (formato `numero/slug`)
3. (Opzionale) Spunta **Aggiorna anche copertine e immagini** se vuoi che il plugin cancelli le immagini remote esistenti prima del refresh, così gli ImageFetchers (Fanart, AniList, TMDB, OMDb) riscaricano artwork migliore. Le immagini locali (`folder.jpg`, `poster.jpg`, `backdrop.jpg`) sono sempre preservate.
4. Clicca **Identifica & Aggiorna**: un box risultato ✓/✗ mostra l'esito e l'elenco delle immagini scaricate. Attendi 3-10 secondi: titolo italiano, trama, generi, cast e staff compaiono sulla pagina dell'item.

> **Alternativa manuale**: clicca sul film → ⋮ → "Refresh & replace metadata". Il pulsante del plugin replica esattamente quel flusso senza navigare nel menu contestuale.

## 🧠 Setup consigliato

AnimeClick è l'enciclopedia più completa per i **testi** (sinossi, titoli) e i **doppiatori italiani**, ma non è un database di locandine ad alta risoluzione. Il plugin segue quindi un modello **"Italian-wins + fill-gaps"**: AnimeClick vince sui campi localizzati (titolo, trama, generi, tag, cast, titoli episodi) e lascia i campi neutri (titolo originale, studio, rating, data) ai provider a monte quando `Sovrascrivi campi non-italiani` è spento (default).

Per il miglior risultato, usa AnimeClick insieme ad altri provider come safety net:

| Plugin | Ruolo | Dove |
|--------|-------|------|
| **AnimeClick** | Titoli, trame, generi, cast, staff, rating in italiano | Questo plugin |
| **Fanart.tv** | Poster, banner e sfondi HD | Catalogo → Plugin (API key gratuita) |
| **TheMovieDb** | Fallback metadati, immagini, ID TMDB per i film | Incluso in Jellyfin |
| **AniSearch** | ID incrociati e copertine anime | Catalogo → Plugin |
| **AniList** | ID incrociati extra, copertine | Catalogo → Plugin |

**Ordine consigliato dei metadata fetcher** (Dashboard → Librerie → tua libreria anime → Gestisci):

- **Serie TV metadati**: AnimeClick → AniSearch → AniList → TheMovieDb → OMDb
- **Serie TV immagini**: Fanart → AniSearch → AniList → TheMovieDb → AnimeClick (fallback)
- **Stagioni metadati**: TheMovieDb → AnimeClick (AnimeClick solo per impostare l'ID; le stagioni non hanno testi AnimeClick)
- **Episodi metadati**: AnimeClick → TheMovieDb → OMDb
- **Film metadati**: AniList → TheMovieDb → OMDb → AnimeClick (per ultimo, vince sui testi IT)
- **Film immagini**: Fanart → AniList → TheMovieDb → AnimeClick (fallback)

> **TheTVDB e AniDB** sono stati rimossi dal mio server: TheTVDB causava duplicati fantasma nella stagione Specials; AniDB sovrascriveva il numero stagione corretto con `Season=0` creando episodi fantasma.

## 🔄 Risoluzione problemi

**ID mancanti dopo Identify manuale**: se usi "Identifica" e clicchi un risultato "AnimeClick", Jellyfin **cancella** gli ID degli altri database per sicurezza, quindi Fanart/TMDB smettono di trovare immagini. Soluzione: ri-incolla a mano l'ID TheMovieDb in Modifica Metadati (lo trovi cercando l'anime su themoviedb.org). Se invece lasci fare la "Scansione libreria" automatica, Jellyfin conserva entrambi gli ID.

**Sinossi episodi vuote**: verifica le credenziali con i pulsanti Test nella card "Stato connessione provider" (scheda Sinossi). Se il badge è rosso, controlla API key/endpoint. Ricorda che non tutti gli anime hanno sinossi episodi complete su TVDB/TMDB.

## 🔧 Build da sorgente

```bash
git clone https://github.com/iCosiSenpai/jellyfin-plugin-animeclick.git
cd jellyfin-plugin-animeclick
dotnet restore
dotnet publish -c Release -o pub
```

L'output sarà in `pub/`.

**Requisiti**: Jellyfin **10.11+**, .NET **9.0** runtime.

## 🙏 Attribution / Fonti dei dati

Questo plugin integra metadati e immagini da diverse fonti pubbliche. Si ringraziano:

| Fonte | Ruolo | Sito |
|---|---|---|
| **[AnimeClick.it](https://www.animeclick.it/)** | Titoli, trame, generi, cast, staff, sigle OP/ED (fonte primaria italiana) | https://www.animeclick.it/ |
| **[TheTVDB](https://thetvdb.com/)** | Sinossi episodi in italiano (fonte preferita, zero traduzione Ollama) | https://thetvdb.com/ |
| **[TheMovieDB (TMDB)](https://www.themoviedb.org/)** | Fallback metadati e immagini, overview episodi EN (poi tradotte via Ollama) | https://www.themoviedb.org/ |
| **[AniList](https://anilist.co/)** | ID incrociati, immagini di copertina | https://anilist.co/ |
| **[Fanart.tv](https://fanart.tv/)** | Poster, banner e sfondi HD | https://fanart.tv/ |

### TheTVDB — Attribution obbligatoria

L'uso gratuito dell'API di TheTVDB (revenue < $50k/anno) richiede di mostrare agli utenti finali la dicitura:

> **TheTVDB** — *Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).*

Questa attribution è mostrata anche nel pannello **Sinossi** della pagina di configurazione del plugin. Per contribuire direttamente o abbonarsi: [https://thetvdb.com/subscribe](https://thetvdb.com/subscribe).

## 📝 Changelog

### v0.3.7.0 (Fix multi-stagione quando AnimeClick elenca episodi in blocco unico)

- **BUG**: multi-cour anime tipo "The Asterisk War" (24 episodi su 2 stagioni) venivano matchati solo per S1 (`strategy=absolute`), mentre S2E1-E12 restituivano `strategy=none` → nessun titolo episodio italiano copiato su Jellyfin per la stagione 2.
- **Root cause**: AnimeClick elenca gli episodi come blocco continuo `Ep. 01` → `Ep. 24` *senza* prefisso `S1/S2 Ep.` per riga, e il parser assegnava `SeasonNumber = null` a tutti. Il matcher allora usava `absolute` per S1E1-E12 (AbsoluteNumber 1-12), ma bloccava il fallback absolute per stagioni > 1 → S2 mismatch.
- **Fix**: il parser ora legge la lista "Stagioni" dalla pagina principale di AnimeClick (es. "Autunno (2015) Primavera (2016)" → `SeasonsCount = 2`). È stato aggiunto un metodo `ParseEpisodesPage(html, baseUrl, int? seasonsCount)`; quando `seasonsCount > 1`, tutti gli episodi hanno `SeasonNumber == null` e il totale è divisibile per `seasonsCount`, l'episodio viene assegnato sinteticamente a stagione + ordinale (es. Asterisk War 24/2 = 12 → episodi 13-24 diventano S2 stagione-ordinale 1-12). Il matcher allora usa `seasonOrdinal` esattamente come per Dangers.
- **Sicurezza**: se gli episodi non sono divisibili per stagioni (es. 17 % 2 != 0) o `seasonsCount` non è noto, il parser rifiuta di inferire e mantiene il vecchio comportamento, evitando split errati su titoli single-cour.
- **Compatibilità**: nuovo overload opzionale. Il vecchio `ParseEpisodesPage(html, baseUrl)` continua a chiamare il nuovo con `seasonsCount=null` → nessun cambiamento per i title esistenti.
- **Test**: 2 nuovi test (`AsteriskContinuousBlockSeasonSplit`, `TestSeasonsCountRefusedOnUnevenSplit`). 18/18 PASS.

### v0.3.6.0 (Fix TVDB string id + accenti italiani + timeout identify)

- **BUG 1 — TVDB `tvdb_id` come stringa**: l'API TVDB v4 `/search` restituisce `tvdb_id` come stringa JSON (es. `"78857"`), non come numero. Il parser precedente scartava tutti i risultati → nessuna sinossi IT da TVDB. Ora accettiamo stringa e numero, e come ultimo fallback anche il campo `id` (record TVDB) quando `tvdb_id` è assente.
- **BUG 2 — Sinossi episodi IT da TVDB**: indirettamente risolto dal BUG 1. Il percorso TVDB diretto IT ora funzionante.
- **BUG 3 — Accenti italiani nella ricerca AnimeClick**: i titoli con accenti (es. "Caffè", "L'incorreggibile Ladro", "più") venivano inviati a AnimeClick come byte accented e il matcher di score trattava `Caffè` ≠ `Caffe` (token diverso). Aggiunto `RemoveDiacritics` (`NormalizationForm.FormD` + strip combining marks) applicato (a) alla query utente prima di tutti i 4 tentativi di ricerca AnimeClick, e (b) dentro `NormalizeForScore` nello scorer. Risultato: un anime con accenti nel nome Jellyfin ma senza accenti su AnimeClick ora matcha al primo tentativo con bonus +100.
- **BUG 4 — IdentifyAndRefresh timeout / spinner infinito**: il flow sequenziale (wipe immagini → AniList lookup → download immagini → RefreshMetadata) ora ha un hard cap di 30 secondi via `CancellationTokenSource.CreateLinkedTokenSource`. Su timeout restituisce `Success=false` con messaggio "Timeout dopo 30 secondi; riprova". La serie resta comunque identificata (l'AnimeClick ID è già persistito) — basta un secondo click per completare il refresh.
- **BUG 2b — Ollama `\uXXXX` escapes**: `ParseTranslatedContent` gestisce ora `\uXXXX` (4 hex) e le surrogate pairs `\UXXXXXXXX` non standard. Prima gli accenti italiani emessi come escape JSON (es. `\u00E8` = `è`) venivano decodificati come `"u00E8"` letterale.
- **TASK — Attribution TheTVDB** (richiesta per uso gratuito API): banner nel pannello *Sinossi* della config page e nuova sezione `Attribution / Fonti dei dati` nel README con link a thetvdb.com/subscribe.
- **Test**: 8 nuovi test — `tvdb_id` come stringa, fallback a `id` record, `\uXXXX` + surrogate pair Ollama, `RemoveDiacritics` diretto, e scorer accent-folding (Caffè vs Caffe → match esatto +100).

### v0.2.11.0 (Total redesign dashboard glassmorphism)
- 🎨 **Dashboard premium glassmorphism**: header sticky con chip versione e link rapidi, tab bar a pillole con icone Material, dashboard Overview con tile stato provider, quick actions e riepilogo opzioni attive. Card glassmorphism su Metadati, Sinossi e Strumenti; save dock fisso in basso a destra.
- 🔍 **Ricerca impostazioni live**: filtra label/descrizioni, evidenzia i match e nasconde le card non pertinenti.
- ⚡ **Quick actions in Overview**: Identifica & Aggiorna e Svuota cache metadati accessibili direttamente dalla dashboard.
- 💾 **Save dock flottante**: pulsante Salva sempre visibile in basso a destra.
- 🧹 **UI pulita**: descrizioni toggleabili, nessun `<pre>` raw visibile di default, esiti umanizzati.
- 🔧 Backend invariato; bump versione a 0.2.11.0.

### v0.2.10.0 (Redesign premium configPage)
- 🎨 **Pagina di configurazione rinnovata**: 4 schede in alto (Metadati / Sinossi episodi / Ricerca / Strumenti) con card raggruppate, icone e header premium (chip versione + link repo/issues). Pattern tab JS-driven riusato da KometaThemes, CSS inline.
- 🧹 **Rimossa la sezione Diagnostica developer-facing** (Test lookup, Preview episodi raw, Stato Identify e i `<pre>` raw). Rimpiazzata dal tab Strumenti.
- 🛠️ **Tab Strumenti**: card **Identifica & Aggiorna** (output umanizzato ✓/✗ + riepilogo immagini) e card **Svuota cache metadati** (singolo pulsante, nessun ID). Nuovo `AnimeClickCacheService.ClearAll()` + ramo "corpo vuoto → clear all" in `POST /Plugins/AnimeClick/ClearCache`.
- 📡 **Card "Stato connessione provider"**: i 3 test TMDB/Ollama/TVDB ora mostrano un badge ✓/✗ + dettagli espandibili, al posto del `<pre>` raw.
- 🐛 **Fix `{}` sui pulsanti di test**: `animeClickApi` setta `dataType:'json'` sui POST e `showProviderError` legge il body della `Response` raw.
- 📝 **README**: riscritto più conciso e pulito; bump versione a 0.2.10.0.

### v0.2.9.0 (TVDB sinossi IT dirette + pulsanti di test)
- 🌐 **TheTVDB come fonte preferita di sinossi episodi in italiano**: nuovo servizio `AnimeClickTvdbClient` (login TVDB v4 con token cached 24h, risoluzione ID serie, lista episodi tradotta in una chiamata paginata, cached per serie+lingua). Quando TVDB ha la sinossi IT la usa direttamente — zero chiamate Ollama, zero compute. Se TVDB non ha la traduzione, fallback TMDB EN + Ollama IT. Nuovi toggle `EnableTvdbSynopsis` + `TvdbApiKey` + `TvdbLanguage` (opt-in).
- 🧪 **Pulsanti di test con errore dettagliato**: Test TMDB / Test Ollama / Test TVDB validano le credenziali inserite (non serve salvare) e mostrano status HTTP, corpo risposta e messaggio eccezione. Nuovi endpoint `POST /Plugins/AnimeClick/TestTmdb|TestOllama|TestTvdb` + metodi `TestConnectionAsync` sui tre service.
- 🧪 **Nuovi unit test** (no-network): TVDB URL building + parsing. 12/12 test verdi.

### v0.2.8.0 (Merge fill-gaps + immagini fallback + sinossi episodi IT)
- 🧠 **Merge "Italian-wins + fill-gaps"**: nuovo toggle `OverwriteNonItalianFields` (default false). AnimeClick emette solo i campi localizzati e lascia i campi neutri ai provider a monte. Empty-guard su tutti i campi.
- 🖼️ **Image provider fallback per Series/Movie**: nuovo `AnimeClickAnimeImageProvider` (`Order=100`) che fornisce la locandina italiana come ultima risorsa. Toggle `EnableAnimeClickImages` (default on).
- 🌐 **Sinossi episodi in italiano (opzionale) via TMDB + Ollama Cloud**: nuovi servizi `AnimeClickTmdbClient` + `AnimeClickOllamaTranslator`. Cache per-episodio. Opt-in via `EnableEpisodeSynopsisTranslation` + chiavi TMDB/Ollama.
- ⚙️ **Config page**: dropdown modello Ollama con modelli consigliati + personalizzato (gating tier Free/Pro/Max).

### v0.2.7.0 (AniList ID risolto durante ogni scan)
- 🔧 **Nuovo servizio `AnimeClickAniListResolver`**: risoluzione AniList ID estratta in un servizio riutilizzabile (DRY).
- 🖼️ **Fix immagini/metadati su scan automatico**: `SeriesProvider` e `MovieProvider` ora risolvono e scrivono `ProviderIds["AniList"]` a ogni scansione, così l'ImageFetcher AniList trova le copertine in automatico. Prima avveniva solo premendo "Identify & Refresh".
- 🧩 **Nota Fanart per i film**: Fanart.tv per i film è indicizzato per TMDB ID, non AniList. Abilita TheMovieDb come metadata fetcher nella libreria "Anime Movie".
- ✅ **Nuovi unit test**: `ParseAniListIdFromSearch` + `EscapeGraphQL`.

### v0.2.6.0 (Fix response undefined + force image download)
- 🐛 **Fix response body `undefined`**: il client JS leggeva PascalCase (`resp.ItemId`) ma ASP.NET Core serializza in camelCase (`resp.itemId`). v0.2.6.0 legge entrambi i casing.
- 🐛 **Fix "no remote images available"**: v0.2.5.0 usava `providerName: string.Empty` (filtra per provider vuoto = nessuno). v0.2.6.0 usa `null` (tutti i provider) + `IncludeDisabledProviders=true`.
- 🔧 **New `EnsureAniListIdAsync`**: risolve l'AniList ID via GraphQL prima del download immagini (necessario perché AnimeClick non fornisce ID incrociati).

### v0.2.5.0 (Force-download images from Fanart/AniList/TMDB)
- 🔧 **Bypass broken image-refresh path di Jellyfin 10.11.x**: `MetadataRefreshOptions.ReplaceAllImages` non esiste in 10.11.11, quindi il plugin chiama `GetAvailableRemoteImages` + `SaveImage` direttamente nel flusso `IdentifyAndRefresh`.
- 🖼️ **Forced download di 5 tipi immagine**: 1 Primary, fino a 3 Backdrop, 1 Logo, 1 Art, 1 Thumb. Priorità: Fanart > AniList > TheMovieDb > OMDb.
- 🔍 **Nuovo campo `DownloadedImages`** in `IdentifyAndRefreshResponse`: lista `Type:Provider:Url` per ogni immagine salvata.
- ⚠️ **Behaviour change**: `IdentifyAndRefresh` cancella sempre le immagini remote esistenti e le riscarica dagli ImageFetcher abilitati, indipendentemente dalla checkbox `ReplaceAllImages` (ora flag legacy).

### v0.2.4.0 (AnimeClick runs LAST so Studios come from AniList/TMDB)
- 🔧 **`IHasOrder` portato da 0 a 100**: AnimeClick gira dopo AniList/TheMovieDb/OMDb, così i campi che non popola (Studios, OfficialRating) vengono riempiti prima dai provider a monte.
- 🔧 **Mapping difensivo**: `Genres` e `Studios` si applicano solo se la sorgente AnimeClick ha davvero qualcosa.
- 🛡️ **Nessuna regressione**: i campi localizzati (Name IT, Overview IT, CommunityRating, Cast, Sigle) continuano a essere applicati per ultimi e a vincere.

### v0.2.3.0 (Fix Identify & Refresh non riscarica le immagini)
- 🐛 **Fix "Identify & Refresh" non sostituisce le immagini**: l'endpoint `POST /Plugins/AnimeClick/IdentifyAndRefresh` accetta il flag opzionale `ReplaceAllImages` (default false). Quando `true`, rimuove le immagini remote lasciando intatte le locali. Il refresh riscarica dagli ImageFetcher configurati.
- 🔍 **Endpoint diagnostico `GET /Plugins/AnimeClick/AvailableRemoteImages`**: elenca le immagini remote disponibili per un item da ogni ImageFetcher abilitato.