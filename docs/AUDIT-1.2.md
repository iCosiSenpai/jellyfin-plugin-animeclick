# Audit 1.2 — provider autonomo e immagini integrate

Data: 2026-10-03. Obiettivo: rimuovere la dipendenza dagli altri provider Jellyfin per ottenere le identità esterne e completare metadati e immagini nelle librerie anime.

## Cambiamenti

- Identificazione TMDB interna per film e serie. ID manuali preservati; IMDb/TheTVDB tramite `/find`, selezionando il solo tipo corretto. Ricerca per nome e alias con titolo esatto, animazione e anno. Le ambiguità tra remake o alias discordanti restano senza abbinamento automatico. I suffissi di sequel non vengono eliminati dall’identità cercata.
- Il resolver degli episodi storico usa la stessa identificazione verificata: non viene più accettato il primo risultato di ricerca e non vengono riusati vecchi abbinamenti automatici privi di questa verifica.
- AnimeClick completa direttamente date, studi, voto con conteggio non nullo, classificazione italiana, stato/fine della serie, ID, trailer e categorie di cast mancanti. I campi AnimeClick esistenti restano prioritari; registi e autori possono completare un cast che contiene soltanto attori. Le preferenze dei singoli campi sono rispettate.
- Stagioni: scheda AnimeClick distinta quando disponibile, poi TMDB con traduzioni esplicite italiane o inglese tradotto. Episodi: date e ID da coordinate verificate, senza cambiare numeri locali o associare un solo episodio a un file multiplo.
- Immagini: Fanart v3.2 integrato, poi TMDB originale, poi poster AnimeClick. Stagioni Fanart filtrate per numero esatto; fotogrammi degli episodi e ritratti TMDB. Poster filtrati per larghezza. Immagini già salvate conservate dall’installazione.
- Fanart accetta chiave personale, di progetto o entrambe. Richieste con chiavi nelle intestazioni; cache separata per impronta del profilo. Il modulo include link ufficiale, campi protetti e test amministrativo, senza inviare chiavi nelle URL delle immagini o nei risultati.
- CDN e redirect verificati prima della richiesta; payload con ID errato non accettati né memorizzati. Errori temporanei e autenticazione fallita non diventano assenze persistenti. Cache, risposte limitate e richieste aggregate evitano download eccessivi.

## Verifiche

- 327 test backend su Jellyfin 12.0.0 e 12.1.0, con avvisi trattati come errori.
- 19 test Playwright: tutte le preferenze, schermi piccoli, API fallite, conflitti di salvataggio, avanzamento e interruzione; nuova configurazione Fanart, verifica e conservazione delle chiavi.
- Provider reali di serie e film eseguiti con fonti HTTP simulate: nessun ID esterno iniziale e AnimeClick temporaneamente indisponibile. Identificazione, trama italiana, cast e campi neutri arrivano dal solo provider AnimeClick.
- Omonimi, remake, alias romaji, film/serie, ID invalidi o discordanti, errori temporanei, interruttori disattivati, episodi multipli e speciali.
- Fanart prima di TMDB, nessuna credenziale sui CDN, ID errati, cache per credenziale, errore Fanart con ripiego TMDB, filtri stagione/risoluzione e rifiuto degli URL esterni o redirect locali.
- Build del programma diagnostico e caricamento del pacchetto minimo in due server Jellyfin isolati; API amministrative e frontend verificati. Nessun servizio AI a pagamento invocato nei test.
- La chiave Fanart del proprietario è stata provata in sola lettura: HTTP 200, 90 immagini per il campione; nessuna chiave nei documenti o nel repository.

## Installazione richiesta

Pubblicazione GitHub e installazione esclusivamente sul NAS del proprietario. Catalogo `iCosiSenpai-Plugins` invariato. Nelle sole librerie Anime TV e Anime Movie, AnimeClick diventa l’unico provider remoto di metadati e immagini; lettori NFO, estrattori locali e opzioni rimanenti conservati. Altri plugin installati conservati per gli altri contenuti.

La verifica di produzione confronta i metadati prima e dopo il riavvio, senza refresh dei contenuti. Backup operativo, verifica dello stato del plugin e registro NAS precedono la pulizia della copia di lavoro. Il collaudo non promette copertura universale delle fonti e non misura la qualità linguistica di ogni traduzione.
