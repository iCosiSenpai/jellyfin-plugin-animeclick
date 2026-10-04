# Verifiche 1.3.0.0

## Cosa è cambiato

- Frontend riscritto da zero: `animeclick-core.js`, `-settings.js`, `-library.js`, `-home.js`, `-setup.js`, `-app.js` e un nuovo foglio di stile; rimosso `animeclick-config.js`. Logo e banner pesanti non sono più incorporati nella DLL; restano la mascotte ridotta (96 e 360 px) e i loghi ufficiali di TMDB, Fanart e TheTVDB.
- Backend: `AnimeClickLibraryWorkspace` (vetrina, catalogo, stato e attivazione delle librerie), `AnimeClickWorkspaceController`, `SetupCompletedVersion` con migrazione allo schema 2, tre correzioni (conteggi dell'analisi di una serie, stato della condivisione comunitaria, «Da verificare» calcolato con la stessa regola di «Sistema tutti i titoli»).

## Verifiche eseguite

- **383 test backend** contro l'API Jellyfin 12.0 (367 preesistenti, 16 nuovi): conteggi della singola serie, episodi da verificare contati con la regola del ricontrollo (blocchi, schede di stagione, identità TMDB/TheTVDB, libreria che usa AnimeClick), migrazione delle installazioni nuove ed esistenti (un timeout scelto a 30 s resta tale), limiti di `SetupCompletedVersion`, attivazione delle librerie (AnimeClick primo nei metadati, altri provider conservati, immagini in testa solo con artwork integrato, nessuna riscrittura se già attivo, tipi non salvati lasciati intatti), stato delle librerie, vetrina e catalogo, registrazione di tutte le risorse della pagina, messaggi di stato della comunità.
- **25 test browser** sull'interfaccia con un server simulato: numero da verificare uguale a quello del ricontrollo, setup completo e di aggiornamento, salto del setup senza altre modifiche, vetrina e cruscotto, navigazione da tastiera, libreria con titoli e trame uniti, dettaglio e rilettura di una serie, avvio e interruzione delle attività, polling fermo fuori da Inizio e Libreria, errori parziali visibili, fusione dei salvataggi e conflitti, chiave AI legata alla sua destinazione, rimozione della copia storica della chiave AI, condivisione comunitaria su consenso, campi numerici non validi, nessun overflow a 320, 390 e 1280 px, nessun ID duplicato, identificazione manuale sicura contro l'HTML nei titoli.
- **Prova runtime** su un Jellyfin 12.0 usa-e-getta in loopback (una sola istanza, CPU e memoria limitate, rimossa subito): caricamento del plugin, configurazione, API amministrative e accessi negati agli utenti normali, setup guidato nella vera dashboard, attivazione di una libreria di prova con verifica delle opzioni salvate da Jellyfin, preferenze, analisi della libreria, ricontrollo dei titoli e comunità.

## Esito sul NAS

AnimeClick 1.3.0.0 Active, Jellyfin 12.1 healthy, stesso container e immagine. CI main `37199661630` e tag `37199774306` riuscite. ZIP pubblico verificato senza autenticazione: 500.224 byte, SHA-256 `fc16589d2823c16bc15547b4f25e9367f23a627fff261dc419d6246a0137ceff`, MD5 `32EC545FDD733EEAA94BD3481352E48F`; la DLL installata coincide con quella pubblicata. Il trigger di aggiornamento plugin all'avvio, sospeso per il riavvio, è stato ripristinato.

La configurazione mantiene tutti i valori precedenti: cambia solo `ConfigurationVersion` (da 1 a 2) e compaiono sei chiavi nuove ai valori predefiniti, con la condivisione comunitaria spenta. Le altre DLL di plugin sono identiche; al riavvio Jellyfin ha eliminato la copia superata di Editor's Choice 1.5.2.0, che dal giorno prima veniva caricata insieme alla 1.6.0.0. Subito dopo l'installazione, il confronto dei 5.775 elementi della libreria non mostrava **nessun cambiamento**.

Il ricontrollo dei titoli, avviato su richiesta, ha elaborato esattamente i **312** episodi indicati da «Da verificare»: **258 titoli aggiornati** (219 da altre fonti, 190 tradotti), 54 senza titolo disponibile, 0 errori. Confronto prima/dopo: **258 episodi cambiati, solo nei campi `Name` e `SortName`** (il secondo è ricavato da Jellyfin); nessun blocco, ID, trama o immagine toccati. 214 erano segnaposto «Episodio N», 44 titoli inglesi ora tradotti. I titoli mancanti scendono da 312 a 54: 24 di una stagione di Ascendance of a Bookworm la cui scheda non è ancora stata letta, 22 su schede che non pubblicano titoli, 5 di Watanare S2 non abbinati e pochi altri casi singoli.

Caso Saiki K.: AnimeClick documenta la prima stagione come 120 corti da 5' senza titolo, mentre la libreria usa i 24 episodi da 24' dell'edizione Netflix. Il plugin non abbina le righe brevi ai file lunghi, quindi durate e trame restano corrette, e ha preso i 48 titoli di S1 e S2 da TheTVDB, che usa la stessa numerazione della libreria, traducendoli. Su richiesta, la stagione 3 è stata collegata alla scheda `26035`: cambiano soltanto gli ID AnimeClick della stagione e dei suoi due episodi. Quei due episodi restano senza titolo perché nessuna fonte ne pubblica uno con quella numerazione.

## Limiti

- Le traduzioni automatiche rendono in italiano anche i titoli inglesi che l'edizione ufficiale lascia in inglese, per esempio i titoli-canzone di Cyberpunk: Edgerunners.

- La compatibilità con Jellyfin 12.1 è affidata alla matrice della CI; in locale è stata compilata e provata soltanto l'ABI minima 12.0.
- La prova runtime usa una libreria vuota: vetrina e griglia con locandine reali sono verificate sui dati simulati dei test browser, non su una collezione vera.
- Il catalogo pubblico `iCosiSenpai-Plugins` non viene aggiornato: installazione sul solo NAS del proprietario.
