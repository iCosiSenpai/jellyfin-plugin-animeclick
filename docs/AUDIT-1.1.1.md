# Audit AnimeClick 1.1.1.0 — 2026-10-02

Estensione del recupero dei titoli episodio, richiesta dopo il controllo della riparazione 1.1.0 sul NAS. Quel lavoro aveva verificato 251 episodi, completato un titolo e lasciato 250 nomi invariati senza errori: AnimeClick non forniva titoli utili o il matcher non disponeva di un abbinamento certo. La versione precedente usava già fonti esterne e AI per le sinossi, ma i titoli dipendevano soltanto da AnimeClick.

## Comportamento e protezioni

AnimeClick conserva la precedenza. Quando manca un titolo valido, il nuovo servizio usa gli ID TMDB/TheTVDB già salvati sulla serie: prima cerca titoli italiani in tutte le fonti configurate, poi valuta titoli inglesi e li traduce soltanto con il consenso AI esistente e un profilo completo. L’opzione di recupero esterno è indipendente e disattivabile; non aggiunge servizi o sceglie modelli.

La lingua viene verificata attraverso i record dell’endpoint [traduzioni TMDB](https://developer.themoviedb.org/reference/tv-episode-translations) e l’endpoint traduzioni episodio dell’[API ufficiale TheTVDB](https://thetvdb.github.io/v4-api/). Un endpoint localizzato che restituisce l’originale non basta per dichiarare italiano un testo. TheTVDB risolve prima un unico episodio dalla lista della serie e ne usa l’ID; TMDB verifica l’ID quando presente. Coordinate duplicate, ID discordanti o mappe assolute non dimostrabili producono nessun titolo. I file multi episodio sono esclusi dal recupero esterno.

Il prompt `episode-title-it-v1` richiede soltanto un titolo su una riga e conserva i nomi propri. Ha una cache distinta da quella delle sinossi. L’AI riceve solo il testo del titolo: gli ID usati nella chiave locale della cache non entrano nel corpo della richiesta. Un risultato vuoto, generico, troppo lungo, multilinea o simile a un blocco di codice/JSON non viene applicato; le risposte AI scartate non vengono memorizzate come traduzioni riuscite.

Il recupero della libreria salva ancora soltanto `Name`, dopo una seconda lettura di nome, blocchi, ID AnimeClick e ID esterni, numerazione, percorso e abilitazione del provider. Le modifiche manuali concorrenti prevalgono. Disattivare il recupero esterno o l’AI prima della scrittura impedisce il salvataggio del relativo risultato. Le protezioni e i test della 1.1.0 rimangono validi.

L’analisi della libreria resta locale: non contatta le fonti, non consuma credito AI e non certifica la disponibilità delle alternative. Il pannello segnala questa distinzione; fonte e traduzione diventano visibili durante il recupero. Il conteggio finale distingue titoli alternativi e tradotti. Il pulsante ricontrolla anche episodi senza ID AnimeClick quando hanno un’identità esterna utilizzabile e il provider AnimeClick è esplicitamente abilitato per gli episodi della libreria.

## Verifiche

- **251 test backend** su Jellyfin 12.0.0 e 12.1.0, warning come errori. I nuovi casi coprono titoli senza sinossi, precedenza dell’italiano rispetto all’AI, cache, prompt dedicato, mancato consenso, ID discordanti, duplicati, lingua non corrispondente, risposte improprie, fonte indisponibile, cancellazione, multi episodio e cambio concorrente dell’identità esterna.
- **18 test Playwright**: tutte le preferenze, il nuovo interruttore, progressione e interruzione, conferma della ricerca esterna e conteggi che non promettono un recupero già accertato; rimangono le prove di errori, concorrenza e condivisione facoltativa.
- **Jellyfin 12.0.0 e 12.1.0 isolati**: DLL compilata per l’ABI minima, registrazione del nuovo servizio, plugin attivo, API, default e persistenza della preferenza, accessi amministrativi e interfaccia reale. Librerie di prova vuote, nessun dato di produzione montato.
- Harness compilato per entrambe le API; controllo delle versioni incorporate, validazione del dataset comunità, sintassi JavaScript, `git diff --check`, audit delle dipendenze NuGet e npm.

## Limiti

Le API esterne sono simulate nei test; non è stata usata una credenziale personale né chiamato un servizio AI a pagamento per il collaudo. Questi test dimostrano il flusso e le protezioni, non la disponibilità di titoli per tutti gli episodi privati. Il plugin non può verificare semanticamente ogni traduzione breve; non modifica un campo quando manca una fonte valida.

Il normale aggiornamento dei metadati segue l’ordine dei provider e le regole di Jellyfin; **Sistema tutti i titoli** esegue la scrittura limitata al nome. Il consenso AI già attivo permette le traduzioni dei titoli: nuove richieste possono consumare credito. La pianificazione settimanale del recupero rimane quella preesistente. L’installazione sul NAS viene controllata senza avviare il recupero e confrontando i campi della libreria prima e dopo.

Catalogo pubblico invariato per richiesta del proprietario. Release e installazione seguono [RELEASING.md](RELEASING.md) e il registro operativo autorizzato del NAS.
