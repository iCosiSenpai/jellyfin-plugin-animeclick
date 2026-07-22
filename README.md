<div align="center">
  <img src="assets/banner.png" alt="Banner AnimeClick Metadata Plugin per Jellyfin" width="100%" />

  # AnimeClick Metadata Plugin for Jellyfin

  [![GitHub Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square&color=blue)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-plugin-7b68ee?style=flat-square)](https://jellyfin.org/)
  [![License](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick?style=flat-square)](LICENSE)
</div>

Porta in Jellyfin i metadati italiani degli anime presenti su [AnimeClick.it](https://www.animeclick.it/): titoli, trame, generi, cast, staff e informazioni sugli episodi.

Il plugin segue una regola semplice: **usa AnimeClick quando possiede un dato italiano affidabile e lascia agli altri provider i campi mancanti**. Se un abbinamento non è sicuro, preferisce non modificare l'episodio invece di assegnare informazioni sbagliate.

> **Nota**: Questo plugin utilizza scraping etico del sito AnimeClick, autorizzato dallo staff. Tutte le richieste sono rate-limited e i dati vengono cacheati localmente.

## Cosa ottieni

- Titolo e trama italiani di serie e film.
- Generi, tag, cast, staff, nazionalità, trailer e sigle quando disponibili.
- Titoli italiani degli episodi.
- Sinossi italiane degli episodi, cercate prima su AnimeClick.
- Fonti di riserva opzionali: TheTVDB, TMDB e traduzione con Ollama Cloud.
- Gestione prudente di stagioni, special, OVA e numerazioni insolite.
- Locandina AnimeClick come alternativa quando i provider di immagini non trovano un poster migliore.

Non è necessario configurare API key per usare i dati disponibili direttamente su AnimeClick.

## Avvio rapido

1. [Installa il plugin](#installazione) e riavvia Jellyfin.
2. Abilita AnimeClick nella libreria che contiene gli anime.
3. Metti **AnimeClick per primo tra i provider dei metadati**.
4. Per le immagini, lascia prima i provider con poster ad alta risoluzione e metti **AnimeClick per ultimo**.
5. Aggiorna i metadati della serie.
6. Se vuoi più sinossi episodio, apri la scheda **Sinossi episodi** del plugin e aggiungi facoltativamente TheTVDB, TMDB o Ollama.

Per la maggior parte delle serie non servono altre impostazioni.

## Titoli e sinossi degli episodi

Titolo e sinossi sono due dati indipendenti.

### Titolo episodio

Il titolo viene letto dalla lista episodi di AnimeClick. Se AnimeClick mostra soltanto un nome generico come `Episodio 12`, `Episode 12` o `Ep. 12`, il plugin lo ignora e lascia il campo disponibile agli altri provider.

### Sinossi episodio

La sinossi viene letta dalla pagina della singola puntata. Il plugin usa soltanto la descrizione editoriale dell'episodio:

- una descrizione italiana reale viene importata;
- un campo vuoto viene ignorato;
- un segnaposto completo come `Episodio 12` viene ignorato;
- i commenti degli utenti presenti più in basso nella pagina non vengono mai usati come sinossi.

Una frase reale non viene scartata soltanto perché contiene la parola “episodio”: il filtro interviene quando tutto il testo è un semplice segnaposto.

### Ordine esatto delle fonti

Quando attivi **Sinossi episodi**, il plugin prova nell'ordine:

| Priorità | Fonte | Cosa cerca |
|---:|---|---|
| 1 | AnimeClick | Sinossi italiana della pagina episodio |
| 2 | TheTVDB `ita` | Sinossi italiana già pronta |
| 3 | TMDB `it-IT` | Sinossi italiana già pronta |
| 4 | TMDB `en-US` | Sinossi inglese da tradurre |
| 5 | TheTVDB `eng` | Seconda possibilità in inglese |
| 6 | Ollama Cloud | Traduce in italiano il testo inglese trovato |

Quindi sì: **le sinossi inglesi arrivano prima da TMDB e, se mancano, da TheTVDB**. Ollama non cerca informazioni e non inventa una trama; traduce soltanto una sinossi inglese già esistente.

Se AnimeClick possiede già la sinossi, le altre fonti non vengono contattate per quella puntata. Se nessuna fonte produce un testo valido, il campo rimane invariato.

## Installazione

### Metodo consigliato: catalogo Jellyfin

1. In Jellyfin apri **Dashboard → Plugin → Repository**.
2. Aggiungi questo indirizzo:

   ```text
   https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
   ```

3. Apri il catalogo dei plugin.
4. Cerca **AnimeClick Metadata** e installalo.
5. Riavvia Jellyfin.

La pagina della [release più recente](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest) indica la compatibilità e le eventuali note importanti.

### Installazione manuale

Usa questo metodo soltanto se il catalogo non è disponibile.

1. Scarica `AnimeClick.Plugin.zip` dalla [release più recente](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest).
2. Ferma Jellyfin.
3. Crea nella cartella dei plugin una nuova directory chiamata `AnimeClick Metadata_<VERSIONE>`.
4. Estrai al suo interno i file contenuti nello ZIP.
5. Avvia nuovamente Jellyfin.

Percorsi comuni della cartella plugin:

- Docker: `/config/plugins/`
- Linux: `~/.local/share/jellyfin/plugins/`
- Windows: `%APPDATA%\jellyfin\plugins\`

Non lasciare due versioni di AnimeClick contemporaneamente nella cartella dei plugin.

## Aggiornamento e ritorno alla versione precedente

### Installazione dal catalogo

1. Apri **Dashboard → Plugin**.
2. Installa l'aggiornamento quando viene proposto.
3. Riavvia Jellyfin.
4. Controlla la versione nella pagina del plugin.

Le impostazioni salvate, comprese le API key, normalmente restano disponibili dopo l'aggiornamento.

### Installazione manuale

1. Scarica il nuovo ZIP.
2. Ferma Jellyfin.
3. Conserva temporaneamente la vecchia cartella del plugin.
4. Estrai la nuova versione in una cartella separata `AnimeClick Metadata_<NUOVA_VERSIONE>`.
5. Sposta la vecchia cartella fuori dalla directory dei plugin.
6. Riavvia Jellyfin e controlla che il plugin venga caricato.

Se qualcosa non funziona, ferma Jellyfin, rimuovi la cartella nuova, rimetti quella precedente e riavvia. Le modifiche specifiche di ogni versione sono elencate nelle [GitHub Releases](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases).

## Configurazione consigliata

### Uso semplice, senza servizi esterni

È sufficiente:

- lasciare attivi i metadati AnimeClick desiderati;
- attivare **Sinossi episodi** se vuoi usare anche le descrizioni presenti nelle pagine episodio;
- non inserire alcuna API key.

In questa modalità il plugin usa soltanto AnimeClick.

### Più copertura per le sinossi

Puoi aggiungere una o più fonti:

- **TheTVDB**: cerca prima il testo italiano `ita` e, se serve Ollama, anche quello inglese `eng`;
- **TMDB**: cerca il testo italiano `it-IT` e poi quello inglese `en-US`;
- **Ollama Cloud**: traduce in italiano il testo inglese trovato su TMDB o TheTVDB.

Le fonti sono facoltative. Puoi usare AnimeClick da solo, AnimeClick con una sola API esterna oppure la catena completa.

## Tutorial Ollama Cloud

### Che cos'è e perché può servire

[Ollama](https://ollama.com/) permette di usare modelli linguistici. Questo plugin utilizza **Ollama Cloud** soltanto come traduttore, quando AnimeClick, TheTVDB e TMDB non hanno una sinossi italiana ma TMDB o TheTVDB ne possiedono una inglese.

Ollama:

- non traduce i titoli degli episodi;
- non crea trame dal nulla;
- non viene chiamato se è già disponibile una sinossi italiana;
- non richiede una GPU o l'installazione di Ollama sul server Jellyfin.

### Cosa serve

- Un account [Ollama](https://ollama.com/).
- Una [API key Ollama](https://ollama.com/settings/keys).
- Una API key [TMDB](https://developer.themoviedb.org/docs/getting-started) oppure [TheTVDB](https://thetvdb.com/dashboard), perché Ollama ha bisogno di una sinossi inglese da tradurre.

Disponibilità dei modelli, limiti ed eventuali costi dipendono dal piano Ollama e possono cambiare: controlla sempre il tuo account.

### Configurazione passo per passo

1. Accedi a [ollama.com](https://ollama.com/).
2. Apri la pagina delle [API key](https://ollama.com/settings/keys), crea una nuova chiave e conservala in modo sicuro.
3. In Jellyfin apri **Dashboard → Plugin → AnimeClick Plugin → Sinossi episodi**.
4. Attiva **Sinossi degli episodi**.
5. Configura TMDB, TheTVDB oppure entrambi.
6. Inserisci nella sezione Ollama:
   - **Endpoint:** `https://ollama.com/api/chat`
   - **Modello:** `gemma4:31b-cloud`
   - **API key:** la chiave appena creata
7. Salva.
8. Usa i pulsanti di verifica per controllare prima TMDB o TheTVDB e poi Ollama.
9. Prova una breve traduzione nella sezione di anteprima.
10. Aggiorna i metadati di un episodio.

Le guide ufficiali sono disponibili nelle pagine [Ollama Cloud](https://docs.ollama.com/cloud) e [autenticazione API](https://docs.ollama.com/api/authentication).

### Perché a volte servono due aggiornamenti

La traduzione viene eseguita senza bloccare la scansione della libreria:

1. il primo aggiornamento trova il testo inglese e avvia la traduzione;
2. la traduzione viene salvata;
3. un aggiornamento successivo applica la sinossi italiana.

Se la sinossi non compare subito, attendi qualche momento e aggiorna nuovamente i metadati dell'episodio.

### Privacy

Quando usi Ollama, il plugin invia tramite HTTPS la sinossi inglese, le istruzioni di traduzione e il nome del modello. Non invia video, file della libreria o credenziali Jellyfin.

La chiave Ollama viene salvata nella configurazione di Jellyfin: proteggi la cartella di configurazione e i relativi backup.

## Numerazioni gestite

Le pagine AnimeClick e le cartelle Jellyfin non dividono sempre una serie nello stesso modo. Il plugin confronta le informazioni disponibili e gestisce, tra gli altri, questi casi:

| Caso | Comportamento |
|---|---|
| Una lista continua di episodi | Ricostruisce l'ordine senza supporre stagioni inesistenti |
| Stagioni separate o numerazione che riparte da 1 | Abbina la puntata alla stagione corretta |
| Stagioni di lunghezza diversa, ad esempio 13 + 11 | Usa i confini reali quando sono disponibili |
| Una sola stagione Jellyfin che unisce più gruppi AnimeClick | Può seguire l'ordine completo verificato |
| Episodio 0, OVA, OAV, OAD, ONA, special, recap, bonus ed extra | Li mantiene separati dagli episodi normali |
| Numeri come `12.5`, `12A` o `12B` | Richiede informazioni sufficienti e non li forza su E12 |
| Un file che contiene E01-E02 | Lo abbina soltanto a un intervallo compatibile |
| Numeri duplicati o dati contraddittori | Non modifica l'episodio finché il match non è sicuro |

Special e OVA non fanno slittare i titoli delle puntate normali. Se una serie ha una struttura eccezionale, nella scheda **Strumenti** è disponibile un override avanzato; usalo soltanto dopo aver verificato il caso o chiesto supporto.

## Risoluzione dei problemi

### La serie non viene trovata

- Controlla che AnimeClick sia abilitato nella libreria anime.
- Cerca la serie su AnimeClick e usa il numero presente nel suo indirizzo per l'identificazione manuale.
- Verifica che Jellyfin possa raggiungere `animeclick.it`.

### Il titolo di un episodio rimane vuoto

AnimeClick potrebbe mostrare soltanto un titolo generico, oppure la numerazione potrebbe essere ambigua. In entrambi i casi il plugin lascia intenzionalmente il campo invariato per evitare un titolo errato.

### La sinossi non compare

1. Controlla che **Sinossi episodi** sia attivo.
2. Prova la funzione **Pipeline reale** nella pagina del plugin.
3. Se usi servizi esterni, esegui i relativi pulsanti di verifica.
4. Se la fonte è inglese e usi Ollama, attendi e aggiorna una seconda volta.
5. Ricorda che alcune puntate non hanno una sinossi in nessuna fonte.

### I titoli sono spostati

Non forzare subito un override. Apri una [segnalazione](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues) indicando:

- indirizzo della serie AnimeClick;
- stagione ed episodio mostrati da Jellyfin;
- nome del file, senza percorsi personali;
- risultato atteso;
- log utili dopo aver rimosso dati sensibili.

### Il poster AnimeClick non viene usato

È normale se un provider precedente ha già trovato un'immagine migliore. AnimeClick è pensato come fonte di riserva per le immagini.

## Scraping autorizzato e uso corretto

I metadati sono forniti da **AnimeClick.it**, gestito dall'associazione culturale no-profit Associazione NewType Media.

Questo plugin non è affiliato con AnimeClick. Lo scraping è stato autorizzato dallo staff di AnimeClick per uso non commerciale.

Per rispettare il sito e l'autorizzazione ricevuta, il plugin:

- limita la frequenza delle richieste;
- conserva localmente i risultati per evitare richieste ripetute;
- recupera soltanto le informazioni necessarie ai metadati;
- non importa i commenti degli utenti come contenuto editoriale;
- non deve essere usato per raccolte massive o finalità commerciali.

Se modifichi o riutilizzi il progetto, mantieni queste protezioni e rispetta le condizioni delle fonti coinvolte.

## Fonti e riconoscimenti

| Servizio | Ruolo nel plugin |
|---|---|
| [AnimeClick.it](https://www.animeclick.it/) | Fonte italiana principale, comprese le sinossi episodio quando presenti |
| [TheTVDB](https://thetvdb.com/) | Sinossi episodio italiane e inglesi opzionali |
| [TMDB](https://www.themoviedb.org/) | Sinossi episodio italiane e inglesi opzionali |
| [Ollama](https://ollama.com/) | Traduzione cloud opzionale dall'inglese all'italiano |

**TheTVDB:** Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).

This product uses the TMDB API but is not endorsed or certified by TMDB.

## Versioni del README

Il badge **GitHub Release** in cima alla pagina mostra automaticamente la versione pubblica più recente. **Non è necessario cambiare il numero nel README a ogni release**: qui vengono usati il link `/releases/latest` e il segnaposto `<VERSIONE>`.

Il README va aggiornato quando cambiano funzioni, requisiti, compatibilità o procedura di installazione. Il numero reale della release viene gestito nei file tecnici del progetto, nel catalogo e nel tag Git.

## Supporto e progetto

- [Segnala un problema o proponi un caso reale](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues)
- [Consulta le release](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases)
- [Sostieni il progetto su Buy Me a Coffee](https://buymeacoffee.com/iCosiSenpai)
- [Fai una donazione con PayPal](https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ)

## Licenza

[GNU GPL v3](LICENSE)
