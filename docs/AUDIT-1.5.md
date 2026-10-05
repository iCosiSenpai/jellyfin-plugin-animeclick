# Verifiche 1.5.0.0

## Cosa è cambiato

- `AnimeClickAniListMedia` legge un'opera AniList (formato, episodi, date, stato, voto, studio principale, trailer, copertina, banner, doppiatori giapponesi con il personaggio, staff, relazioni).
- `AnimeClickAniListResolver.GetMediaAsync` la chiede con lo User-Agent del plugin, con la stessa pausa e lo stesso interruttore delle ricerche di identità; cache come una scheda AnimeClick, cache negativa per un ID inesistente, nessuna cache per gli errori.
- `AnimeClickAniListMetadata` completa i campi vuoti di serie e film prima di TMDB, propone copertina e banner dopo Fanart e TMDB, e ricava l'anno di una stagione dalla catena dei sequel. La risoluzione dell'ID AniList nei provider di serie e film avviene ora prima del completamento.
- `AnimeClickArtwork` accetta le immagini della CDN di AniList solo nei percorsi delle copertine, dei banner e delle foto dello staff.
- I provider di stagioni ed episodi passano l'anno ricavato da AniList alla ricerca dei sequel quando Jellyfin non ha date per quella stagione. `AnimeClickLibrarySeasons` conta gli episodi reali di una stagione per la comunità e per AniList.
- Comunità: il relay non ha più credenziali GitHub. Tiene le proposte in coda (`202`), il workflow `community-intake.yml` con `tools/community_intake.py` le trasforma in issue controllate con il token del workflow e comunica il numero al relay; il plugin lo recupera con `GET /v1/proposals/{impronta}` una volta all'ora. `community/relay/deploy.py` pubblica il worker tramite l'API di Cloudflare.
- Interfaccia: scheda AniList in Fonti, voce AniList in Inizio, passo AniList del setup (`SETUP_VERSION` 4) e interruttore nel passo Fonti per le installazioni nuove.

## Verifiche eseguite

- **426 test backend** (23 su AniList, uno sul link recuperato dal relay): lettura di una risposta reale ridotta; ID accettato solo con tipo e anno coerenti, sui casi trovati nella libreria del NAS (film collegato alla serie TV, spot al posto di un film, terza stagione al posto della prima); campi completati solo se vuoti, valori e regista di AnimeClick conservati, preferenze spente rispettate, foto dei doppiatori solo dalla CDN di AniList; catena dei sequel con anno restituito solo a parità di episodi, mai con un cour diverso, con due sequel TV, con uno speciale in mezzo o con un ID che non indica la prima stagione; anno già noto o fonte spenta lasciati intatti; immagini fidate e rifiutate; copertina e banner proposti solo per un'opera coerente; richiesta con lo User-Agent del plugin, risposta in cache e ID inesistente ricordato.
- **31 test Python** (5 sul prelievo: issue aperta e controllata, verdetti, issue già esistente riusata, proposta alterata lasciata in coda) e **10 test del relay** (coda, contatore pubblico, conferme, prelievo solo con il segreto, stato pubblico, record corrotti scartati, limiti).
- **29 test browser**: setup completo con AniList proposto nel passo Fonti e salvato con le chiavi; aggiornamenti dalla 1.2, dalla 1.3 e dalla 1.4 con i soli passi nuovi; scelta spenta rispettata e riaccesa da Fonti con lo stato aggiornato; nessun overflow a 320, 390 e 1280 px.

## Servizio della comunità pubblicato

Il 2026-10-05 `community/relay/deploy.py` ha pubblicato il servizio su `https://animeclick-community.lookatale95.workers.dev` (namespace KV e worker creati tramite l'API di Cloudflare, segreti generati e mai mostrati, chiave di prelievo salvata come `RELAY_ADMIN_SECRET`), e l'indirizzo è nel dataset (`7b0309a`). Verifiche dal vivo: pagina iniziale, proposta non valida rifiutata con `400`, stato di una proposta sconosciuta `404`, prelievo senza chiave `401`. Prova completa con la stagione 3 di Saiki: `202` in coda, workflow **community intake** che apre la [segnalazione #4](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/4) come `github-actions`, la controlla e la chiude come già approvata, e il servizio che dopo circa 20 secondi (ritardo di KV) risponde `published` con il link.

## Limiti

- Il servizio vede il proprio stato con il ritardo di KV (fino a circa un minuto); il prelievo ogni 30 minuti e il controllo orario del plugin lo assorbono, e la ricerca per impronta evita le segnalazioni doppie.
- Il workflow di prelievo non ha un orario su GitHub, così non può essere sospeso dopo 60 giorni senza commit: lo avvia ogni 10 minuti il NAS del curatore (timer `systemd` utente con `animeclick-intake.sh`) solo quando `GET /v1/queue` segnala proposte in attesa. Se il NAS è spento le proposte aspettano in coda. Provato dal vivo: lo script, eseguito da `systemd`, ha avviato il workflow, che ha letto la coda con la propria chiave.

- AniList non ha testi italiani: titoli e trame restano ad AnimeClick, TMDB e TheTVDB.
- Le copertine AniList sono più piccole di quelle di TMDB e Fanart: per questo vengono dopo.
- L'anno di una stagione non arriva per i cour divisi, le serie rimontate per lo streaming e le stagioni ancora in corso, perché il numero di episodi non coincide.
- Un'opera senza ID AniList non riceve dati AniList finché il plugin non risolve l'ID in modo univoco (titolo, anno e formato).
