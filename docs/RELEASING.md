# Rilascio del plugin

Il repository del codice e quello del catalogo sono distinti. La release non è completa finché il catalogo pubblico non punta a un asset pubblico verificato.

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
2. Verificare l’integrità ZIP e i quattro file previsti: `AnimeClick.Plugin.dll`, `HtmlAgilityPack.dll`, `LICENSE`, `NOTICE`. Non distribuire le DLL Jellyfin.
3. Calcolare MD5 maiuscolo per il catalogo e SHA-256 come verifica supplementare. MD5 è il formato del catalogo Jellyfin, non una firma di autenticità.
4. Pubblicare la draft come release stabile/latest.
5. Scaricare l’asset dall’URL pubblico **senza autenticazione** e confrontare il checksum con quello dell’asset verificato.
6. Solo ora aggiungere la nuova versione a `manifest.json` in [iCosiSenpai-Plugins](https://github.com/iCosiSenpai/iCosiSenpai-Plugins). Usare il GUID esistente, la versione del pacchetto, l’ABI minima da `Directory.Build.props`, URL pubblico, MD5 e timestamp UTC. Conservare le versioni precedenti.
7. Validare JSON, ordine delle versioni e URL; commit e push del catalogo. Controllare infine il manifest pubblico e il suo asset.

Se il catalogo non può essere aggiornato, dichiarare esplicitamente che la release GitHub esiste ma la distribuzione tramite catalogo non è completata. Non aggirare un asset non pubblico inserendolo ugualmente nel manifest.

L’installazione o l’aggiornamento di un server in produzione è un’attività separata: non fa parte della pubblicazione e richiede le istruzioni operative di quell’ambiente.
