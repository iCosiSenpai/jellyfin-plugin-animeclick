# Verifiche 1.3.0.0

## Cosa è cambiato

- Frontend riscritto da zero: `animeclick-core.js`, `-settings.js`, `-library.js`, `-home.js`, `-setup.js`, `-app.js` e un nuovo foglio di stile; rimosso `animeclick-config.js`. Logo e banner pesanti non sono più incorporati nella DLL; restano la mascotte ridotta (96 e 360 px) e i loghi ufficiali di TMDB, Fanart e TheTVDB.
- Backend: `AnimeClickLibraryWorkspace` (vetrina, catalogo, stato e attivazione delle librerie), `AnimeClickWorkspaceController`, `SetupCompletedVersion` con migrazione allo schema 2, tre correzioni (conteggi dell'analisi di una serie, stato della condivisione comunitaria, «Da verificare» calcolato con la stessa regola di «Sistema tutti i titoli»).

## Verifiche eseguite

- **383 test backend** contro l'API Jellyfin 12.0 (367 preesistenti, 16 nuovi): conteggi della singola serie, episodi da verificare contati con la regola del ricontrollo (blocchi, schede di stagione, identità TMDB/TheTVDB, libreria che usa AnimeClick), migrazione delle installazioni nuove ed esistenti (un timeout scelto a 30 s resta tale), limiti di `SetupCompletedVersion`, attivazione delle librerie (AnimeClick primo nei metadati, altri provider conservati, immagini in testa solo con artwork integrato, nessuna riscrittura se già attivo, tipi non salvati lasciati intatti), stato delle librerie, vetrina e catalogo, registrazione di tutte le risorse della pagina, messaggi di stato della comunità.
- **25 test browser** sull'interfaccia con un server simulato: numero da verificare uguale a quello del ricontrollo, setup completo e di aggiornamento, salto del setup senza altre modifiche, vetrina e cruscotto, navigazione da tastiera, libreria con titoli e trame uniti, dettaglio e rilettura di una serie, avvio e interruzione delle attività, polling fermo fuori da Inizio e Libreria, errori parziali visibili, fusione dei salvataggi e conflitti, chiave AI legata alla sua destinazione, rimozione della copia storica della chiave AI, condivisione comunitaria su consenso, campi numerici non validi, nessun overflow a 320, 390 e 1280 px, nessun ID duplicato, identificazione manuale sicura contro l'HTML nei titoli.
- **Prova runtime** su un Jellyfin 12.0 usa-e-getta in loopback (una sola istanza, CPU e memoria limitate, rimossa subito): caricamento del plugin, configurazione, API amministrative e accessi negati agli utenti normali, setup guidato nella vera dashboard, attivazione di una libreria di prova con verifica delle opzioni salvate da Jellyfin, preferenze, analisi della libreria, ricontrollo dei titoli e comunità.

## Limiti

- La compatibilità con Jellyfin 12.1 è affidata alla matrice della CI; in locale è stata compilata e provata soltanto l'ABI minima 12.0.
- La prova runtime usa una libreria vuota: vetrina e griglia con locandine reali sono verificate sui dati simulati dei test browser, non su una collezione vera.
- Il catalogo pubblico `iCosiSenpai-Plugins` non viene aggiornato: installazione sul solo NAS del proprietario.
