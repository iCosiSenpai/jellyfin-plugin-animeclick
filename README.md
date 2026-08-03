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
- Fonti di riserva opzionali: TheTVDB, TMDB e traduzione AI (Ollama, OpenAI, Claude, Gemini, Mistral, Groq, DeepSeek, OpenRouter e altri).
- Gestione prudente di stagioni, special, OVA e numerazioni insolite.
- Una scheda **Libreria** che analizza la copertura dei titoli e spiega, serie per serie, perché ne manca uno.
- Locandina AnimeClick come alternativa quando i provider di immagini non trovano un poster migliore.

Non è necessario configurare API key per usare i dati disponibili direttamente su AnimeClick.

## Avvio rapido

1. [Installa il plugin](#installazione) e riavvia Jellyfin.
2. Abilita AnimeClick nella libreria che contiene gli anime.
3. Metti **AnimeClick per primo tra i provider dei metadati**.
4. Per le immagini, lascia prima i provider con poster ad alta risoluzione e metti **AnimeClick per ultimo**.
5. Aggiorna i metadati della serie.
6. Se vuoi più sinossi episodio, apri la scheda **Sinossi episodi** del plugin e aggiungi facoltativamente TheTVDB, TMDB o un servizio AI.

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
| 6 | Traduzione AI | Traduce in italiano il testo inglese trovato |

Quindi sì: **le sinossi inglesi arrivano prima da TMDB e, se mancano, da TheTVDB**. L'AI non cerca informazioni e non inventa una trama; traduce soltanto una sinossi inglese già esistente.

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

- **TheTVDB**: cerca prima il testo italiano `ita` e, se serve una traduzione, anche quello inglese `eng`;
- **TMDB**: cerca il testo italiano `it-IT` e poi quello inglese `en-US`;
- **Traduzione AI**: traduce in italiano il testo inglese trovato su TMDB o TheTVDB.

Le fonti sono facoltative. Puoi usare AnimeClick da solo, AnimeClick con una sola API esterna oppure la catena completa.

## Traduzione AI

### Che cos'è e perché può servire

Il plugin può tradurre in italiano una sinossi che esiste **soltanto in inglese**, quando AnimeClick, TheTVDB e TMDB non ne hanno una italiana ma TMDB o TheTVDB ne possiedono una inglese.

La traduzione AI:

- non traduce i titoli degli episodi;
- non crea trame dal nulla;
- non viene chiamata se è già disponibile una sinossi italiana;
- costa una richiesta per sinossi, una volta sola: il risultato resta in cache.

### Quale servizio scegliere

Il servizio si sceglie da un menu nella scheda **Sinossi episodi**. Sono già configurati:

| Servizio | Chiave | Note |
|---|---|---|
| Ollama Cloud | sì | Modelli con suffisso `-cloud`; un modello per volta sul piano gratuito |
| Ollama in casa | no | Nessuna quota; se il demone ha già fatto `ollama signin` usa lui l'abbonamento |
| OpenAI | sì | Chiave della piattaforma API, a consumo |
| Anthropic Claude | sì | Chiave della console, a consumo |
| Google Gemini | sì | Endpoint compatibile OpenAI ufficiale, con piano gratuito a limiti |
| Mistral | sì | Buona resa sulle lingue europee |
| Groq | sì | Molto rapido quando le sinossi da tradurre sono tante |
| DeepSeek | sì | Tra i più economici sui testi brevi |
| OpenRouter | sì | Una sola chiave per i modelli di molti fornitori |
| Together AI | sì | Modelli aperti ospitati |
| xAI Grok | sì | Chiave della console xAI |
| LM Studio in casa | no | Server locale di LM Studio |
| Personalizzato | opzionale | Qualunque servizio compatibile OpenAI: LiteLLM, vLLM, llama.cpp, un proxy aziendale |

Il **nome del modello non ha un valore predefinito**, ed è voluto: i fornitori ritirano e rinominano i modelli continuamente, quindi il plugin chiede l'elenco al servizio. Premi **Elenca modelli** e scegli dal campo.

### Perché non c'è il "login con ChatGPT" o "login con Claude"

Sarebbe comodo usare un abbonamento già pagato, ma non è permesso:

- l'abbonamento ChatGPT [non include l'accesso all'API](https://help.openai.com/en/articles/6950777-what-is-chatgpt-plus), che è fatturato a parte; «Sign in with ChatGPT» esiste ma copre i client Codex, non chiamate API di altri programmi;
- Anthropic ha [chiarito nei termini di servizio](https://www.theregister.com/2026/02/20/anthropic_clarifies_ban_third_party_claude_access/) che i token OAuth dei piani Free, Pro e Max valgono solo per Claude Code e Claude.ai: usarli altrove viola le condizioni d'uso.

Esistono progetti che aggirano il vincolo riusando le credenziali dei client ufficiali. Questo plugin non lo fa: metterebbe a rischio l'account di chi lo installa e si romperebbe al primo cambiamento lato fornitore.

L'unico modo legittimo per non mettere una chiave in Jellyfin è far tenere le credenziali a un servizio in casa — Ollama con `ollama signin`, o un gateway come LiteLLM — e puntare il plugin a quello.

### Cosa serve

- Una chiave del servizio scelto (non serve per un servizio in casa).
- Una API key [TMDB](https://developer.themoviedb.org/docs/getting-started) oppure [TheTVDB](https://thetvdb.com/dashboard), perché la traduzione ha bisogno di una sinossi inglese da tradurre.

Disponibilità dei modelli, limiti ed eventuali costi dipendono dal servizio e cambiano nel tempo: controlla sempre il tuo account.

### Configurazione passo per passo

1. In Jellyfin apri **Dashboard → Plugin → AnimeClick Plugin → Sinossi episodi**.
2. Attiva **Sinossi degli episodi**.
3. Configura TMDB, TheTVDB oppure entrambi.
4. Scegli il **Servizio AI** dal menu: endpoint ed eventuale link per creare la chiave vengono compilati da soli.
5. Incolla la **API key** del servizio, se ne richiede una.
6. Premi **Elenca modelli** e scrivi nel campo **Modello** per scegliere.
7. Salva.
8. Usa i pulsanti di verifica: prima TMDB o TheTVDB, poi **Verifica AI**.
9. Prova una breve traduzione nella sezione di anteprima.
10. Aggiorna i metadati di un episodio.

### Un servizio in casa

Un endpoint sulla tua rete è ammesso anche in HTTP semplice — `http://ollama:11434/api/chat`, `http://nas.local:11434/api/chat`, un indirizzo privato della LAN — perché pretendere TLS avrebbe significato un certificato per un indirizzo di rete locale. Verso un host pubblico l'HTTPS resta obbligatorio, e **la chiave non viene mai inviata su una connessione in chiaro**: se ne hai configurata una e l'endpoint è HTTP, viene scartata con un avviso nel log.

### Perché a volte servono due aggiornamenti

La traduzione viene eseguita senza bloccare la scansione della libreria:

1. il primo aggiornamento trova il testo inglese e avvia la traduzione;
2. la traduzione viene salvata;
3. un aggiornamento successivo applica la sinossi italiana.

Se la sinossi non compare subito, attendi qualche momento e aggiorna nuovamente i metadati dell'episodio.

### Privacy

Quando usi la traduzione AI, il plugin invia al servizio scelto la sinossi inglese, le istruzioni di traduzione e il nome del modello. Non invia video, file della libreria o credenziali Jellyfin.

La chiave del servizio AI viene salvata nella configurazione di Jellyfin: proteggi la cartella di configurazione e i relativi backup.

## Perché mancano dei titoli: la scheda Libreria

Un titolo assente sembra identico qualunque ne sia la causa, ma le cause chiedono reazioni opposte. La scheda **Libreria** le distingue leggendo soltanto le schede già in cache, quindi l'analisi non produce richieste ad AnimeClick.

| Diagnosi | Che cosa significa | Cosa fare |
|---|---|---|
| Completa | Tutti gli episodi hanno un titolo | Niente |
| Basta un ricontrollo | Il titolo c'è già su AnimeClick, Jellyfin non l'ha ancora scritto | **Esegui ora il ricontrollo** |
| Titolo non pubblicato | Episodio abbinato, ma la scheda non ha ancora il titolo italiano | Attendere: il ricontrollo settimanale lo prenderà |
| Scheda senza titoli | AnimeClick elenca gli episodi senza titolo | Nulla è recuperabile |
| Numerazione ripetuta | La scheda ripete gli stessi numeri, di solito uno spin-off nella stessa tabella | Segnalare la serie |
| Riga scomparsa | L'identità salvata punta a una riga che la scheda non contiene più | **Svuota cache** su quella serie |
| Scheda di stagione da indicare | La stagione sta su un'altra scheda e la traversata non riesce a dimostrare quale | Scrivere l'ID di quel cour nel campo AnimeClick della stagione |
| Nessuna identità | Nessun abbinamento scritto su quell'episodio | **Esegui ora il ricontrollo**; se resiste, segnalare |
| Non identificata | La serie non ha un ID AnimeClick | Identificarla |

Per ogni serie ci sono tre azioni: **Analizza** rilegge la scheda dal sito e rifà la traversata delle stagioni per dare un verdetto definitivo, **Svuota cache** invalida le schede memorizzate di quella serie, e per una serie non identificata un pulsante porta l'ID nella scheda Strumenti.

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

Apri la scheda **Libreria** e premi **Analizza la libreria**: ti dice quale delle cause è in gioco, senza indovinare. Le più comuni sono una scheda AnimeClick che elenca gli episodi senza titolo — e allora non c'è nulla da recuperare — oppure una numerazione ambigua, davanti alla quale il plugin lascia intenzionalmente il campo invariato per evitare un titolo errato.

### La sinossi non compare

1. Controlla che **Sinossi episodi** sia attivo.
2. Prova la funzione **Pipeline reale** nella pagina del plugin.
3. Se usi servizi esterni, esegui i relativi pulsanti di verifica.
4. Se la fonte è inglese e usi la traduzione AI, attendi e aggiorna una seconda volta.
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
| [Ollama](https://ollama.com/) e gli altri servizi AI | Traduzione opzionale dall'inglese all'italiano |

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
