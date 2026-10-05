# Verifiche 1.6.0.0

## Issue #2: due versioni caricate dopo un aggiornamento

Causa, letta nel `PluginManager` di Jellyfin: all'avvio le cartelle dei plugin vengono ordinate e raggruppate per il `name` del loro `meta.json`, e solo le versioni più vecchie **con lo stesso nome** vengono cancellate. L'installazione dal catalogo scrive il nome del pacchetto («AnimeClick Metadata»); quando il plugin si carica, Jellyfin lo sostituisce con il `Name` del plugin («AnimeClick Plugin»). All'aggiornamento successivo la nuova cartella riparte con il nome del pacchetto: due nomi diversi, due plugin caricati con lo stesso GUID, `InvalidCastException` sulla configurazione. Lo stesso schema si è visto sul NAS con Editor's Choice il 2026-10-03.

Correzioni:

- `tools/release_meta.py` genera il `meta.json` incluso nello ZIP, con nome, GUID e descrizione letti da `Plugin.cs`, versione dal progetto e ABI da `Directory.Build.props`. Jellyfin conserva il nome di un `meta.json` già presente nel pacchetto, quindi ogni versione installata dal catalogo porta il nome del plugin e la vecchia viene cancellata al riavvio.
- `AnimeClickPluginVersions.SupersedeOlderCopies`, chiamato all'avvio del plugin, segna «Superseded» le cartelle con lo stesso GUID e una versione più vecchia ancora attive; Jellyfin non le carica più dal riavvio successivo. Non cancella nulla, lascia intatte le copie disattivate o più recenti e non interrompe mai l'avvio.

## Licenza

`LICENSE` contiene il testo ufficiale dell'AGPLv3 (gnu.org, SHA-256 `0d96a4ff68ad6d4b6f1f30f713b18d5184912ba8dd389f86aa7710db079abcb0`); `PackageLicenseExpression` è `AGPL-3.0-only`. Le librerie Jellyfin restano GPL-3.0-only: la sezione 13 delle due licenze ne consente la combinazione. `NOTICE` descrive i termini aggiuntivi della sezione 7 — attribuzione (b), versioni modificate riconoscibili per nome, GUID, User-Agent e uso del servizio della comunità (c), marchi estesi a mascotte e servizio della comunità (e) — e il piè di pagina mostra attribuzione e collegamento al codice sorgente, come chiede la sezione 13.

## Verifiche eseguite

- **429 test backend**: copia più vecchia segnata come superata e ogni altra cartella intatta (copia disattivata, più recente, di un altro plugin, manifest illeggibile, cartella senza manifest), nessun file temporaneo lasciato, campi del manifest conservati, percorsi mancanti senza errori; User-Agent predefinito che identifica il progetto.
- **32 test Python**: il `meta.json` dello ZIP porta il nome dichiarato dal plugin, il suo GUID, l'ABI minima e una versione a quattro parti.
- **29 test browser**: attribuzione e collegamento al codice sorgente nella pagina.

## Esito sul NAS

AnimeClick 1.6.0.0 Active, Jellyfin 12.1 healthy in 30 s, stesso container e immagine. Tag sul commit `c0ca3b0`, CI riuscita su 12.0 e 12.1. ZIP pubblico verificato senza autenticazione: 536.426 byte, cinque file, SHA-256 `7365cb4a4aa548eb0f0eadcaaec198f7ff140b0a3d46ba96734bb386889c57d8`, MD5 `B80AA8D46D956EDAED1D6167268C2381`; DLL installata identica e `meta.json` con nome «AnimeClick Plugin». Configurazione identica (50 chiavi), 37 DLL degli altri plugin identiche, libreria (5.811 elementi) identica byte per byte, invio della comunità tramite il servizio, piè di pagina con attribuzione e codice sorgente servito dalla dashboard.

## Catalogo e versioni obsolete

Su decisione del proprietario, il plugin funziona solo con Jellyfin 12 e ogni release viene pubblicata nel catalogo. Il manifest di `iCosiSenpai-Plugins` (commit `cd3bb8c`) contiene ora le dieci versioni stabili per Jellyfin 12, dalla 1.0.0.0 alla 1.6.0.0 (la prerelease 1.2.1.0 esclusa), con l'MD5 di ogni ZIP pubblico ricalcolato e coincidente con quello registrato al rilascio; tolte le 30 voci 0.x per Jellyfin 10.x. Cancellate le 39 release GitHub 0.x con i loro ZIP; i 39 tag `v0.*` restano per la storia del codice. README, risposta alla issue #2 e note della release indicano che le versioni 0.x sono obsolete.

## Limiti

- Chi è ancora su Jellyfin 10.x non riceve più aggiornamenti: deve passare a Jellyfin 12 e installare la 1.6 dal catalogo, togliendo prima le cartelle `AnimeClick Metadata_0.*`.
- Le versioni 1.1–1.5 nel catalogo non contengono il `meta.json`: installarle oggi esplicitamente da catalogo ripropone il vecchio nome; l'aggiornamento alla 1.6, che Jellyfin propone di default, lo corregge.
- I termini aggiuntivi valgono dalla 1.6.0.0; le versioni precedenti restano sotto GPLv3.
