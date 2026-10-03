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

## Limiti

La coerenza del titolo inglese protegge i titoli già informativi e i titoli inglesi trovati su AnimeClick. Se manca qualsiasi titolo informativo, il recupero continua a richiedere identità e coordinate verificate; non si può dedurre automaticamente ogni organizzazione personalizzata delle stagioni. Il plugin conserva i casi ambigui e non garantisce copertura universale o traduzioni editoriali perfette.

Nessuna scansione o riparazione globale usata come test. Catalogo pubblico invariato. Copyright, attribuzioni e loghi delle fonti conservati.
