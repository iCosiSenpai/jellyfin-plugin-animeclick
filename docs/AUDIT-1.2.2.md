# Audit 1.2.2 — verifica del titolo prima delle fonti alternative

Data: 2026-10-03. Prosegue l'[audit 1.2.1](AUDIT-1.2.1.md), dopo il collaudo sul Jellyfin del NAS richiesto dal proprietario.

## Caso reale e correzione

Chuunibyou Ren S1E6 conserva il titolo inglese «Travelling to the Island of Tsukushi... of Hesitation». La scheda della seconda stagione riparte da uno; gli ID esterni della serie coprono anche la prima stagione. L'anteprima 1.2.1 ha proposto da TheTVDB «L'espiazione... dell'innocente», appartenente alla prima stagione. **Non è stato salvato alcun titolo**. Il recupero è stato temporaneamente disabilitato sul NAS e 1.2.1 marcata prerelease/non-latest, conservando il tag.

La correzione verifica il titolo inglese originale della fonte prima di accettarne la traduzione italiana. Se non concorda con il titolo AnimeClick verificato o con il titolo inglese corrente, la fonte viene scartata. Punteggiatura, maiuscole e accenti non creano differenze artificiali; parole diverse rimangono diverse.

Ordine: AnimeClick italiano → altre fonti italiane coerenti → inglese della fonte coerente tradotto → traduzione del titolo inglese già valido, quando non c'è una fonte coerente. Il titolo AnimeClick inglese verificato precede gli altri testi inglesi. Il nome derivato dal file, un segnaposto o un testo dalla lingua incerta non diventano una fonte di traduzione. Rimangono attive le protezioni su blocchi, preferenze, modifiche concorrenti e salvataggio del solo nome.

## Verifiche

- 367 test backend per Jellyfin 12.0 e 12.1, in CI GitHub: sei regressioni aggiuntive su fonte italiana dell'episodio errato, traduzione del titolo corrente, titolo AnimeClick inglese, originale concordante e nomi di file. 19 prove browser e build dell'harness per entrambe le API.
- Nessuna nuova compilazione o istanza di test sul NAS dopo la segnalazione del carico. I due server temporanei e i file del relativo SDK sono stati eliminati.
- Prima dell'aggiornamento 1.2.1, snapshot di 5.769 elementi estesi. Dopo l'installazione, identità byte per byte: SHA-256 `89ac0929824abf817656c2d956e1346c35b2cc5d461d473554f9ebbfb24bdc69`.
- Durante quel riavvio, l'aggiornamento automatico di Jellyfin ha scaricato Enhanced 12.10: il solo nuovo download è stato annullato tramite le API del server, ripristinando 12.9. Le 38 DLL degli altri plugin risultano identiche. Nessuna opzione di aggiornamento generale modificata.
- Anteprima senza applicazione: caso inglese errato identificato; Ani ni Tsukeru S1E1 tradotto in «Prendo di mira mio fratello» da TheTVDB inglese; titolo italiano di The Dangers in My Heart restituito identico da AnimeClick. Richieste AI reali limitate ai campioni e cache riutilizzata.
- Pacchetto compilato e verificato dalla CI; installazione della correzione sullo stesso container, ripristinando il profilo originale. Verifica mirata dei campioni e degli altri metadati documentata nel registro NAS.

## Esito finale sul NAS

AnimeClick 1.2.2.0 Active, Jellyfin 12.1 healthy, stessa immagine/container. Configurazione completa identica al profilo originale, chiavi conservate, condivisione comunitaria disattivata, opzioni librerie e 38 DLL degli altri plugin identiche. Il trigger di aggiornamento plugin all'avvio, temporaneamente sospeso per il secondo riavvio, è stato ripristinato con l'intervallo giornaliero originale.

L'anteprima corretta per Chuunibyou Ren S1E6 è «In viaggio verso l'isola di Tsukushi... dell'Esitazione», tradotta dal titolo AnimeClick. Ani ni Tsukeru S1E1 diventa «Prendo di mira mio fratello» da TheTVDB inglese; «Io sono morto» di The Dangers in My Heart resta identico da AnimeClick.

Applicati **soltanto i due campioni da correggere**. Confronto dei 5.772 elementi immediatamente prima/dopo: **esattamente due cambiamenti, entrambi nel solo campo `Name`**. Numerazione, ID, trama, cast, tag, immagini e ogni altro campo sono identici. Ripetizione: zero modifiche. Snapshot finale SHA-256 `edc516545d3e9f3295f15f0c31ee02fbf034f8f4ad60e59bca213f298d2964c4`.

I tre elementi aggiuntivi rispetto al riferimento iniziale (serie, stagione, episodio) provengono dalle normali importazioni in corso sul NAS. Prima dei due salvataggi di prova, tutti i metadati memorizzati dei 5.769 elementi precedenti erano ancora uguali; soltanto i tag/blurhash delle immagini di persone condivise, calcolati dall'API in quattro elenchi cast, erano cambiati durante quelle importazioni. Nessun cast, ID o nome precedente era stato alterato dall'installazione.

Immagini remote dal solo provider configurato AnimeClick: **43**, di cui **10 Fanart**, **32 TMDB originale** e una AnimeClick. Lettura e diagnostica non hanno sostituito immagini già salvate. API amministrative, anteprima predefinita senza scrittura, ID non valido 400 e episodio inesistente 404 verificati. Non è stata avviata alcuna riparazione massiva.

CI main `37134904622` e tag `37135091987` riuscite. ZIP pubblico verificato senza autenticazione: 3.350.388 byte, SHA-256 `4925367f6c9afea5489f06c1d5031c6eabedddab310a15432a17a35ad8d7f07a`, MD5 `20032C59DA93ACB1C415175003CAB3CA`. Catalogo al commit `4343b070ce600ea33432091130c4054caceae4b4`, invariato. Registro NAS aggiornato e copie di lavoro rimosse dopo la verifica positiva.

## Limiti

La coerenza del titolo inglese protegge i titoli già informativi e i titoli inglesi trovati su AnimeClick. Se manca qualsiasi titolo informativo, il recupero continua a richiedere identità e coordinate verificate; non si può dedurre automaticamente ogni organizzazione personalizzata delle stagioni. Il plugin conserva i casi ambigui e non garantisce copertura universale o traduzioni editoriali perfette.

Nessuna scansione o riparazione globale usata come test. Catalogo pubblico invariato. Copyright, attribuzioni e loghi delle fonti conservati.
