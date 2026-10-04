# Verifiche 1.4.0.0

## Cosa è cambiato

- **Dati:** dataset allo schema 2 (`community/mappings-v2.json`) con le stagioni e l'indirizzo del servizio; `mappings.json` resta allo schema 1 e viene generato dallo stesso strumento, perché le versioni fino alla 1.3 rifiutano l'intero file davanti a un tipo che non conoscono. Contratto in `community/schema-v2.json` ed esempi condivisi in `community/fixtures/proposals.json`.
- **Plugin:** `AnimeClickCommunityData` (validazione, testo canonico, impronta, abbinamento di opere e stagioni) separato da `AnimeClickCommunityService` (lettura, coda, invio al servizio o a GitHub, storico e stato delle proposte, riepilogo). Il provider delle stagioni usa una scheda approvata solo per una stagione senza ID con lo stesso numero e lo stesso numero di episodi. Configurazione allo schema 3 con `CommunitySharingMode`.
- **Interfaccia:** passo «Comunità» del setup (`SETUP_VERSION` 3), richiesta «Condividi» dopo una correzione, tessera in Inizio, scheda Comunità riordinata, scelta della stagione in «Correggi un abbinamento».
- **Servizio della comunità:** Cloudflare Worker in `community/relay/`, pubblicato da `community-relay.yml` solo quando i segreti esistono.
- **Revisione:** `tools/community_review.py` e `community-review.yml` (controlli automatici, etichetta `approvato`, commit del dataset).

## Verifiche eseguite

- **402 test backend** contro l'API Jellyfin 12.0 (33 sulla comunità): tutte le fixture condivise accettate o rifiutate come nelle altre implementazioni, con lo stesso testo canonico e la stessa impronta; numeri come testo e nomi dei campi con maiuscole diverse rifiutati (le opzioni JSON «Web» di .NET li accettavano, trovato dal test); stagioni abbinate solo con stesso numero ed episodi, mai con ID in contrasto o con il solo AniList, mai con due schede per la stessa suddivisione; provider delle stagioni con la scheda approvata e senza, stagione già identificata intatta; migrazione allo schema 3 (`Always` solo per chi aveva attivato la condivisione); «Chiedi» che non invia nulla prima del clic, «Mai» che ferma anche la coda; invio al servizio con i soli campi previsti, nessun dato privato e nessuna intestazione di autorizzazione; link fuori dal repository scartati; proposta rifiutata scartata, servizio occupato ritentato; invio diretto a GitHub con titolo e corpo condivisi; nessuna richiesta di rete con tutto spento; coda corrotta, riavvio e svuotamento della cache; stati «approvata» dal dataset e «non accettata» dalla issue.
- **28 test browser:** setup completo con il passo Comunità, aggiornamento dalla 1.2 e dalla 1.3 (solo i passi nuovi), richiesta «Condividi» su una stagione scelta nella correzione, «Non chiedere più», tessera di Inizio con attivazione dell'elenco, scheda Comunità con link solo verso le issue del repository, scelta della condivisione e token, nessun overflow a 320, 390 e 1280 px.
- **25 test Python** dello strumento e della revisione: fixture, conflitti, file legacy allineato byte per byte, indirizzo del servizio solo `https` semplice, lettura di una scheda AnimeClick reale, schede richieste con un nome nell'indirizzo (un numero da solo risponde 404: trovato provando la revisione sui 169 abbinamenti della libreria del NAS), proposta alterata dopo l'apertura, scheda o ID inesistenti, dubbi senza rifiuto quando un sito non risponde o manca la chiave, approvazione che scrive entrambi i file.
- **9 test del servizio** con KV, limitatore e GitHub simulati: fixture, campi estranei e corpi troppo grandi, una sola issue per proposta con conferme contate una volta per installazione, issue aperta da un plugin con token riutilizzata, limiti per raffica, per installazione e globali, GitHub irraggiungibile come errore temporaneo, nessun indirizzo salvato o pubblicato.

## Limiti

- Il servizio della comunità richiede un account Cloudflare e i segreti descritti in `community/README.md`; finché non è pubblicato le proposte senza token restano in coda.
- Il limitatore di Cloudflare conta per finestre di 60 secondi: il tetto giornaliero per installazione e quello globale sono contatori in KV, eventualmente consistenti, quindi approssimati di poche unità.
- Le issue aperte dal servizio risultano dell'account che ha creato il token del servizio.
- Il numero di episodi di una stagione conta i file reali: chi ha una stagione incompleta riceverà la scheda approvata solo quando l'avrà completa.
- La prova runtime su un Jellyfin isolato e l'installazione sul NAS non fanno parte di questa verifica; la compatibilità con 12.1 è affidata alla matrice della CI.
