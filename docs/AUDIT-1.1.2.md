# Audit AnimeClick 1.1.2.0 — 2026-10-02

La richiesta successiva alla 1.1.1 estende la precedenza di AnimeClick a ogni campo, con recupero da una fonte inglese e traduzione quando il dato è assente, generico o indisponibile. Il flusso precedente aveva copertura completa delle alternative per i titoli episodio, ma serie e film recuperavano solo trame TMDB italiane. Alcuni segnaposto non vuoti interrompevano il recupero; una scheda AnimeClick irraggiungibile bloccava le sinossi episodio anche con ID esterni disponibili.

## Implementazione

La normalizzazione dei segnaposto è condivisa da mappatura, recupero, analisi e riparazione. Le trame AnimeClick chiaramente inglesi lasciano spazio al recupero italiano; i testi nativi validi sono conservati per intero, senza il limite applicato alle richieste AI. Titoli, trame, generi e tag vengono valutati indipendentemente: le alternative completano solo campi non forniti da AnimeClick. Le opzioni di ciascun campo continuano a valere. Nomi propri e dati neutri non passano dal traduttore.

Serie e film cercano titoli e trame mediante ID già salvati e traduzioni esplicite [TMDB serie](https://developer.themoviedb.org/reference/tv-series-translations), [TMDB film](https://developer.themoviedb.org/reference/movie-translations) e [TheTVDB](https://thetvdb.github.io/v4-api/). Gli ID di risposta TMDB devono corrispondere. Si provano tutte le fonti italiane prima dell’inglese. I generi TMDB vengono richiesti in italiano; in loro assenza possono essere tradotti i generi inglesi dell’opera TheTVDB identificata. I tag provengono dall’endpoint keyword dell’opera TMDB e vengono tradotti solo con un profilo AI completo.

Prompt distinti per titoli, trame ed etichette. Le etichette devono tornare come array JSON di stringhe con il numero atteso di elementi, senza segnaposto. Cache separate per provider, tipo di opera, ID, campo, lingua, profilo e testo. Film e serie con lo stesso ID numerico non condividono il contenuto.

La coda delle trame ricontrolla il consenso AI e l’interruttore pertinente: `EnablePlot` per serie/film, `EnableEpisodeSynopsisTranslation` per episodi. La disabilitazione delle trame episodio non impedisce più il recupero delle trame serie/film. Gli esiti della coda distinguono attesa ed errore di accodamento. La pubblicazione resta limitata al campo Overview e all’esatto testo ancora presente, preservando correzioni manuali e blocchi.

Le sinossi episodio usano gli ID esterni noti anche se AnimeClick non risponde. TheTVDB usa la traduzione dell’episodio identificato; TMDB verifica l’ID episodio e permette rimappatura assoluta soltanto dopo un 404 accertato. Una traduzione assente o un ID discordante non autorizza una rimappatura. I file multi episodio non usano il recupero esterno della singola sinossi.

Le immagini conservano l’ordine 100 e la precedenza dei provider ad alta risoluzione, come richiesto dal proprietario. L’ordine immagini salvato sul NAS resta invariato; nessuna immagine viene cancellata o aggiornata.

## Verifiche

- **284 test backend** su Jellyfin 12.0.0 e 12.1.0, warning come errori. 33 nuovi casi verificano indipendenza dei campi, zero richieste quando AnimeClick è completo, segnaposto, traduzioni e cache, separazione film/serie, campi disabilitati, errori delle fonti, consenso, generi/tag, ID discordanti e cancellazione. Rimangono le prove delle protezioni della libreria e della concorrenza.
- **18 test browser**, inclusi salvataggio di tutte le preferenze, flusso di riparazione, avanzamento, interruzione, errori, modifiche concorrenti e condivisione facoltativa.
- **Runtime Jellyfin 12.0.0 e 12.1.0 isolati** con DLL per l’ABI minima: caricamento, registrazione servizi, API, persistenza configurazione, autorizzazione amministrativa e pagina effettivamente servita.
- Harness per entrambe le API, audit dipendenze NuGet/npm, validazione dataset comunità, versioni incorporate e `git diff --check`.

## Limiti e installazione

Le API esterne sono simulate nei test: nessuna richiesta AI a pagamento e nessuna garanzia di disponibilità per ogni episodio privato. La qualità semantica di una traduzione non è certificabile per tutti i titoli brevi. In assenza di fonte valida il plugin non inventa contenuti. I campi non supportati continuano a essere gestiti dagli altri provider configurati in Jellyfin.

Sul NAS autorizzato si attiva la precedenza anche sui campi neutri e si porta AnimeClick prima nell’ordine dei provider metadati che già lo includono, senza abilitarlo in altre librerie. Sono conservate tutte le altre impostazioni e credenziali. Si confrontano i dati della libreria prima/dopo e si verifica il caricamento senza avviare riparazioni o scansioni. Le copie di lavoro sono rimosse solo dopo verifica positiva, come richiesto dal registro NAS.

Catalogo pubblico invariato per richiesta del proprietario. Pubblicazione e installazione seguono [RELEASING.md](RELEASING.md) e il registro operativo autorizzato del NAS.
