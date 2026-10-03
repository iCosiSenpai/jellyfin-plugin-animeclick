# Audit 1.2.1 — titoli inglesi e verifica sul server

Data: 2026-10-03. Richieste: includere i titoli inglesi nel recupero, verificare il plugin su Jellyfin, correggere l'icona della sidebar e ripristinare l'installazione da manifest nel README.

## Cambiamenti verificabili

- Classificazione dei titoli brevi distinta dalle trame: almeno due indizi inglesi, nessun indizio italiano. Italiano, nomi singoli e lingua incerta rimangono esclusi. È un criterio prudente, non un riconoscimento universale delle lingue.
- Priorità AnimeClick italiano → altre fonti italiane → traduzione dell'inglese, privilegiando il testo AnimeClick quando disponibile. Le chiavi, l'attivazione delle fonti e l'abilitazione AI restano necessarie.
- Riparazione del solo nome con rilettura dell'episodio dopo la rete: blocchi, disabilitazione del provider, cambi di numerazione, percorso, identità e titolo impediscono il salvataggio. Italiano/inglese ancora identico e risultati generici o malformati non contano come riparazione.
- Nuova versione del prompt/cache dei titoli per non riutilizzare risultati accettati con la precedente validazione. Numerazione, ID, trama, immagini e altri campi non vengono aggiornati dall'azione titoli.
- `TestEpisodeTitle`: API amministrativa per un solo episodio esistente. `Apply` è `false` per default; l'anteprima usa la pipeline reale e può usare l'AI configurata, senza salvare metadati. Il salvataggio esplicito riusa tutte le protezioni del servizio titoli.
- `MenuIcon = movie`: Jellyfin 12 usa il nome di un'icona Material; in assenza del campo mostra `folder`. [Implementazione ufficiale Jellyfin Web](https://github.com/jellyfin/jellyfin-web/pull/8053). Non sono stati modificati Jellyfin Web o altri plugin.
- DTO immagini rinominato per evitare la collisione con `MediaBrowser.Model.Providers.RemoteImageInfo`. Documento OpenAPI HTTP 200 verificato sui server 12.0 e 12.1 temporanei dopo la correzione.
- Manifest ufficiale verificato e ripristinato nel README come installazione consigliata. Le nuove release GitHub non vengono presentate come già disponibili nel catalogo.

## Prove completate prima della release

- 361 test backend per Jellyfin 12.0 e 12.1, avvisi trattati come errori; selezione dei titoli inglesi, nomi italiani/incerti, campi bloccati, modifiche concorrenti, priorità delle fonti, AI non configurata e traduzioni non valide.
- 19 test Playwright: preferenze, salvataggio e conflitti, mobile, errori API, avvio unico delle attività, avanzamento, annullamento e ritrovamento dell'attività dopo aver riaperto la pagina. Conferma aggiornata per indicare anche i titoli inglesi.
- Caricamento 1.2.1, configurazione, provider registrati, accesso amministrativo/negato, interfaccia web reale, icona dichiarata e OpenAPI verificati su due Jellyfin temporanei con soli dati sintetici in `/tmp`.
- Il proprietario ha segnalato il carico del doppio server e del compilatore: entrambi i container e i processi di prova sono stati fermati e rimossi immediatamente. La prova di recupero completo sui media sintetici non è terminata e non viene dichiarata riuscita. Le compilazioni successive sono affidate alla CI GitHub; il collaudo prosegue sul Jellyfin esistente con campioni singoli.
- Build dell'harness 12.1, audit NuGet senza vulnerabilità segnalate, audit npm senza vulnerabilità e validazione degli abbinamenti comunitari.
- Sul NAS, diagnostica reale TMDB, TheTVDB, Fanart e servizio AI riuscita. Nessuna riparazione massiva avviata per questi controlli. La condivisione comunitaria resta disattivata.

## Installazione e limiti

Aggiornamento esclusivamente di AnimeClick nel container Jellyfin esistente, con backup operativo e confronto di configurazione, opzioni librerie, altri plugin e metadati prima/dopo. Catalogo pubblico invariato. Il registro `/volume1/docker/AGENT.md` documenta l'esito sul NAS.

Le prove non garantiscono copertura o qualità di ogni titolo disponibile: un dato assente, un'identità ambigua o un titolo dalla lingua incerta rimangono invariati. Il test amministrativo di un episodio permette di verificare un caso senza attivare il lavoro globale.
