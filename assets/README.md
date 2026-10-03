# Identità visiva e attribuzioni

Questa cartella conserva il logo originale del plugin, la nuova mascotte e i loghi delle fonti. I loghi dei servizi sono copie dei file ufficiali, senza modifiche a colori o proporzioni.

## Progetto

- `logo.png`, i banner e le varianti esistenti identificano **AnimeClick Metadata Plugin**, sviluppato da **Alessio Cosi (iCosiSenpai)**.
- `mascot.png` è la nuova mascotte: un gatto robot bibliotecario con felpa rossa, occhi turchesi e schede dei metadati.
- La mascotte è stata generata con il **tool integrato image_gen**, poi rifinita con lo stesso tool per mantenere margini trasparenti attorno al personaggio. Il PNG conserva il canale alfa.
- La riserva sui segni distintivi del progetto e la licenza del software sono descritte in [NOTICE](../NOTICE) e [LICENSE](../LICENSE). I marchi e i contenuti AnimeClick eventualmente presenti nel logo originale restano ai rispettivi titolari.

## Loghi delle fonti

Recuperati il **3 ottobre 2026**. I file servono a identificare e riconoscere le fonti dei dati; non indicano affiliazione, approvazione o certificazione del plugin.

| File | Provenienza ufficiale | Attribuzione e condizioni |
|---|---|---|
| [AnimeClick](providers/animeclick.png) | [Logo dal sito AnimeClick](https://www.animeclick.it/bundles/accommon/images/logo_gatto.png) | **AnimeClick.it / Associazione NewType Media.** [Termini](https://www.animeclick.it/termini-di-servizio). La licenza dei contenuti editoriali non si estende automaticamente ai marchi e alle immagini. |
| [TMDB](providers/tmdb.svg) | [SVG ufficiale approvato](https://www.themoviedb.org/assets/v4/logos/v2/blue_long_2-9665a76b1ae401a510ec1e0ca40ddcb3b0cfe45f1d51b77a308fea0845885648.svg), dalla pagina [Logos & Attribution](https://www.themoviedb.org/about/logos-attribution) | **The Movie Database (TMDB).** [Regole di attribuzione e marchio](https://developer.themoviedb.org/docs/faq). Nessuna modifica al logo; nel README è meno prominente dell'identità del plugin. |
| [TheTVDB, tema chiaro](providers/thetvdb.png) | [Logo ufficiale 2](https://www.thetvdb.com/images/attribution/logo2.png) | **TheTVDB.com, A Whip Media Company.** [Attribuzione e condizioni API](https://www.thetvdb.com/api-information). |
| [TheTVDB, tema scuro](providers/thetvdb-dark.png) | [Logo ufficiale 1](https://www.thetvdb.com/images/attribution/logo1.png) | Stesse condizioni del logo per il tema chiaro. Il README seleziona il file con `picture`, senza ricolorarlo. |
| [Fanart.tv](providers/fanart.png) | [File della documentazione ufficiale Fanart](https://github.com/fanart-tv/fanartwiki/blob/56535ed4074f9a7fdf7cae04560989b83da4ba73/docs/assets/images/fatv-logo.png) | **Fanart.tv.** [Condizioni d'uso](https://fanart.tv/terms-and-conditions/). I diritti sulle immagini distribuite dal servizio restano ai rispettivi titolari. |

Per il logo AnimeClick, la [presentazione ufficiale del 2019](https://www.animeclick.it/news/77907-nel-2019-arriva-il-nuovo-logo-di-animeclick-scopriamolo-insieme) riconosce **Alex Hylian** per l'ideazione del logo e **Sailortenshi** per il daruma.

I loghi dei servizi non vengono rilicenziati sotto GPL e non sono marchi del plugin. Le frasi di attribuzione richieste dai provider sono visibili nel [README principale](../README.md#copyright-fonti-e-licenze) e conservate in [NOTICE](../NOTICE).

## Prompt della mascotte

<details>
<summary>Prompt di generazione</summary>

```text
Use case: stylized-concept.
Asset type: original mascot illustration for the AnimeClick Metadata Jellyfin plugin README and project identity.
Primary request: create one charming, original anime-style chibi robot cat librarian, a helpful guide that organizes anime metadata. It should feel designed as a distinctive, polished open-source plugin mascot, not a generic stock sticker.
Subject: a compact friendly white and charcoal robot cat, large expressive turquoise eyes, softly rounded triangular ears, a small red zip hoodie echoing the existing project's red identity, restrained violet accents that connect to Jellyfin. The robot cat holds a small stack of neatly organized media catalog cards against its chest, with one simple play triangle and two tidy metadata lines on the front card; its other paw gives a welcoming wave. Original face and body design, cute and confident.
Style/medium: high quality clean anime cel-shaded character illustration, crisp dark outlines, controlled soft shading, strong readable silhouette even at a small README size, pleasant balanced shapes, no photorealism or glossy 3D rendering.
Composition/framing: exactly one mascot, full body visible, centered square composition with comfortable transparent padding, ears and feet not cropped, no circular badge enclosing it.
Scene/backdrop: genuinely transparent background, clean alpha cutout, no environment, no opaque white rectangle, no fake checkerboard pattern, no ground or large drop shadow.
Text: no letters, no words, no slogans.
Constraints: original character, do not use or imitate any existing anime character or third-party mascot; no AnimeClick, TMDB, TheTVDB, Fanart or Jellyfin logos; no watermark; clear anatomy with two arms, two legs and one tail.
```

</details>

<details>
<summary>Prompt finale di rifinitura</summary>

```text
Use case: precise-object-edit.
Edit target: the provided original robot cat mascot.
Change only framing: place this exact unchanged character at about 78 percent of the canvas height, with even, generous completely transparent padding around it. Recover the complete tiny tip of the upper ear if needed. The whole head, ears, waving paw, cards, tail and feet must fit comfortably inside the canvas, with at least 8 percent empty transparent margin at top and bottom. Remove any stray edge pixels outside the character silhouette.
Preserve invariants: preserve the same character identity, proportions, pose, turquoise eyes, red hoodie, charcoal and white robot surfaces, violet accents, catalog cards, outlines and anime cel-shaded illustration. Do not redesign or add elements. Exactly one character. No lettering, watermarks, logos, opaque backdrop or fake transparency checkerboard. Deliver a genuine transparent alpha cutout.
```

</details>

