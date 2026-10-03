# Changelog

## 1.2.2.0

Scarta i titoli alternativi di un'altra stagione verificando il titolo inglese originale. Quando manca una fonte coerente, traduce il titolo inglese già valido; esclude i nomi dei file. Correzione trovata sul NAS in anteprima, senza salvare la proposta errata. Include le novità 1.2.1.

- [Note complete](docs/releases/1.2.2.0.md)
- [Audit e verifiche](docs/AUDIT-1.2.2.md)

## 1.2.1.0

«Sistema tutti i titoli» include i nomi inglesi con AnimeClick prioritario, fonti italiane e traduzione AI se necessaria. Protezione dei nomi italiani/incerti e dei blocchi; risultati ancora inglesi rifiutati. Icona cinema nella sidebar Jellyfin 12, collisione OpenAPI risolta e installazione da manifest ripristinata come metodo consigliato nel README.

- [Note complete](docs/releases/1.2.1.0.md)
- [Audit e verifiche](docs/AUDIT-1.2.1.md)

## 1.2.0.0

AnimeClick come unico provider remoto per le librerie anime: identificazione TMDB verificata, completamento dei metadati senza altri plugin e immagini integrate Fanart → TMDB → AnimeClick. Chiavi Fanart personale e di progetto nelle impostazioni, con link e verifica. Numerazione, campi bloccati e immagini esistenti conservati.

- [Note complete](docs/releases/1.2.0.0.md)
- [Audit e verifiche](docs/AUDIT-1.2.md)

## 1.1.2.0

AnimeClick primo campo per campo: i valori generici lasciano spazio alle fonti configurate. Recupero italiano e traduzione dall’inglese anche per serie, film, generi e tag, senza mescolare titolo e trama. Le immagini conservano la precedenza dei provider ad alta risoluzione; nessun aggiornamento immagini o riparazione viene avviato dall’installazione.

- [Note complete](docs/releases/1.1.2.0.md)
- [Audit e verifiche](docs/AUDIT-1.1.2.md)

## 1.1.1.0

Titoli episodio mancanti: ricerca su TheTVDB e TMDB configurati dopo AnimeClick, precedenza all’italiano e traduzione dei soli titoli inglesi con il profilo AI esistente. Verifica degli ID esterni, conservazione dei titoli compilati e indicazione della fonte nell’avanzamento.

- [Note complete](docs/releases/1.1.1.0.md)
- [Audit e verifiche](docs/AUDIT-1.1.1.md)

## 1.1.0.0

La tua libreria con analisi automatica, viste separate, avanzamento reale e interruzione dei lavori. Recupero dei soli titoli mancanti con protezione dei metadati esistenti. Abbinamenti della comunità e invio automatico delle correzioni su GitHub, entrambi facoltativi e disattivati di default.

- [Note complete](docs/releases/1.1.0.0.md)
- [Audit e verifiche](docs/AUDIT-1.1.md)

## 1.0.0.0

Major per Jellyfin 12 e .NET 10: frontend ridisegnato, identificazione automatica più conservativa, identificazione manuale integrata nel refresh Jellyfin e correzioni di cache, concorrenza, rete e traduzioni.

- [Note complete](docs/releases/1.0.0.0.md)
- [Audit e verifiche](docs/AUDIT-1.0.md)

Per la storia precedente consulta le [release 0.x](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).
