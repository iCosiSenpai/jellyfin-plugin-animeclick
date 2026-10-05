# Rilascio del plugin

Il repository del codice e quello del catalogo sono distinti. Per una distribuzione tramite catalogo, la release non è completa finché il catalogo pubblico non punta a un asset pubblico verificato. Una release GitHub destinata all’installazione manuale può invece lasciare invariato il catalogo, quando questo è lo scopo richiesto.

## Prima del tag

1. Aggiornare la versione a quattro parti nel progetto, negli asset di configurazione e nel User-Agent predefinito. La CI verifica l’allineamento.
2. Documentare la release in `docs/releases/<versione>.md`, i requisiti nel README e le verifiche nel rapporto di audit.
3. Eseguire test backend contro la versione minima e le versioni Jellyfin supportate. Aggiornare la matrice CI quando necessario.
4. Eseguire i test browser, build dell’harness, audit delle dipendenze e una prova runtime isolata sui server dichiarati compatibili.
5. Verificare `git diff --check`, contenuto del diff, assenza di credenziali e file locali. Fare commit e push, attendere la CI di `main`.
6. Creare e pubblicare il tag `vMAJOR.MINOR.PATCH.REVISION`. Non spostare un tag pubblicato.

Il tag genera una **draft** dopo il successo dei test backend e web. Il pacchetto è compilato esplicitamente contro `JellyfinVersion=12.0.0`, indipendentemente dall’ultima variante della matrice.

## Verifica e pubblicazione

1. Scaricare `AnimeClick.Plugin.zip` dalla draft tramite un account autenticato.
2. Verificare l’integrità ZIP e i cinque file previsti: `AnimeClick.Plugin.dll`, `HtmlAgilityPack.dll`, `meta.json`, `LICENSE`, `NOTICE`. Il `name` di `meta.json` deve essere quello del plugin («AnimeClick Plugin»), non quello del pacchetto nel catalogo: Jellyfin raggruppa le versioni per nome e, se differiscono, dopo un aggiornamento carica sia la vecchia sia la nuova (issue #2). Non distribuire le DLL Jellyfin.
3. Calcolare MD5 maiuscolo per il catalogo e SHA-256 come verifica supplementare. MD5 è il formato del catalogo Jellyfin, non una firma di autenticità.
4. Pubblicare la draft come release stabile/latest.
5. Scaricare l’asset dall’URL pubblico **senza autenticazione** e confrontare il checksum con quello dell’asset verificato.
6. Solo ora aggiungere la nuova versione a `manifest.json` in [iCosiSenpai-Plugins](https://github.com/iCosiSenpai/iCosiSenpai-Plugins). Usare il GUID esistente, la versione del pacchetto, l’ABI minima da `Directory.Build.props`, URL pubblico, MD5 e timestamp UTC. Conservare le versioni precedenti.
7. Validare JSON, ordine delle versioni e URL; commit e push del catalogo. Controllare infine il manifest pubblico e il suo asset.

Se il catalogo non può essere aggiornato, dichiarare esplicitamente che la release GitHub esiste ma la distribuzione tramite catalogo non è completata. Non aggirare un asset non pubblico inserendolo ugualmente nel manifest.

### Release da 1.1.0.0 a 1.5.0.0: installazione sul NAS, catalogo invariato

Per esplicita richiesta del proprietario, pubblicare e verificare il pacchetto GitHub seguendo i passaggi 1–5; **non eseguire i passaggi 6–7** e non modificare il repository `iCosiSenpai-Plugins`. Installare soltanto il plugin AnimeClick sul NAS autorizzato, seguendo il registro operativo `/volume1/docker/AGENT.md`; il proprietario ha autorizzato questo riferimento in sostituzione del file `START_HERE` mancante.

Salvare un riferimento in sola lettura della libreria, fermare soltanto Jellyfin, conservare copie di lavoro di plugin/configurazione/database, spostare le vecchie DLL fuori dalla directory caricata dal server, installare il pacchetto verificato e riavviare lo stesso container. Verificare caricamento, API amministrative, configurazione conservata (salvo le preferenze esplicitamente richieste dal proprietario) e uguaglianza dei campi della libreria. Non avviare riparazioni massive sulla libreria di produzione come prova dell’installazione. Registrare l’esito nel documento operativo del NAS e rimuovere le copie di lavoro solo dopo verifica positiva.

L’installazione o l’aggiornamento di un server in produzione è un’attività separata: non fa parte della pubblicazione e richiede le istruzioni operative di quell’ambiente.
