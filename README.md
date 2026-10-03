# AnimeClick Metadata per Jellyfin

[![Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?label=release)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/iCosiSenpai/jellyfin-plugin-animeclick/build.yml?branch=main)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/actions/workflows/build.yml)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.0%2B-7b68ee)](#installazione)
[![Licenza](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick)](LICENSE)

**Un solo plugin per gli anime in italiano:** titoli, trame, episodi e immagini nella tua libreria Jellyfin, con AnimeClick come prima fonte dei metadati.

[Scarica il plugin](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest) · [Configuralo](#configurazione) · [Segnala un problema](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues)

## Il plugin e le fonti integrate

Nelle librerie dedicate agli anime puoi attivare **AnimeClick come unico provider remoto di metadati e immagini**, dalla versione 1.2. Non servono i plugin separati TMDB, TheTVDB o Fanart per usare queste integrazioni.

| Fonte | A cosa serve | Cosa occorre |
|---|---|---|
| **AnimeClick** | Prima scelta per titoli, trame, generi, tag e altri metadati italiani | Nessuna chiave API |
| **TMDB** | Identificazione delle opere, completamento dei dati mancanti e immagini alla risoluzione originale | [Chiave API TMDB](https://developer.themoviedb.org/docs/getting-started) |
| **Fanart** | Locandine, sfondi anche 4K, loghi, banner e artwork delle stagioni | [Chiave personale Fanart](https://fanart.tv/get-an-api-key/), sufficiente anche da sola |
| **TheTVDB** | Fonte aggiuntiva per titoli e trame | [Chiave API TheTVDB](https://thetvdb.com/dashboard) e opzione **Usa TheTVDB** |
| **Traduzione AI** | Traduzione dei testi disponibili soltanto in inglese | Servizio abilitato, modello scelto e credenziali richieste dal servizio |

I metadati vengono completati **campo per campo**: AnimeClick → altre fonti in italiano → testi inglesi tradotti. Le immagini seguono l'ordine **Fanart → TMDB → locandina AnimeClick**. Le fonti aggiuntive funzionano solo se configurate.

I nomi propri, gli ID e i dati numerici non vengono tradotti. Se manca un dato o l'abbinamento è ambiguo, il campo resta invariato: il plugin non inventa titoli o trame.

## Installazione

**Richiede almeno Jellyfin 12.0 (.NET 10); verificato su Jellyfin 12.0 e 12.1.** Non è compatibile con Jellyfin 10.11. La compatibilità con nuove versioni major va verificata.

1. Scarica `AnimeClick.Plugin.zip` dall'[ultima release](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest).
2. Ferma Jellyfin e conserva una copia della configurazione e del plugin precedente.
3. Sposta le vecchie copie di AnimeClick **fuori dalla directory `plugins`**. Estrai lo ZIP in una sola sottocartella di quella directory.
4. Riavvia Jellyfin e apri le impostazioni del plugin dalla dashboard.

Usa il pacchetto ZIP: contiene il plugin, HtmlAgilityPack, LICENSE e NOTICE. Non copiare le DLL del server dalla cartella di compilazione.

La versione 1.2 è distribuita su GitHub; il catalogo pubblico Jellyfin contiene ancora le versioni precedenti. Per questa versione usa l'installazione manuale.

## Configurazione

1. In **Preferenze**, lascia attive **Completa i metadati dalle fonti integrate**, **Immagini TMDB ad alta risoluzione** e, se vuoi usarlo, **Preferisci le immagini Fanart**.
2. In **Fonti aggiuntive**, inserisci la chiave TMDB e le chiavi delle altre fonti desiderate. Per tradurre dall'inglese, abilita il servizio AI, carica il suo elenco di modelli e scegli un modello.
3. Premi **Salva modifiche**.
4. Nelle impostazioni delle **sole librerie anime**, seleziona AnimeClick come unico provider remoto per metadati e immagini di serie, stagioni, episodi e film. Puoi mantenere lettori NFO, immagini locali ed estrattori Jellyfin; gli altri plugin possono restare attivi nelle altre librerie.
5. Controlla l'attivazione dalla pagina **Inizio** e prova un aggiornamento su un singolo titolo.

Senza traduzione AI vengono usati i testi già italiani. Per le serie, Fanart usa l'ID TheTVDB; per i film, l'ID TMDB. Gli ID esterni verificati vengono recuperati dalle integrazioni, senza altri plugin.

L'installazione non avvia una riparazione della libreria. I blocchi dei metadati continuano a essere rispettati; la sostituzione delle immagini esistenti si richiede esplicitamente nel refresh Jellyfin.

## La tua libreria

La pagina analizza i metadati locali **senza modificarli**. Le viste **Titoli episodio** e **Trame e sinossi** mostrano cosa manca; apri una serie per i dettagli e usa **Aggiorna analisi** per rileggere la situazione.

**Sistema tutti i titoli** cerca su AnimeClick e poi nelle fonti configurate. Completa soltanto nomi vuoti, generici come “Episodio 12” o derivati dal file; conserva i titoli già compilati. Scrive **solo il nome**, rispettando blocchi e correzioni manuali: numerazione, ID, trame e immagini restano invariati.

Il riquadro **Attività** mostra fase, avanzamento e contatori degli elementi verificati, aggiornati, saltati o in errore. Puoi lasciare la pagina e ritrovare il lavoro in corso, oppure premere **Interrompi**. I refresh delle sinossi già consegnati a Jellyfin possono comunque terminare; lo stato delle attività si azzera al riavvio del server.

L'analisi indica cosa controllare, senza garantire che una fonte abbia il dato. Il recupero conserva la numerazione locale e non assegna i metadati di un singolo episodio ai file che ne contengono più di uno.

## Correggere un abbinamento

Apri **Avanzate → Correggi un abbinamento**, seleziona un film o una serie della libreria e incolla il link alla scheda AnimeClick corretta. Il plugin verifica la scheda, salva l'ID e prosegue l'aggiornamento in background.

Se una stagione ha una scheda AnimeClick distinta, inserisci il suo ID nel campo **AnimeClick** dell'editor metadati della stagione in Jellyfin: avrà precedenza sull'ID della serie per la lista episodi.

## Condividere le correzioni

In **Comunità** trovi due opzioni indipendenti, **entrambe disattivate di default**:

- **Leggere gli abbinamenti approvati**, per riconoscere opere senza un ID AnimeClick già presente.
- **Inviare automaticamente le correzioni manuali**, dopo aver abilitato la condivisione e configurato un token GitHub capace di creare issue nel repository.

Ogni invio crea una **issue pubblica associata al tuo account GitHub**, contenente solo il tipo dell'opera e gli ID AnimeClick/TMDB/TVDB/AniList. Non include file, percorsi, utenti Jellyfin, URL del server o cronologia. Puoi vedere l'anteprima e lo stato degli invii nelle impostazioni.

Il plugin usa soltanto gli abbinamenti revisionati e approvati: migliora il riconoscimento attraverso un dataset condiviso, senza addestrare un modello AI. Disabilitare la condivisione ferma gli invii futuri; le issue già pubblicate restano su GitHub. [Dettagli e revisione delle proposte](community/README.md).

## Problemi e assistenza

| Problema | Cosa controllare |
|---|---|
| Titolo non riconosciuto | Attivazione nella libreria; poi **Avanzate → Correggi un abbinamento** |
| Titoli o trame mancanti | **La tua libreria**, chiavi delle fonti e configurazione della traduzione |
| Un campo non cambia | Blocchi Jellyfin e preferenze del plugin; la riparazione titoli conserva i nomi già validi |
| Pagina vuota dopo un aggiornamento | Riavvio di Jellyfin, ricaricamento senza cache e assenza di copie duplicate del plugin |

Per [segnalare un problema](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues), indica versione di Jellyfin e del plugin, link AnimeClick, numerazione degli episodi e risultato atteso. Rimuovi chiavi, token e percorsi privati dai log.

Le chiavi vengono salvate nella configurazione amministrativa Jellyfin. La traduzione invia al servizio scelto solo i testi da tradurre, senza file o informazioni sugli utenti, e può avere costi secondo il fornitore.

## Documentazione e crediti

[Novità delle versioni](CHANGELOG.md) · [Audit e verifiche 1.2](docs/AUDIT-1.2.md) · [Procedura di rilascio](docs/RELEASING.md) · [Pipeline di test](.github/workflows/build.yml)

Codice sotto **[GNU GPL v3](LICENSE)**. Il progetto non è affiliato con AnimeClick.it. L'autorizzazione allo scraping riguarda questo progetto, per uso non commerciale, e non è trasferibile ai fork: condizioni, marchi e licenze delle dipendenze sono in **[NOTICE](NOTICE)**. Il plugin mantiene cache e limiti alle richieste e non importa i commenti degli utenti come sinossi; non usarlo per raccolte massive o commerciali.

Metadati principali da [AnimeClick.it](https://www.animeclick.it/).

Metadata provided by [TheTVDB](https://thetvdb.com/). Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).

This product uses the TMDB API but is not endorsed or certified by TMDB.

Artwork provided by [Fanart.tv](https://fanart.tv/).

[Buy Me a Coffee](https://buymeacoffee.com/iCosiSenpai) · [PayPal](https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ)

Copyright (C) 2026 Alessio Cosi (iCosiSenpai)
