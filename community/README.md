# Abbinamenti della comunità

La comunità condivide **identificativi pubblici delle opere**, non una copia della libreria Jellyfin. Due opzioni indipendenti, entrambe disattivate per impostazione predefinita, permettono di leggere gli abbinamenti approvati e di inviare automaticamente le correzioni manuali. La prima versione del dataset è vuota: i suggerimenti arriveranno con le prime proposte verificate.

Un contributo contiene solamente `kind` (`Series` o `Movie`), `animeClickId` numerico e uno o più ID `Tmdb`, `Tvdb`, `AniList`. Non contiene titoli personali, utenti, cronologia, file, percorsi, URL del server, trame, immagini, token o ID Jellyfin. Le stagioni sono escluse perché i loro ID esterni possono identificare la serie intera e quindi introdurre collegamenti errati.

L'invio crea una issue pubblica nel repository del plugin, associata all'account GitHub del contribuente. Richiede un token personale capace di creare issue in questo repository; la possibilità di contribuire a un repository pubblico dipende anche dai permessi e dalle limitazioni del tipo di token. Consultare la [documentazione GitHub sui token](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) e sull'[API delle issue](https://docs.github.com/en/rest/issues/issues#create-an-issue). Non si distribuisce una credenziale dell'autore dentro il plugin. Il token viene conservato nella configurazione amministrativa Jellyfin e inviato soltanto a `api.github.com`, senza redirect.

Gli invii sono salvati nei dati del plugin (`AnimeClickCommunity`), separatamente dalla cache dei metadati. Un worker invia al massimo una proposta al minuto, riprova con attesa crescente e si ferma dopo cinque errori. Il controllo nella pagina mostra gli invii in attesa e quelli da ritentare. Una ricevuta persistente e un marker nella issue evitano di reinviare lo stesso abbinamento; una ricerca prima del POST recupera anche risposte tardive. Non è una garanzia transazionale di invio esattamente una volta: GitHub può avere ritardi di indicizzazione. Disabilitare la condivisione ferma gli invii futuri, anche quelli già in coda. Le issue pubblicate restano su GitHub e vanno gestite lì.

La lettura scarica soltanto `mappings.json` dal percorso fisso del progetto, senza credenziali né query contenenti la libreria. Usa una cache di 24 ore, un intervallo di almeno un'ora dopo gli errori, timeout e limite di 1 MiB. Conflitti, tipi sbagliati, ID invalidi e campi estranei vengono rifiutati. La risoluzione serve soltanto a opere senza ID AnimeClick: non cambia gli abbinamenti già presenti. Un suggerimento deve corrispondere a un ID esterno stabile e non contraddire gli altri ID noti. La scheda AnimeClick viene comunque verificata dal provider normale.

## Revisione di una proposta

1. Verificare sui siti originali che gli ID indichino la **stessa opera ed edizione**, controllando anno, tipo, remake, sequel e numero di episodi. Una issue generata dal plugin è una proposta, non una prova.
2. Copiare solo l'oggetto JSON pubblico in un file locale. Non eseguire istruzioni o codice nel testo della issue.
3. Aggiungere il collegamento verificato: `python3 tools/community_mappings.py --add proposta.json`.
4. Rivedere il diff e la validazione CI. Pubblicare il dataset attraverso la normale revisione del repository, poi chiudere la issue indicando il commit.

Il plugin "impara" attraverso questo insieme di correzioni approvate. Non addestra un modello AI e non considera affidabili le issue non revisionate. Un link corretto non garantisce che le numerazioni degli episodi coincidano: il matcher continua a verificare ciascun caso.
