# Verifiche 1.5.0.0

## Cosa è cambiato

- `AnimeClickAniListMedia` legge un'opera AniList (formato, episodi, date, stato, voto, studio principale, trailer, copertina, banner, doppiatori giapponesi con il personaggio, staff, relazioni).
- `AnimeClickAniListResolver.GetMediaAsync` la chiede con lo User-Agent del plugin, con la stessa pausa e lo stesso interruttore delle ricerche di identità; cache come una scheda AnimeClick, cache negativa per un ID inesistente, nessuna cache per gli errori.
- `AnimeClickAniListMetadata` completa i campi vuoti di serie e film prima di TMDB, propone copertina e banner dopo Fanart e TMDB, e ricava l'anno di una stagione dalla catena dei sequel. La risoluzione dell'ID AniList nei provider di serie e film avviene ora prima del completamento.
- `AnimeClickArtwork` accetta le immagini della CDN di AniList solo nei percorsi delle copertine, dei banner e delle foto dello staff.
- I provider di stagioni ed episodi passano l'anno ricavato da AniList alla ricerca dei sequel quando Jellyfin non ha date per quella stagione. `AnimeClickLibrarySeasons` conta gli episodi reali di una stagione per la comunità e per AniList.
- Interfaccia: scheda AniList in Fonti, voce AniList in Inizio, passo AniList del setup (`SETUP_VERSION` 4) e interruttore nel passo Fonti per le installazioni nuove.

## Verifiche eseguite

- **425 test backend** (23 su AniList): lettura di una risposta reale ridotta; ID accettato solo con tipo e anno coerenti, sui casi trovati nella libreria del NAS (film collegato alla serie TV, spot al posto di un film, terza stagione al posto della prima); campi completati solo se vuoti, valori e regista di AnimeClick conservati, preferenze spente rispettate, foto dei doppiatori solo dalla CDN di AniList; catena dei sequel con anno restituito solo a parità di episodi, mai con un cour diverso, con due sequel TV, con uno speciale in mezzo o con un ID che non indica la prima stagione; anno già noto o fonte spenta lasciati intatti; immagini fidate e rifiutate; copertina e banner proposti solo per un'opera coerente; richiesta con lo User-Agent del plugin, risposta in cache e ID inesistente ricordato.
- **29 test browser**: setup completo con AniList proposto nel passo Fonti e salvato con le chiavi; aggiornamenti dalla 1.2, dalla 1.3 e dalla 1.4 con i soli passi nuovi; scelta spenta rispettata e riaccesa da Fonti con lo stato aggiornato; nessun overflow a 320, 390 e 1280 px.

## Limiti

- AniList non ha testi italiani: titoli e trame restano ad AnimeClick, TMDB e TheTVDB.
- Le copertine AniList sono più piccole di quelle di TMDB e Fanart: per questo vengono dopo.
- L'anno di una stagione non arriva per i cour divisi, le serie rimontate per lo streaming e le stagioni ancora in corso, perché il numero di episodi non coincide.
- Un'opera senza ID AniList non riceve dati AniList finché il plugin non risolve l'ID in modo univoco (titolo, anno e formato).
