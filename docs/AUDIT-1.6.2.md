# Verifiche 1.6.2.0

## Azioni rapide in pagina, sblocco schede e navigazione dettaglio

### Backend e API

- **Sblocco e identificazione (`AnimeClickIdentifyController`)**:
  - `POST Plugins/AnimeClick/IdentifyAndRefresh` accetta `Unlock = true` per rimuovere i blocchi su `IsLocked`, `MetadataField.Name` e `MetadataField.Overview` della scheda (e delle stagioni/episodi figli non bloccati separatamente) prima di salvare l'ID AnimeClick e accodare il refresh, restituendo `UnlockedCount`.
  - `GET Plugins/AnimeClick/IdentifyStatus` espone `IsLocked` e `LockedFields` dell'elemento selezionato.
  - Nuovi endpoint `POST Plugins/AnimeClick/UnlockItem` (`ItemId`, `IncludeChildren`, `Field`) e `POST Plugins/AnimeClick/RefreshItem` (`ItemId`, `ReplaceAllMetadata`, `ReplaceAllImages`, `Unlock`).
- **Riparazione per singola serie e sblocco sinossi (`AnimeClickDiagnosticsController`, `AnimeClickLibraryQualityService`)**:
  - Nuovo endpoint `POST Plugins/AnimeClick/RepairSeriesTitles` (`ItemId`, `Unlock`) che ripara sul posto i titoli episodio mancanti o inglesi di una singola serie e restituisce il `LibraryAuditSeriesItem` aggiornato.
  - `LibraryAuditSeasonItem` include `SeasonId` sia in `LibraryAudit` sia in `LibraryAuditSeries`.
  - `POST Plugins/AnimeClick/LibraryQualityRepair` e `AnimeClickLibraryQualityService.QueueRepairAsync` supportano `Unlock = true` per sbloccare `MetadataField.Overview` prima di accodare il refresh delle sinossi, restituendo `UnlockedCount`.
- **Allineamento cache e registro tentativi (`AnimeClickDiagnosticsController`, `AnimeClickRepairLedger`)**:
  - `AnimeClickRepairLedger.Clear()` svuota anche le voci in memoria quando viene svuotata l'intera cache.
  - `ClearCache` per singola scheda (`AnimeClickId`) rimuove dal `RepairLedger` la serie e tutti i suoi episodi (`ForgetLedgerForCard`), così gli episodi marcati `no-source` o `waiting-translation` tornano immediatamente rielaborabili.
  - `POST Plugins/AnimeClick/PreviewEpisodeFallback` restituisce anche `EpisodeTitle` risolto dal catalogo di AnimeClick.

### Interfaccia e usabilità

- **Strumenti (`Web/assets/animeclick-settings.js`)**:
  - Rilevamento immediato del blocco in Jellyfin alla selezione del titolo o della stagione, con badge «Bloccata in Jellyfin» e pulsante «Sblocca ora».
  - Pulsanti contestuali «Sblocca e identifica ora» e «Solo sblocca» sugli errori `409`, «Aggiorna metadati ora» quando il refresh non parte e «Torna alla scheda in Libreria» / «Torna alla libreria».
  - Invio con `Invio` su tutti i campi di ricerca/ID/prova e avvio automatico della ricerca AnimeClick quando `startIdentify` viene aperto dalla Libreria.
  - Salvataggio automatico delle modifiche non salvate prima di eseguire «Da dove arriva questa trama?» e visualizzazione del badge `Titolo AnimeClick`.
- **Libreria e Inizio (`Web/assets/animeclick-library.js`, `Web/assets/animeclick-home.js`)**:
  - Navigazione «Precedente» / «Successivo» (`Alt+←` / `Alt+→`) nel pannello di dettaglio senza chiuderlo, con ripristino del focus nel pannello.
  - Azioni dirette nel pannello di dettaglio per serie, stagioni e singoli episodi (refresh metadati, riparazione titoli serie, abbinamento stagione, override layout, completamento/sblocco/riprova trame, prova catena fonti con ritorno al cassetto).
  - Pulsanti operativi diretti nella checklist «Da fare» in **Inizio** e nel banner di avviso in **Libreria**.

## Verifiche eseguite

- **436 test backend** (`JellyfinVersion=12.0.0` e `JellyfinVersion=12.1.0` con `-warnaserror`): 0 errori, 0 avvisi.
- **35 test browser Playwright** (`tools/AnimeClick.WebTests`): 0 errori, 0 ID duplicati, 0 overflow orizzontali (`320px`, `390px`, `1280px`).
- **32 test Python** (`tools/tests`): 0 errori.
- **10 test servizio della comunità** (`community/relay`): 0 errori.
