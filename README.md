<div align="center">
  <img src="assets/banner.png" alt="AnimeClick Metadata Plugin per Jellyfin" width="100%" />

  # AnimeClick Metadata per Jellyfin

  [![Release](https://img.shields.io/github/v/release/iCosiSenpai/jellyfin-plugin-animeclick?label=release)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest)
  [![Build](https://img.shields.io/github/actions/workflow/status/iCosiSenpai/jellyfin-plugin-animeclick/build.yml?branch=main)](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/actions/workflows/build.yml)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-12.0%2B-7b68ee)](#compatibilità-e-aggiornamento)
  [![.NET](https://img.shields.io/badge/.NET-10-512bd4)](https://dotnet.microsoft.com/)
  [![Licenza](https://img.shields.io/github/license/iCosiSenpai/jellyfin-plugin-animeclick)](LICENSE)
</div>

Titoli, trame e puntate degli anime in italiano, direttamente nella tua libreria Jellyfin.
La fonte principale è [AnimeClick.it](https://www.animeclick.it/): **non serve una chiave API per iniziare**.

Il plugin cerca un abbinamento verificabile fra il tuo titolo e la scheda AnimeClick. Se i dati sono ambigui, lascia il campo invariato e permette di correggere l’abbinamento. Dalla **1.2.0**, AnimeClick può essere l’**unico provider remoto di metadati e immagini per le librerie anime**: integra direttamente TMDB, TheTVDB e Fanart. AnimeClick resta la prima fonte per i metadati; Fanart ha precedenza per le immagini, seguito da TMDB alla risoluzione originale.

## Inizia da qui

1. Installa la [release 1.2.0.0](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/tag/v1.2.0.0) seguendo l’installazione manuale qui sotto. Questa release non aggiorna il catalogo pubblico Jellyfin.
2. In **Preferenze**, lascia attive **Completa i metadati dalle fonti integrate** e **Immagini TMDB ad alta risoluzione**. Non serve un altro plugin TMDB.
3. In **Fonti aggiuntive**, configura la tua [chiave API TMDB](https://developer.themoviedb.org/docs/getting-started). Per le immagini Fanart, inserisci la [chiave personale Fanart](https://fanart.tv/get-an-api-key/): è sufficiente anche da sola. TheTVDB è una seconda fonte testuale facoltativa.
4. Se vuoi completare anche i testi disponibili solo in inglese, abilita e configura un servizio AI e il suo modello. Senza AI vengono usati i testi già italiani; non vengono importate trame inglesi come italiane.
5. Nelle librerie dedicate agli anime abilita **AnimeClick come unico provider remoto** per serie, stagioni, episodi e film, sia per i metadati sia per le immagini. I lettori NFO, le immagini locali e gli estrattori integrati di Jellyfin possono restare attivi. Gli altri plugin possono restare installati per le librerie non anime.
6. **Inizio** mostra dove è attivo il plugin; **La tua libreria** aiuta a trovare i campi mancanti. Prova un aggiornamento su un solo titolo, conservando le immagini e i campi bloccati.

Per il catalogo delle versioni precedenti, il repository Jellyfin resta `https://raw.githubusercontent.com/iCosiSenpai/iCosiSenpai-Plugins/main/manifest.json`.

Le normali impostazioni di Jellyfin continuano a contare: abilitazione per tipo di contenuto, ordine dei provider e blocchi dei metadati.

## Compatibilità e aggiornamento

La linea 1.x richiede **Jellyfin 12.0 o successivo**, con ABI minima `12.0.0.0` e .NET 10. La compatibilità è verificata su **12.0.0 e 12.1.0**, compilando contro entrambe le API e caricando la DLL compilata per 12.0 in entrambi i server. Le versioni future, soprattutto nuove major, richiedono ulteriori verifiche: “12+” non significa compatibilità binaria garantita per sempre.

Jellyfin 10.11 non può caricare questa major. Le release precedenti rimangono nel catalogo con la propria ABI; non installare manualmente la 1.x su un server 10.11.

Prima di aggiornare Jellyfin, segui le [indicazioni ufficiali per Jellyfin 12](https://jellyfin.org/posts/jellyfin-release-12.0/), compreso il backup di dati e configurazione. L’aggiornamento del plugin **non aggiorna il server**.

### Cosa conserva la major

- Identificativi e metadati già presenti nella libreria.
- Preferenze e chiavi salvate, comprese le configurazioni AI precedenti.
- Un’opzione già disabilitata resta disabilitata: le sinossi episodio sono attive per impostazione predefinita solo nelle nuove configurazioni.
- Le immagini non vengono cancellate dall’identificazione manuale. La sostituzione è una scelta esplicita, eseguita dal normale aggiornamento di Jellyfin.

Le cache di ricerca e quelle con chiavi precedentemente ambigue possono essere ricalcolate. Il primo aggiornamento può quindi fare più richieste. La cache non è un backup dei metadati.

### Installazione manuale

Scarica `AnimeClick.Plugin.zip` dalla [release](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases/latest), ferma Jellyfin ed estrai il pacchetto in una sola sottocartella della sua directory `plugins`. Sposta eventuali vecchie copie **fuori** dalla directory dei plugin, poi riavvia.

Non copiare le DLL del server dalla cartella di compilazione: il pacchetto contiene soltanto il plugin, HtmlAgilityPack, LICENSE e NOTICE. Per tornare alla versione precedente, ripristina plugin e configurazione compatibili con la tua versione di Jellyfin; il downgrade del server richiede il relativo backup.

## Una pagina più semplice

| Sezione | Quando usarla |
|---|---|
| **Inizio** | Controllare l’attivazione nelle librerie e capire da dove partire |
| **La tua libreria** | Analizzare i campi incompleti e avviare i recuperi disponibili |
| **Preferenze** | Scegliere quali titoli, trame e altri metadati importare |
| **Fonti aggiuntive** | Configurare TMDB, Fanart, TheTVDB e la traduzione |
| **Comunità** | Leggere abbinamenti approvati o condividere automaticamente le correzioni, con consenso esplicito |
| **Avanzate** | Correggere un abbinamento, gestire la cache o aprire la diagnostica |

Le impostazioni tecniche sono raccolte in sezioni espandibili. Le modifiche restano nel modulo finché non premi **Salva modifiche**; **Annulla** ripristina l’ultima configurazione caricata. Un errore di caricamento non rende modificabile una configurazione vuota.

### La tua libreria

La pagina analizza automaticamente i dati locali senza modificarli. **Titoli episodio** e **Trame e sinossi** hanno viste separate; apri una serie per i dettagli e usa **Aggiorna analisi** per rileggere lo stato. Gli errori parziali sono visibili e puoi riprovare.

**Sistema tutti i titoli** rilegge le liste AnimeClick e, se il titolo manca, controlla le fonti aggiuntive configurate. Completa soltanto nomi vuoti, segnaposto o derivati dal file. Conserva i titoli già compilati, anche quando differiscono da AnimeClick, e scrive solo il nome: numerazione, ID, trame e immagini restano invariati. Rispetta i blocchi, le modifiche manuali intervenute durante la ricerca e la disabilitazione del provider per gli episodi.

Il riquadro **Attività** mostra fase, avanzamento, elementi verificati, aggiornati, saltati e in errore. Tornando alla pagina ritrovi il lavoro in corso. **Interrompi** ferma il recupero; per le sinossi, i refresh già consegnati a Jellyfin possono comunque terminare. Lo stato dettagliato delle attività riguarda la sessione corrente del server e si azzera dopo un riavvio.

### Abbinamenti della comunità

Entrambe le opzioni sono **disattivate di default**. Puoi attivare la lettura degli abbinamenti approvati senza condividere nulla. Per l’**invio automatico quando correggi un abbinamento**, attiva la condivisione e configura un token del tuo account GitHub capace di creare issue nel repository del plugin.

Ogni proposta pubblica contiene soltanto il tipo dell’opera e gli ID AnimeClick/TMDB/TVDB/AniList, ed è associata al tuo account GitHub. Non contiene file, percorsi, utenti Jellyfin, URL del server, cronologia o testi della libreria. Anteprima, esportazione locale e stato degli invii sono nella pagina Comunità. La disattivazione interrompe gli invii futuri; le issue già pubblicate restano su GitHub.

Il plugin usa solo correzioni revisionate nel dataset del progetto, inizialmente vuoto, senza cambiare gli ID AnimeClick già presenti. Questo è il suo modo di apprendere dagli errori; non addestra un modello AI. Dettagli e procedura di revisione in [community/README.md](community/README.md).

### Correggere un abbinamento

In **Avanzate → Correggi un abbinamento**, cerca un film o una serie della tua libreria, selezionalo e incolla il link alla scheda AnimeClick corretta. Il plugin verifica la pagina prima di salvare l’ID; l’aggiornamento prosegue in background.

Per una stagione con una scheda separata, usa il campo **AnimeClick** nella modifica metadati della stagione in Jellyfin. Ha la precedenza sull’ID della serie per la scelta della lista episodi. Gli ID possono essere numerici o nella forma `numero/slug`.

Un titolo numerico come “86” viene cercato come titolo, non interpretato automaticamente come ID. Remake, stagioni e titoli simili richiedono riscontri sufficienti; una ricerca manuale può mostrare candidati che il riconoscimento automatico rifiuta.

## Cosa importa

Titolo italiano, trama, generi, tag, cast e staff, immagini delle persone, trailer/PV, sigle e locandina italiana. Studi, valutazioni, date e altri campi neutri vengono importati anche con le fonti integrate, attive per impostazione predefinita. Disattivandole, quei campi tornano sotto l’opzione avanzata di sovrascrittura.

Il plugin non usa i commenti degli utenti come sinossi, non rimpiazza la durata rilevata dai file e rispetta i blocchi dei metadati. La locandina AnimeClick è una riserva: Fanart e TMDB ad alta risoluzione mantengono la precedenza. Le immagini già presenti rimangono fino a un aggiornamento immagini richiesto esplicitamente. La soglia predefinita è 400 pixel di larghezza, modificabile nelle preferenze aggiuntive.

### Episodi e stagioni

Titolo e trama sono trattati separatamente. Un titolo generico come “Episodio 12” non viene importato come se fosse un titolo editoriale. Special, episodi frazionari, file multipli e numerazioni che ripartono vengono confrontati con la struttura disponibile, senza spostare automaticamente tutta la numerazione.

Le relazioni fra schede possono risolvere stagioni successive. Se non forniscono una catena sufficientemente chiara, serve l’ID della stagione. Gli override di layout restano disponibili in **Avanzate**, ma non sono necessari nell’uso ordinario.

Per un titolo assente o generico, la precedenza è AnimeClick, poi un titolo italiano su TheTVDB o TMDB, infine un titolo inglese tradotto con il servizio AI già abilitato e configurato. La traduzione usa un prompt specifico per il solo titolo, senza generare una trama o inventare un nome dall’episodio. Se manca una fonte valida, il campo resta invariato.

**Preferenze → Cerca i titoli anche nelle altre fonti** è attiva di default, ma usa soltanto i servizi configurati: TheTVDB richiede anche **Usa TheTVDB**. Servono gli ID esterni già presenti sulla serie; l’ID episodio, quando presente, deve corrispondere. Non cerca la serie per somiglianza del nome e non usa questo recupero per file contenenti più episodi. Un abbinamento esterno errato va corretto prima del recupero.

L’analisi locale non interroga queste API: **Da verificare** e **Assenti su AnimeClick** indicano cosa il recupero può controllare, senza garantire che le altre fonti abbiano un titolo. La conferma dichiara la possibile traduzione e il suo costo; l’attività mostra fonte e fase, e conta i titoli recuperati e tradotti.

### Immagini integrate

L’unico provider immagini **AnimeClick** propone nell’ordine:

1. **Fanart**, se configurato: locandine, sfondi anche 4K, loghi, banner, clearart e artwork delle stagioni.
2. **TMDB** alla risoluzione originale: poster, sfondi, loghi, stagioni, fotogrammi degli episodi e ritratti del cast.
3. **AnimeClick**: locandina italiana, con il filtro di larghezza minima configurabile.

Le immagini Fanart delle serie usano l’ID TheTVDB; quelle dei film l’ID TMDB. La chiave personale Fanart è sufficiente; una chiave di progetto è facoltativa. Crea le chiavi su [Fanart](https://fanart.tv/get-an-api-key/) e verifica la connessione nelle impostazioni. Le chiavi viaggiano nelle intestazioni dell’API Fanart e non nelle URL delle immagini; vengono accettati soltanto i CDN controllati. [Documentazione ufficiale Fanart v3.2](https://api.fanart.tv/).

L’installazione e l’analisi non sostituiscono le immagini presenti. La loro sostituzione resta una scelta esplicita nel refresh Jellyfin.

### Trame e fonti facoltative

Per ogni testo descrittivo la precedenza è AnimeClick, una fonte già italiana e infine una fonte inglese tradotta. I valori generici, come «N/D», «Trama non disponibile» o «Episode 12», non bloccano il recupero. Titolo e trama vengono risolti separatamente: un titolo valido AnimeClick resta prioritario anche se la trama arriva da un’altra fonte.

Con le fonti integrate attive, serie e film identificano direttamente l’opera su TMDB. Gli ID manuali hanno precedenza; in loro assenza si usa un ID IMDb/TheTVDB oppure un titolo o alias esatto, formato film/serie e anno, cercando soltanto risultati di animazione. Remake ambigui e identità discordanti non vengono scelti automaticamente. Gli ID TMDB/TheTVDB/IMDb verificati diventano disponibili anche agli episodi, senza un altro plugin che li aggiunga.

Titoli e trame provengono da TheTVDB/TMDB; i generi italiani da TMDB o quelli inglesi TheTVDB tradotti; i tag TMDB vengono tradotti quando mancano quelli AnimeClick. TMDB completa titolo originale, date, studio, valutazione, classificazione italiana, stato della serie, cast e trailer mancanti. Stagioni ed episodi mantengono sempre la numerazione locale; i file che contengono più episodi non ricevono dati di un singolo episodio. Nomi propri, codici dei paesi, date, numeri e ID non vengono tradotti. Tutti gli interruttori dei campi restano rispettati.

**Non occorrono altri provider Jellyfin per questa catena**, ma occorrono le chiavi delle fonti che vuoi usare e un servizio AI configurato per l’inglese. Se nessun catalogo ha il dato, o l’identità non è certa, il campo resta invariato. La modalità autonoma non promette copertura universale e non inventa metadati. Puoi disattivare le fonti integrate per tornare alla composizione con altri provider; l’opzione avanzata di precedenza dei campi neutri rimane disponibile per quel caso.

Per le sinossi episodio la precedenza è:

1. AnimeClick in italiano.
2. TheTVDB in italiano, se abilitato e configurato.
3. TMDB in italiano, se configurato.
4. Una sinossi inglese da TMDB o TheTVDB, **solo quando la traduzione AI è configurata e abilitata**.

La traduzione elabora una sinossi esistente; non è un generatore di trame. Rimane comunque un risultato automatico e può contenere errori. Nessun modello viene scelto automaticamente: seleziona il servizio, richiedi il suo elenco dei modelli e scegli quello che vuoi utilizzare.

Le traduzioni delle trame lavorano in background; titoli, generi e tag vengono tradotti durante il recupero richiesto. Quando un risultato è disponibile viene richiesto un aggiornamento mirato, che ricontrolla il testo e i blocchi prima di applicarlo. Gli esiti “in traduzione” e “nessuna fonte” evitano richieste ripetute; un cambio delle fonti configurate consente un nuovo tentativo.

Le traduzioni sono conservate in cache in base a testo, fonte, modello, endpoint, credenziale e versione del prompt. Modifiche a questi valori, pulizie della cache o scadenze possono causare nuove chiamate, anche a pagamento. La disponibilità e i prezzi dipendono dal fornitore: il plugin non garantisce quote gratuite.

## Privacy e rete

- AnimeClick riceve le ricerche e le richieste alle sue pagine. Il plugin applica cache, ritardo fra richieste e gestione dei limiti del sito.
- GitHub riceve le richieste al dataset soltanto se abiliti la lettura; riceve proposte pubbliche e la credenziale del contribuente soltanto se abiliti l’invio automatico. Il token è inviato esclusivamente a `api.github.com`, senza redirect.
- Le integrazioni facoltative ricevono i dati necessari alla loro funzione. L’AI riceve soltanto il testo da tradurre (titolo, trama, generi o tag), senza file video, percorsi, ID o informazioni sugli utenti.
- Le chiavi sono conservate nella configurazione Jellyfin: proteggi l’accesso amministrativo, i file e i backup. Non condividere configurazioni o log senza rimuovere i segreti.
- La pagina del plugin non carica immagini promozionali esterne. Le funzioni amministrative richiedono un account amministratore.
- Le immagini accettano soltanto le destinazioni AnimeClick consentite e i CDN TMDB/Fanart controllati; i redirect vengono verificati prima della richiesta successiva. Le API esterne non seguono automaticamente redirect con credenziali.
- I servizi AI pubblici richiedono HTTPS. HTTP è ammesso per destinazioni locali supportate, ma le chiavi AI non vengono inviate su HTTP. Il collegamento fra browser e Jellyfin va protetto separatamente.

## Problemi frequenti

**Non trova il titolo.** Verifica l’attivazione nella libreria e l’accesso ad AnimeClick; poi prova l’identificazione manuale. Un rifiuto automatico può essere una protezione contro un abbinamento ambiguo.

**Mancano trame o titoli episodio.** Usa **La tua libreria**. Il sito può non avere ancora pubblicato quel testo; una fonte assente non può essere ricostruita in modo affidabile. Le attività AnimeClick in **Dashboard → Attività pianificate** permettono di ricontrollare i titoli e completare le sinossi.

**La pagina non si carica dopo l’aggiornamento.** Riavvia Jellyfin, ricarica senza cache e controlla che non siano presenti due copie del plugin.

**Un campo non cambia.** Controlla i blocchi, le preferenze del plugin e l’ordine dei provider. Il recupero delle sinossi non sovrascrive una correzione italiana effettuata nel frattempo.

Per una [segnalazione](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues), indica versione server/plugin, link AnimeClick, numerazione Jellyfin, risultato atteso e log ripuliti. Non inviare chiavi, token o percorsi privati.

## Sviluppo e verifiche

Serve l’SDK .NET 10; `global.json` seleziona una feature band 10.0 installata. Per i test del frontend servono Node.js 22 e Chromium tramite Playwright.

```bash
dotnet test AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj -c Release -warnaserror
dotnet test AnimeClick.Plugin.Tests/AnimeClick.Plugin.Tests.csproj -c Release -p:JellyfinVersion=12.1.0 -warnaserror
dotnet build tools/AnimeClick.Harness/AnimeClick.Harness.csproj -c Release
npm ci --prefix tools/AnimeClick.WebTests
cd tools/AnimeClick.WebTests
npx playwright install --with-deps chromium
npm test
```

La pipeline esegue le due varianti backend, i test browser e la validazione degli abbinamenti; il pacchetto viene sempre compilato contro la versione minima. I risultati, i difetti corretti e i limiti della verifica sono nel [rapporto di audit 1.1](docs/AUDIT-1.1.md), con il [precedente audit](docs/AUDIT-1.0.md) per la major. La procedura di rilascio è in [RELEASING](docs/RELEASING.md).

`tools/AnimeClick.Harness` riusa parser e matcher reali su pagine AnimeClick o su un confronto in sola lettura con Jellyfin. La verifica runtime `tools/AnimeClick.WebTests/runtime-smoke.cjs` è riservata a **server localhost nuovi e usa-e-getta**: configura utenti di prova e modifica le preferenze del plugin. Non è uno strumento per la produzione.

## Fonti, autorizzazione e licenza

Il progetto non è affiliato con AnimeClick.it. L’autorizzazione allo scraping per questo progetto e le condizioni d’uso sono riportate in [NOTICE](NOTICE); mantieni cache e limitazione delle richieste e non usarlo per raccolte massive o commerciali.

Metadati da [AnimeClick.it](https://www.animeclick.it/), opzionalmente [TheTVDB](https://thetvdb.com/), [TMDB](https://www.themoviedb.org/) e [Fanart](https://fanart.tv/).

**TheTVDB:** Metadata provided by TheTVDB. Please consider adding missing information or [subscribing](https://thetvdb.com/subscribe).

This product uses the TMDB API but is not endorsed or certified by TMDB.

Codice sotto [GNU GPL v3](LICENSE). Le condizioni relative al nome, al logo e all’autorizzazione allo scraping sono in [NOTICE](NOTICE), incluso nello ZIP.

[Release](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/releases) · [Segnalazioni](https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues) · [Buy Me a Coffee](https://buymeacoffee.com/iCosiSenpai) · [PayPal](https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ)

Copyright (C) 2026 Alessio Cosi (iCosiSenpai)

Artwork provided by Fanart.tv.
