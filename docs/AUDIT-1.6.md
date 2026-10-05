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

## Limiti

- Chi usa Jellyfin 10.11 resta alla 0.5.8.0, l'ultima per quella versione del server: non ci saranno altri aggiornamenti su quel ramo, quindi una volta tolta la copia doppia il problema non si ripresenta.
- Il catalogo pubblico è fermo alla 1.0.0.0: la correzione arriva a chi installa dal catalogo quando il catalogo verrà aggiornato.
- I termini aggiuntivi valgono dalla 1.6.0.0; le versioni precedenti restano sotto GPLv3.
