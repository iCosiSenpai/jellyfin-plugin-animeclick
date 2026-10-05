# AnimeClick Metadata per Jellyfin

[![Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?label=release)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest) [![Build](https://img.shields.io/github/actions/workflow/status/iCosiSenpai/jellyfin-plugin-animeclick/build.yml?branch=main)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/actions/workflows/build.yml) [![Jellyfin](https://img.shields.io/badge/Jellyfin-12.0%2B-7b68ee)](#installazione) [![Licenza](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick)](LICENSE)

<p align="center">
  <img src="assets/logo.png" alt="Logo di AnimeClick Metadata Plugin, sviluppato da iCosiSenpai" width="140" />
</p>

**Un solo plugin per gli anime in italiano:** titoli, trame, episodi e immagini nella tua libreria Jellyfin, con AnimeClick come prima fonte dei metadati.

[Installa dal catalogo](#dal-catalogo-jellyfin-consigliata) · [Configuralo](#configurazione) · [Segnala un problema](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues)

## Il plugin e le fonti integrate

Nelle librerie dedicate agli anime puoi attivare **AnimeClick come unico provider remoto di metadati e immagini**, dalla versione 1.2. Non servono i plugin separati TMDB, TheTVDB o Fanart per usare queste integrazioni.

| Fonte | A cosa serve | Cosa occorre |
|---|---|---|
| **AnimeClick** | Prima scelta per titoli, trame, generi, tag e altri metadati italiani | Nessuna chiave API |
| **TMDB** | Identificazione delle opere, completamento dei dati mancanti e immagini alla risoluzione originale | [Chiave API TMDB](https://developer.themoviedb.org/docs/getting-started) |
| **Fanart** | Locandine, sfondi anche 4K, loghi, banner e artwork delle stagioni | [Chiave personale Fanart](https://fanart.tv/get-an-api-key/), sufficiente anche da sola |
| **TheTVDB** | Fonte aggiuntiva per titoli e trame | [Chiave API TheTVDB](https://thetvdb.com/dashboard) e opzione **Usa TheTVDB** |
| **AniList** | Cast giapponese con i personaggi, studio di animazione, date, voto, trailer, copertina e banner; l'anno di ogni stagione per collegare i sequel | Nessuna chiave: opzione **Usa AniList** in Fonti |
| **Traduzione AI** | Traduzione dei testi disponibili soltanto in inglese | Servizio abilitato, modello scelto e credenziali richieste dal servizio |

I metadati vengono completati **campo per campo**: AnimeClick → altre fonti in italiano → testi inglesi tradotti. Cast, studio, date, voto e trailer: AnimeClick → AniList → TMDB. Le immagini seguono l'ordine **Fanart → TMDB → AniList → locandina AnimeClick**. Le fonti aggiuntive funzionano solo se configurate.

AniList non scrive testi (è solo in inglese) e un suo ID viene usato soltanto se tipo (film o serie) e anno coincidono con l'opera: un ID lasciato da un altro plugin che indica uno spot o un'altra stagione viene ignorato. Quando Jellyfin non sa in che anno è andata in onda una stagione, il plugin lo ricava dalla catena dei sequel di AniList, ma solo se quella stagione ha lo stesso numero di episodi della libreria.

I nomi propri, gli ID e i dati numerici non vengono tradotti. Se manca un dato o l'abbinamento è ambiguo, il campo resta invariato: il plugin non inventa titoli o trame.

## Installazione

**Il plugin funziona solo con Jellyfin 12.0 o successivo (.NET 10); verificato su Jellyfin 12.0 e 12.1.** Le vecchie versioni 0.x per Jellyfin 10.x sono obsolete: non sono più distribuite né aggiornate, e sono state tolte dal catalogo e dalle release. Per continuare a usare AnimeClick aggiorna prima Jellyfin alla 12, poi il plugin dal catalogo. La compatibilità con nuove versioni major di Jellyfin va verificata.

### Dal catalogo Jellyfin (consigliata)

1. Apri **Dashboard → Plugin → Repository** e premi **Aggiungi**.
2. Come nome inserisci **iCosiSenpai Plugins** e come URL copia il manifest ufficiale:

   ```text
   https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json
   ```

3. Salva, apri **Catalogo** e seleziona **AnimeClick Metadata**.
4. Installa l'ultima versione, riavvia il server e apri le impostazioni del plugin.

Questo è il metodo ufficiale e consigliato: Jellyfin gestisce l'installazione e gli aggiornamenti, e ogni nuova release viene pubblicata nel catalogo. Il catalogo contiene solo le versioni per Jellyfin 12, dalla 1.0 in poi. Dalla 1.6 un aggiornamento dal catalogo sostituisce la versione precedente senza lasciarla caricata accanto alla nuova.

### Installazione manuale (alternativa)

Per installare una versione specifica senza catalogo:

1. Scarica `AnimeClick.Plugin.zip` dall'[ultima release](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest).
2. Ferma Jellyfin e conserva una copia della configurazione e del plugin precedente.
3. Sposta le vecchie copie di AnimeClick **fuori dalla directory `plugins`**. Estrai lo ZIP in una sola sottocartella di quella directory.
4. Riavvia Jellyfin e apri le impostazioni del plugin dalla dashboard.

Usa il pacchetto ZIP: contiene il plugin, HtmlAgilityPack, LICENSE e NOTICE. Non copiare le DLL del server dalla cartella di compilazione.

## Configurazione

Alla prima apertura il plugin parte con un **setup guidato** di pochi passi:

1. **Fonti**: inserisci la chiave TMDB (consigliata) e, se vuoi, quella personale Fanart. Puoi verificarle subito.
2. **Preferenze**: le scelte consigliate sono già attive; spegni quello che non ti interessa.
3. **Librerie**: scegli le librerie con i tuoi anime e premi **Attiva nelle librerie scelte**. AnimeClick diventa il primo provider dei metadati di serie, stagioni, episodi e film e si aggiunge alle immagini; gli altri provider restano attivi e le altre librerie non vengono toccate. Nulla cambia senza conferma.

Puoi saltare il setup e riaprirlo quando vuoi da **Strumenti**. Quando un aggiornamento importante richiede una scelta, il plugin mostra soltanto i passi nuovi.

Dopo il setup trovi tutto nelle sezioni della pagina:

- **Preferenze**: cosa importare, raggruppato per identità italiana, episodi, arricchimento, immagini e opzioni invasive.
- **Fonti**: TMDB, Fanart, TheTVDB e traduzione AI, con stato, verifica e l'ordine con cui vengono consultate. Per tradurre dall'inglese abilita il servizio AI, premi **Elenca modelli** e scegli un modello.
- **Inizio**: lo stato delle librerie, con **Attiva AnimeClick** per quelle in cui manca. Se preferisci usare AnimeClick come **unico** provider remoto delle librerie anime, togli gli altri provider dalle impostazioni della libreria in Jellyfin; lettori NFO, immagini locali ed estrattori possono restare.

Le modifiche si salvano dalla barra **Salva** in fondo alla pagina.

Senza traduzione AI vengono usati i testi già italiani. Per le serie, Fanart usa l'ID TheTVDB; per i film, l'ID TMDB. Gli ID esterni verificati vengono recuperati dalle integrazioni, senza altri plugin.

L'installazione non avvia una riparazione della libreria. I blocchi dei metadati continuano a essere rispettati; la sostituzione delle immagini esistenti si richiede esplicitamente nel refresh Jellyfin.

## Inizio e libreria

**Inizio** apre sulla vetrina degli ultimi anime aggiornati da AnimeClick, con locandine, trama e generi, e su un cruscotto: quanta parte della collezione è già in italiano, le attività in corso, le fonti collegate, le librerie e le cose da fare.

**Libreria** mostra ogni anime con la sua locandina e lo stato di titoli e trame. L'analisi legge i metadati locali **senza modificarli**; filtri, ricerca e ordinamento portano subito a quello che manca, e un clic apre il dettaglio con stagioni, cause, episodi e azioni. **Aggiorna analisi** rilegge la situazione.

**Sistema tutti i titoli** cerca su AnimeClick e poi nelle fonti configurate. Completa nomi vuoti, generici come “Episodio 12” o derivati dal file e converte i titoli riconoscibilmente inglesi: prima cerca un titolo italiano coerente con quello originale, poi traduce quello inglese della fonte se l’AI è configurata. Se non trova una fonte coerente, può tradurre il titolo inglese già valido, evitando di prendere il titolo di un’altra stagione. Conserva i titoli italiani e quelli dalla lingua incerta. Scrive **solo il nome**, rispettando i campi bloccati e le modifiche manuali intervenute durante la ricerca: numerazione, ID, trame e immagini restano invariati.

Il riquadro **Attività** mostra fase, avanzamento e contatori degli elementi verificati, aggiornati, saltati o in errore. Puoi lasciare la pagina e ritrovare il lavoro in corso, oppure premere **Interrompi**. I refresh delle sinossi già consegnati a Jellyfin possono comunque terminare; lo stato delle attività si azzera al riavvio del server.

L'analisi indica cosa controllare, senza garantire che una fonte abbia il dato. Il recupero conserva la numerazione locale e non assegna i metadati di un singolo episodio ai file che ne contengono più di uno.

## Correggere un abbinamento

Apri **Strumenti → Correggi un abbinamento** (oppure **Correggi abbinamento** dal dettaglio di un anime in Libreria), scegli il film o la serie e cerca la scheda giusta su AnimeClick, o incolla il suo link. Il plugin verifica la scheda, salva l'ID e prosegue l'aggiornamento in background.

Se una stagione ha una scheda AnimeClick sua (un sequel, un arco finale), dopo aver scelto la serie seleziona la stagione in **Cosa correggere**: la serie conserva la propria scheda e la stagione usa quella indicata. In alternativa puoi scrivere l'ID nel campo **AnimeClick** dell'editor metadati della stagione in Jellyfin.

## Comunità

Le correzioni di ciascuno possono sistemare la libreria di tutti, **senza account e senza condividere la libreria**.

- **Ricevere**: con **Usa gli abbinamenti della comunità** il plugin scarica una volta al giorno l'elenco approvato. Vale solo per serie, film e stagioni che non hanno ancora un ID AnimeClick e non cambia mai un abbinamento esistente. Una stagione viene collegata solo se la tua libreria ha lo stesso numero di stagione **e** lo stesso numero di episodi della proposta: chi ha suddiviso la serie diversamente non riceve una scheda sbagliata.
- **Condividere**: dopo una correzione il plugin chiede **Condividi · Non ora · Non chiedere più** e mostra esattamente cosa partirebbe. Nel setup e in **Comunità** puoi scegliere tra *Chiedimi ogni volta* (predefinito), *Condividi sempre* e *Non condividere*.

Partono solo identificativi pubblici: tipo, ID AnimeClick e ID TMDB, TheTVDB o AniList; per una stagione anche il suo numero e quanti episodi contiene. Mai titoli, utenti, percorsi, file, indirizzo del server, trame o immagini. Le proposte passano dal servizio della comunità, che apre una segnalazione pubblica nel repository; chi preferisce può inviarle a proprio nome con un token GitHub (in **Comunità → Avanzate**).

Ogni proposta passa controlli automatici (la scheda esiste, tipo, anno ed episodi coerenti con TMDB) e l'approvazione del curatore del plugin prima di arrivare a tutti. Lo stato delle tue proposte è in **Comunità**, i numeri della comunità in **Inizio**. [Come funziona nel dettaglio](community/README.md).

## Problemi e assistenza

| Problema | Cosa controllare |
|---|---|
| Titolo non riconosciuto | Attivazione nella libreria (**Inizio → Librerie**); poi **Strumenti → Correggi un abbinamento** |
| Titoli o trame mancanti | **Libreria**, chiavi in **Fonti** e configurazione della traduzione |
| Un campo non cambia | Blocchi Jellyfin e preferenze del plugin; la riparazione titoli conserva i nomi italiani o dalla lingua incerta |
| Pagina vuota dopo un aggiornamento | Riavvio di Jellyfin, ricaricamento senza cache e assenza di copie duplicate del plugin |

Per [segnalare un problema](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues), indica versione di Jellyfin e del plugin, link AnimeClick, numerazione degli episodi e risultato atteso. Rimuovi chiavi, token e percorsi privati dai log.

Le chiavi vengono salvate nella configurazione amministrativa Jellyfin. La traduzione invia al servizio scelto solo i testi da tradurre, senza file o informazioni sugli utenti, e può avere costi secondo il fornitore.

## Documentazione

[Novità delle versioni](CHANGELOG.md) · [Audit e verifiche 1.2.2](docs/AUDIT-1.2.2.md) · [Procedura di rilascio](docs/RELEASING.md) · [Pipeline di test](.github/workflows/build.yml)

## Copyright, fonti e licenze

### Plugin e identità del progetto

**Copyright (C) 2026 Alessio Cosi (iCosiSenpai).** Dalla versione 1.6.0.0 il codice è distribuito sotto **[GNU AGPL v3](LICENSE)**, con i termini aggiuntivi descritti in **[NOTICE](NOTICE)**; le versioni fino alla 1.5.0.0 restano sotto GPLv3. Il plugin usa le librerie Jellyfin GPL-3.0-only, che la sezione 13 delle due licenze permette di combinare con l'AGPL. Chi modifica e distribuisce il codice deve rispettare la stessa licenza e renderne disponibili i sorgenti; con l'AGPL questo vale anche per chi fa usare una versione modificata attraverso la rete, per esempio da un server Jellyfin condiviso.

Termini aggiuntivi (sezione 7 dell'AGPLv3), in sintesi:

- **Attribuzione:** una versione modificata deve mantenere nella pagina del plugin la dicitura «Basato su AnimeClick Metadata Plugin di Alessio Cosi (iCosiSenpai)» con il collegamento a questo repository.
- **Versioni modificate riconoscibili:** nome, GUID del plugin e User-Agent devono essere propri, così le richieste di un fork verso AnimeClick.it e le altre fonti non si confondono con quelle di questo progetto; le proposte inviate al servizio della comunità non possono presentarsi come provenienti da questo plugin.
- **Marchi:** nome, logo, banner, **mascotte**, servizio della comunità e identità **iCosiSenpai** identificano questo progetto e non possono essere usati per presentare un fork come l'originale o come approvato dall'autore. I file restano ridistribuibili con il programma; un fork deve avere nome, identità visiva e mascotte propri.

### AnimeClick e autorizzazione allo scraping

<a href="https://www.animeclick.it/"><img src="assets/providers/animeclick.png" alt="Logo ufficiale AnimeClick.it" width="170" /></a>

I metadati italiani provengono da **[AnimeClick.it](https://www.animeclick.it/)**, gestito dall'associazione culturale senza fini di lucro **[Associazione NewType Media](https://www.antme.it/)**. Il marchio, il logo e i contenuti appartengono ai rispettivi titolari; il plugin non rivendica diritti su di essi e non è affiliato con AnimeClick.

Salvo diversa indicazione, i contenuti redazionali su cui l'associazione possiede i diritti sono distribuiti sotto **[CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)**. Questa licenza non si estende automaticamente a immagini, video, marchi o materiali di terzi: valgono i [termini di AnimeClick](https://www.animeclick.it/termini-di-servizio).

Lo staff AnimeClick ha autorizzato **questo progetto** allo scraping per uso non commerciale. L'autorizzazione è personale e **non trasferibile**: un fork non eredita il consenso e deve ottenere il proprio. Il plugin limita le richieste, usa la cache e non importa i commenti degli utenti come sinossi. Mantieni queste protezioni e non usarlo per raccolte massive o finalità commerciali. I dettagli dell'autorizzazione sono in **[NOTICE](NOTICE)**.

### Provider integrati e piattaforma

I loghi ufficiali sono riportati per identificare le fonti. Marchi, dati e immagini restano soggetti ai diritti dei rispettivi titolari e alle condizioni dei servizi; la licenza del plugin non li rende contenuti GPL.

| Fonte | Attribuzione e condizioni |
|---|---|
| <a href="https://www.themoviedb.org/"><img src="assets/providers/tmdb.svg" alt="Logo ufficiale TMDB" width="130" /></a> | **TMDB / The Movie Database.** This product uses the TMDB API but is not endorsed or certified by TMDB. [Attribuzione e marchio](https://developer.themoviedb.org/docs/faq) · [Termini API](https://www.themoviedb.org/api-terms-of-use) |
| <a href="https://thetvdb.com/"><picture><source media="(prefers-color-scheme: dark)" srcset="assets/providers/thetvdb-dark.png" /><img src="assets/providers/thetvdb.png" alt="Logo ufficiale TheTVDB" width="90" /></picture></a> | **© 2026 TheTVDB.com®, A Whip Media Company. All rights reserved.** Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe). [Attribuzione e condizioni API](https://www.thetvdb.com/api-information) |
| <a href="https://fanart.tv/"><img src="assets/providers/fanart.png" alt="Logo ufficiale Fanart.tv" width="48" /></a> | **Fanart.tv.** Artwork provided by Fanart.tv. I diritti sulle immagini restano ai rispettivi titolari. [Condizioni d'uso](https://fanart.tv/terms-and-conditions/) |
| [Jellyfin](https://jellyfin.org/) | **Copyright Jellyfin Contributors.** Piattaforma e librerie `Jellyfin.Controller` / `Jellyfin.Model`, sotto GPL-3.0-only. |

La traduzione AI, se attivata, usa il servizio scelto dall'utente ed è soggetta ai suoi termini. **HtmlAgilityPack** è di **ZZZ Projects Inc.**, sotto licenza MIT; **Microsoft.Extensions.*** usa licenze MIT. Le attribuzioni e il testo MIT della DLL inclusa sono conservati in **[NOTICE](NOTICE)**, distribuito anche nello ZIP del plugin.

Origine dei loghi e condizioni d'uso: **[assets/README.md](assets/README.md)**.

[Buy Me a Coffee](https://buymeacoffee.com/iCosiSenpai) · [PayPal](https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ)
