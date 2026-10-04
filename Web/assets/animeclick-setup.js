/* AnimeClick per Jellyfin — setup guidato: primo avvio e passi degli aggiornamenti importanti.
 *
 * Come aggiungere un passo per un aggiornamento che richiede una decisione dell'utente
 * (vedi anche AGENTS.md):
 *   1. aumenta SETUP_VERSION di uno;
 *   2. aggiungi il passo a STEPS con `since: <nuova versione>` (e `updateOnly: true` se ha senso solo
 *      per chi aggiorna, come le novità);
 *   3. aggiungi un test web che lo mostri a una configurazione con SetupCompletedVersion precedente.
 * Chi installa da zero vede tutti i passi non `updateOnly`; chi aggiorna vede solo quelli con
 * `since` maggiore del setup che ha già completato.
 */
(function () {
    'use strict';

    var AC = window.AnimeClickUI;
    var h = AC.dom.h, icon = AC.dom.icon, clear = AC.dom.clear, replace = AC.dom.replace;
    var val = AC.util.val, plural = AC.util.plural, truncate = AC.util.truncate;
    var ui = AC.ui, api = AC.api;

    var SETUP_VERSION = 4;

    var STEPS = [
        { id: 'welcome', since: 1, label: 'Benvenuto', render: renderWelcome },
        { id: 'news', since: 2, updateOnly: true, label: 'Novità', render: renderNews },
        { id: 'sources', since: 1, label: 'Fonti', render: renderSources },
        { id: 'preferences', since: 1, label: 'Preferenze', render: renderPreferences },
        { id: 'libraries', since: 2, label: 'Librerie', render: renderLibraries },
        { id: 'community', since: 3, label: 'Comunità', render: renderCommunity },
        { id: 'anilist', since: 4, updateOnly: true, label: 'AniList', render: renderAniList },
        { id: 'done', since: 1, always: true, label: 'Fatto', render: renderDone }
    ];

    var PREFERENCES = [
        ['PreferItalianTitle', 'Titolo italiano', 'Il titolo AnimeClick come nome principale.'],
        ['EnablePlot', 'Trama italiana', 'La sinossi AnimeClick quando c’è.'],
        ['EnableEpisodeTitles', 'Titoli degli episodi', 'Completa i nomi vuoti, generici o inglesi.'],
        ['EnableEpisodeSynopsisTranslation', 'Trame degli episodi', 'AnimeClick e le fonti che hai aggiunto.'],
        ['EnableGenres', 'Generi in italiano', null],
        ['EnableCast', 'Cast e staff', 'Doppiatori e ruoli.'],
        ['EnableTrailers', 'Trailer e PV', null],
        ['EnableIntegratedImages', 'Immagini ad alta risoluzione', 'Da TMDB, con la chiave.']
    ];

    var page = null;
    var host = null;
    var steps = [];
    var index = 0;
    var draft = {};
    var mode = null;

    function el(id) {
        return page ? page.querySelector('#' + id) : null;
    }

    /** 'full' for a fresh install, 'update' when an update added steps, null when nothing is due. */
    function needed(config) {
        var done = Number(config && config.SetupCompletedVersion) || 0;
        if (done <= 0) return 'full';
        return done < SETUP_VERSION ? 'update' : null;
    }

    function selectSteps(kind, done) {
        return STEPS.filter(function (step) {
            if (step.always) return true;
            if (kind === 'full') return !step.updateOnly;
            return step.since > done;
        });
    }

    function open(kind) {
        if (AC.settings.isDirty()) {
            ui.toast('Salva o annulla le modifiche alle impostazioni prima di aprire il setup.', 'error');
            return;
        }
        var config = AC.settings.saved() || {};
        mode = kind === 'update' ? 'update' : 'full';
        steps = selectSteps(mode, Number(config.SetupCompletedVersion) || 0);
        index = 0;
        draft = {};
        PREFERENCES.forEach(function (pref) { draft[pref[0]] = config[pref[0]] !== false; });
        draft.TmdbApiKey = config.TmdbApiKey || '';
        draft.FanartPersonalApiKey = config.FanartPersonalApiKey || '';
        // The community step proposes reading the approved list switched on, but only the first time it
        // is shown: a setup reopened later keeps whatever the administrator chose since.
        draft.EnableCommunityMappings = (Number(config.SetupCompletedVersion) || 0) < 3 ? true : !!config.EnableCommunityMappings;
        draft.CommunitySharingMode = ['Ask', 'Always', 'Never'].indexOf(config.CommunitySharingMode) >= 0 ? config.CommunitySharingMode : 'Ask';
        draft.EnableAniListMetadata = (Number(config.SetupCompletedVersion) || 0) < 4 ? true : !!config.EnableAniListMetadata;
        AC.app.showSetup(true);
        render();
    }

    function close() {
        AC.app.showSetup(false);
        AC.app.go('home');
    }

    function finish(target) {
        var buttonNode = el('acSetupFinish');
        ui.setBusy(buttonNode, true, 'Salvataggio…');
        return AC.settings.savePatch({ SetupCompletedVersion: SETUP_VERSION }).then(function () {
            close();
            if (target && target !== 'home') AC.app.go(target);
            ui.toast(mode === 'update' ? 'Aggiornamento completato: buona visione!' : 'Tutto pronto: buona visione!', 'success');
        }).catch(function (error) {
            ui.toast('Non riesco a salvare: ' + truncate(error.message, 200), 'error');
            ui.setBusy(buttonNode, false);
        });
    }

    function skip() {
        ui.confirm({
            title: 'Saltare il setup?',
            confirmLabel: 'Salta',
            message: 'Le impostazioni restano come sono. Puoi riaprire il setup quando vuoi da Strumenti.'
        }).then(function (yes) {
            if (!yes) return;
            AC.settings.savePatch({ SetupCompletedVersion: SETUP_VERSION }).then(close).catch(function (error) {
                ui.toast('Non riesco a salvare: ' + truncate(error.message, 200), 'error');
            });
        });
    }

    /* ===== frame ===== */

    function render() {
        host = el('acSetup');
        var step = steps[index];
        var progressList = h('ol', { class: 'ac-steps', 'aria-label': 'Passi del setup' }, steps.map(function (item, position) {
            return h('li', {
                class: position < index ? 'is-done' : position === index ? 'is-current' : '',
                'aria-current': position === index ? 'step' : null
            }, item.label);
        }));
        var main = h('div', { class: 'ac-setup-main' });
        var side = h('div', { class: 'ac-setup-side' });
        var cardNode = h('section', { class: 'ac-setup-card', 'aria-labelledby': 'acSetupTitle' }, main, side);
        replace(host, progressList, cardNode);
        step.render(main, side, cardNode);
        var heading = el('acSetupTitle');
        if (heading) { heading.tabIndex = -1; heading.focus({ preventScroll: true }); }
        host.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }

    function heading(jp, kicker, title, lead) {
        return [
            h('p', { class: 'ac-jp' }, kicker, h('i', { 'aria-hidden': 'true', text: jp })),
            h('h1', { id: 'acSetupTitle', class: 'ac-h1', text: title }),
            lead ? h('p', { class: 'ac-lead', text: lead }) : null
        ];
    }

    function mascot(side, line) {
        side.appendChild(h('img', { src: api.asset('AnimeClickMascot'), alt: '' }));
        if (line) side.appendChild(h('p', { class: 'ac-bubble', text: line }));
    }

    function actions(main, options) {
        options = options || {};
        var row = h('div', { class: 'ac-setup-actions' });
        if (options.skip !== false && steps[index].id !== 'done') {
            row.appendChild(h('button', { type: 'button', class: 'ac-link', id: 'acSetupSkip', text: 'Salta il setup', on: { click: skip } }));
        } else {
            row.appendChild(h('span', { style: 'margin-right:auto' }));
        }
        if (index > 0 && steps[index].id !== 'done') {
            row.appendChild(ui.button('Indietro', { id: 'acSetupBack', variant: 'ghost', icon: 'chevronLeft', onClick: function () { index--; render(); } }));
        }
        if (options.extra) row.appendChild(options.extra);
        var next = ui.button(options.nextLabel || 'Avanti', { id: options.nextId || 'acSetupNext', variant: 'primary', icon: options.nextIcon || 'arrowRight' });
        next.addEventListener('click', function () {
            var gate = options.beforeNext ? options.beforeNext(next) : Promise.resolve(true);
            Promise.resolve(gate).then(function (ok) {
                if (ok === false) return;
                index = Math.min(index + 1, steps.length - 1);
                render();
            });
        });
        row.appendChild(next);
        main.appendChild(row);
        return next;
    }

    /* ===== steps ===== */

    function feature(iconName, title, text) {
        return h('div', { class: 'ac-feature' }, h('span', { class: 'ac-card-icon' }, icon(iconName)),
            h('div', null, h('strong', { text: title }), h('span', { class: 'ac-hint', text: text })));
    }

    function renderWelcome(main, side) {
        heading('ようこそ', 'Benvenuto', 'I tuoi anime, in italiano.',
            'AnimeClick porta in Jellyfin titoli, trame, generi, cast ed episodi dal più grande database italiano di anime. Bastano un paio di minuti.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        main.appendChild(h('div', { class: 'ac-features' },
            feature('cat', 'Funziona subito', 'AnimeClick non chiede chiavi né account.'),
            feature('plug', 'Fonti aggiuntive facoltative', 'TMDB, Fanart, TheTVDB e traduzione AI completano ciò che manca.'),
            feature('folder', 'Solo dove vuoi tu', 'Lo attivi nelle librerie anime; le altre restano come sono.')));
        actions(main, { nextLabel: 'Iniziamo' });
        mascot(side, 'Ciao! Ti aiuto a sistemare la tua collezione.');
    }

    function renderNews(main, side) {
        heading('ニュース', 'Novità', 'AnimeClick si è rifatto il look',
            'Una nuova casa per i tuoi anime, con un paio di cose da controllare insieme.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        main.appendChild(h('div', { class: 'ac-features' },
            feature('sparkles', 'Vetrina', 'In Inizio trovi le locandine degli ultimi anime aggiornati e lo stato della collezione.'),
            feature('library', 'Libreria a locandine', 'Titoli e trame di ogni anime in un colpo d’occhio, con il dettaglio a un clic.'),
            feature('folder', 'Librerie a un clic', 'Puoi attivare AnimeClick nelle librerie direttamente da qui.'),
            feature('checklist', 'Setup degli aggiornamenti', 'Quando un aggiornamento richiede una scelta, te lo chiediamo una volta sola.')));
        actions(main, { nextLabel: 'Diamo un’occhiata' });
        mascot(side, 'Bentornato! Ho qualche novità da mostrarti.');
    }

    function renderSources(main, side) {
        heading('ソース', 'Fonti', 'Vuoi più dati e immagini migliori?',
            'Facoltativo. La chiave TMDB è quella che fa la differenza: identifica meglio le opere e porta immagini ad alta risoluzione.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        var tmdbResult = ui.liveStatus('acSetupTmdbResult');
        var tmdbTest = ui.button('Verifica', { small: true, icon: 'check', id: 'acSetupTmdbTest' });
        var tmdb = ui.field('acSetupTmdbKey', 'Chiave API TMDB (consigliata)', {
            secret: true, spellcheck: false,
            html: 'Gratuita: la trovi nelle <a href="https://developer.themoviedb.org/docs/getting-started" target="_blank" rel="noopener noreferrer">impostazioni API di TMDB</a>.'
        });
        var fanartResult = ui.liveStatus('acSetupFanartResult');
        var fanartTest = ui.button('Verifica', { small: true, icon: 'check', id: 'acSetupFanartTest' });
        var fanart = ui.field('acSetupFanartKey', 'Chiave personale Fanart (facoltativa)', {
            secret: true, spellcheck: false,
            html: 'Per sfondi e loghi più belli: <a href="https://fanart.tv/get-an-api-key/" target="_blank" rel="noopener noreferrer">creala su Fanart</a>.'
        });
        main.appendChild(tmdb);
        main.appendChild(h('div', { class: 'ac-row' }, tmdbTest, tmdbResult));
        main.appendChild(fanart);
        main.appendChild(h('div', { class: 'ac-row' }, fanartTest, fanartResult));
        main.appendChild(aniListSwitch());
        main.appendChild(ui.hint('TheTVDB e la traduzione AI le trovi più avanti in Fonti.'));
        el('acSetupTmdbKey').value = draft.TmdbApiKey;
        el('acSetupFanartKey').value = draft.FanartPersonalApiKey;
        el('acSetupTmdbKey').addEventListener('input', function () { draft.TmdbApiKey = this.value.trim(); });
        el('acSetupFanartKey').addEventListener('input', function () { draft.FanartPersonalApiKey = this.value.trim(); });

        function verify(source, buttonNode, out, payload) {
            ui.setBusy(buttonNode, true, 'Verifica…');
            ui.status(out, 'Connessione in corso…');
            AC.settings.runTest(source, payload).then(function (result) {
                ui.status(out, result.message, result.success ? 'ok' : 'bad');
            }).catch(function (error) {
                ui.status(out, truncate(error.message, 240), 'bad');
            }).finally(function () { ui.setBusy(buttonNode, false); });
        }
        tmdbTest.addEventListener('click', function () {
            if (!draft.TmdbApiKey) { ui.status(tmdbResult, 'Inserisci prima la chiave.', 'bad'); return; }
            verify('tmdb', tmdbTest, tmdbResult, { apiKey: draft.TmdbApiKey });
        });
        fanartTest.addEventListener('click', function () {
            if (!draft.FanartPersonalApiKey) { ui.status(fanartResult, 'Inserisci prima la chiave.', 'bad'); return; }
            var saved = AC.settings.saved() || {};
            verify('fanart', fanartTest, fanartResult, { personalApiKey: draft.FanartPersonalApiKey, projectApiKey: saved.FanartProjectApiKey || '' });
        });
        actions(main);
        mascot(side, 'Le chiavi sono facoltative: AnimeClick funziona anche da solo.');
    }

    function renderPreferences(main, side) {
        heading('セッティング', 'Preferenze', 'Cosa importare?',
            'Le scelte consigliate sono già attive. Le altre opzioni, comprese quelle avanzate, sono in Preferenze.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        var rows = PREFERENCES.map(function (pref) {
            var row = ui.switchRow('acSetupPref_' + pref[0], pref[1], pref[2]);
            var input = row.querySelector('input');
            input.checked = !!draft[pref[0]];
            input.addEventListener('change', function () { draft[pref[0]] = input.checked; });
            return row;
        });
        main.appendChild(ui.switches(rows));
        var reset = ui.button('Ripristina i consigliati', { small: true, variant: 'ghost', icon: 'undo', onClick: function () {
            PREFERENCES.forEach(function (pref) {
                draft[pref[0]] = true;
                var input = el('acSetupPref_' + pref[0]);
                if (input) input.checked = true;
            });
        } });
        main.appendChild(h('div', { class: 'ac-row' }, reset));
        // Sources and preferences are saved here, before the libraries: enabling a library places the
        // AnimeClick images according to the keys that are actually saved.
        actions(main, {
            nextLabel: 'Salva e continua',
            nextIcon: 'save',
            beforeNext: function (buttonNode) {
                var patch = {};
                PREFERENCES.forEach(function (pref) { patch[pref[0]] = !!draft[pref[0]]; });
                patch.TmdbApiKey = draft.TmdbApiKey;
                patch.FanartPersonalApiKey = draft.FanartPersonalApiKey;
                patch.EnableAniListMetadata = !!draft.EnableAniListMetadata;
                ui.setBusy(buttonNode, true, 'Salvataggio…');
                return AC.settings.savePatch(patch).then(function () { return true; }).catch(function (error) {
                    ui.toast('Non riesco a salvare: ' + truncate(error.message, 200), 'error');
                    ui.setBusy(buttonNode, false);
                    return false;
                });
            }
        });
        mascot(side, 'Ho già acceso quello che serve di solito.');
    }

    function renderLibraries(main, side) {
        heading('ライブラリ', 'Librerie', 'Dove usare AnimeClick?',
            'Scegli le librerie con i tuoi anime: AnimeClick diventa il primo provider dei metadati e si aggiunge alle immagini. Le altre fonti restano attive e le altre librerie non vengono toccate.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        var listHost = h('div', { id: 'acSetupLibraries', class: 'ac-stack' }, h('div', { class: 'ac-libs' }, ui.skeleton(96), ui.skeleton(96)));
        main.appendChild(listHost);
        var selected = Object.create(null);
        var enable = ui.button('Attiva nelle librerie scelte', { id: 'acSetupEnable', icon: 'play', disabled: true });
        main.appendChild(h('div', { class: 'ac-row' }, enable, ui.liveStatus('acSetupLibrariesResult')));

        function draw(rows) {
            if (!rows.length) {
                replace(listHost, ui.callout('Nessuna libreria di serie o film', 'Crea una libreria in Jellyfin (Dashboard → Librerie) e torna qui quando vuoi: puoi attivare AnimeClick anche da Inizio.', 'info'));
                enable.disabled = true;
                return;
            }
            replace(listHost, h('div', { class: 'ac-libs', role: 'group', 'aria-label': 'Librerie' }, rows.map(function (lib) {
                var id = val(lib, 'id');
                var state = AC.libraries.state(lib);
                var canEnable = !!val(lib, 'canEnable');
                var box = h('input', { type: 'checkbox', id: 'acSetupLib_' + id, disabled: !canEnable });
                box.checked = !!selected[id];
                box.addEventListener('change', function () {
                    if (box.checked) selected[id] = lib; else delete selected[id];
                    card.classList.toggle('is-selected', box.checked);
                    enable.disabled = !Object.keys(selected).length;
                });
                var card = h('label', { class: 'ac-lib' + (canEnable ? ' is-selectable' : '') + (box.checked ? ' is-selected' : ''), htmlFor: box.id },
                    h('div', { class: 'ac-lib-head' }, box,
                        h('div', null, h('strong', { text: val(lib, 'name') }), h('span', { class: 'ac-muted', text: AC.libraries.collectionName(lib) })),
                        ui.badge(state.label, state.tone, state.icon)),
                    AC.libraries.types(lib),
                    val(lib, 'state') === 'unmanaged' ? ui.hint('Jellyfin non ha ancora salvato i provider di questa libreria: apri e salva le sue impostazioni in Dashboard → Librerie.') : null);
                return card;
            })));
            enable.disabled = !Object.keys(selected).length;
        }

        function load(force) {
            AC.libraries.load(force).then(function (rows) {
                if (!force) {
                    rows.forEach(function (lib) {
                        if (val(lib, 'canEnable') && val(lib, 'state') !== 'active' && /anime|アニメ/i.test(val(lib, 'name') || '')) selected[val(lib, 'id')] = lib;
                    });
                }
                draw(rows);
            }).catch(function (error) {
                replace(listHost, ui.callout('Non riesco a leggere le librerie', truncate(error.message, 240), 'warn'),
                    h('div', { class: 'ac-row' }, ui.button('Riprova', { small: true, icon: 'refresh', onClick: function () { load(true); } })));
            });
        }

        enable.addEventListener('click', function () {
            var chosen = Object.keys(selected).map(function (id) { return selected[id]; });
            AC.libraries.confirmEnable(chosen.map(function (lib) { return val(lib, 'name'); })).then(function (yes) {
                if (!yes) return;
                var out = el('acSetupLibrariesResult');
                var failures = [];
                ui.setBusy(enable, true, 'Attivazione…');
                chosen.reduce(function (chain, lib) {
                    return chain.then(function () {
                        return AC.libraries.enable(lib).catch(function (error) { failures.push(val(lib, 'name') + ': ' + error.message); });
                    });
                }, Promise.resolve()).then(function () {
                    Object.keys(selected).forEach(function (id) { delete selected[id]; });
                    draw(AC.libraries.rows() || []);
                    ui.status(out, failures.length ? 'Non riuscito per ' + failures.join(' · ') : 'Fatto! ' + plural(chosen.length, 'libreria usa', 'librerie usano') + ' AnimeClick.', failures.length ? 'bad' : 'ok');
                }).finally(function () {
                    ui.setBusy(enable, false);
                    enable.disabled = !Object.keys(selected).length;
                });
            });
        });

        load(false);
        actions(main);
        mascot(side, 'Solo le librerie anime: il resto lo lascio stare.');
    }

    function aniListSwitch() {
        var row = ui.switchRow('acSetupAniList', 'AniList, senza chiave',
            'Completa cast con personaggi e doppiatori, studio, date, voto, trailer, copertina e banner, e l’anno delle stagioni per collegare i sequel. Solo i campi vuoti; nessun testo, perché AniList è in inglese.');
        var input = row.querySelector('input');
        input.checked = !!draft.EnableAniListMetadata;
        input.addEventListener('change', function () { draft.EnableAniListMetadata = input.checked; });
        return ui.switches([row]);
    }

    function renderAniList(main, side) {
        heading('アニリスト', 'Novità', 'AniList, insieme a TMDB',
            'Una fonte in più, senza chiave, per i dati delle opere che AnimeClick non ha. Non cambia nulla di quello che è già nella libreria: completa i campi vuoti al prossimo aggiornamento dei metadati.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        main.appendChild(h('div', { class: 'ac-features' },
            feature('users', 'Cast giapponese con i personaggi', 'Doppiatori con foto e il personaggio che interpretano, più regia e soggetto.'),
            feature('sparkles', 'Studio, date, voto e trailer', 'Lo studio di animazione al posto delle case di produzione, quando AnimeClick non lo indica.'),
            feature('layers', 'Stagioni più sicure', 'Quando Jellyfin non sa in che anno è andata in onda una stagione, AniList lo dice: il sequel giusto si trova anche senza date.')));
        main.appendChild(aniListSwitch());
        main.appendChild(ui.hint('Un ID AniList viene usato solo se tipo (film o serie) e anno coincidono con l’opera. Puoi cambiare idea in Fonti.'));
        actions(main, {
            nextLabel: 'Salva e continua',
            nextIcon: 'save',
            beforeNext: function (buttonNode) {
                ui.setBusy(buttonNode, true, 'Salvataggio…');
                return AC.settings.savePatch({ EnableAniListMetadata: !!draft.EnableAniListMetadata }).then(function () { return true; }).catch(function (error) {
                    ui.toast('Non riesco a salvare: ' + truncate(error.message, 200), 'error');
                    ui.setBusy(buttonNode, false);
                    return false;
                });
            }
        });
        mascot(side, 'Più dati per i tuoi anime, senza nessuna chiave!');
    }

    var SHARING = [
        ['Ask', 'Chiedimi ogni volta', 'Consigliato. Dopo una correzione ti mostro cosa verrebbe inviato e decidi tu.'],
        ['Always', 'Condividi sempre', 'Le correzioni partono da sole, con i soli ID pubblici.'],
        ['Never', 'Non condividere', 'Le tue correzioni restano solo tue.']
    ];

    function renderCommunity(main, side) {
        heading('コミュニティ', 'Comunità', 'Una correzione, per tutti',
            'Quando correggi un abbinamento, la stessa correzione può sistemare la libreria di chi ha la stessa opera. Ogni proposta passa controlli automatici e una revisione prima di arrivare a tutti. Non serve nessun account.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        var receive = ui.switchRow('acSetupCommunityMappings', 'Usa gli abbinamenti della comunità',
            'Una volta al giorno scarica l’elenco approvato. Vale solo per serie, film e stagioni senza un ID AnimeClick: non cambia mai un abbinamento che hai già.');
        var receiveInput = receive.querySelector('input');
        receiveInput.checked = !!draft.EnableCommunityMappings;
        receiveInput.addEventListener('change', function () { draft.EnableCommunityMappings = receiveInput.checked; });
        main.appendChild(ui.switches([receive]));

        main.appendChild(h('p', { class: 'ac-step-title', id: 'acSetupSharingLabel', text: 'Dopo una correzione' }));
        main.appendChild(h('div', { class: 'ac-mode-choices', role: 'radiogroup', 'aria-labelledby': 'acSetupSharingLabel' }, SHARING.map(function (choice) {
            var input = h('input', { type: 'radio', name: 'acSetupSharing', id: 'acSetupSharing_' + choice[0], value: choice[0] });
            input.checked = draft.CommunitySharingMode === choice[0];
            input.addEventListener('change', function () { if (input.checked) draft.CommunitySharingMode = choice[0]; });
            return h('label', { class: 'ac-mode-choice', htmlFor: input.id }, input,
                h('span', null, h('strong', { text: choice[1] }), h('small', { class: 'ac-hint', text: choice[2] })));
        })));
        main.appendChild(ui.callout('Cosa parte, e cosa mai',
            'Solo ID pubblici: tipo, ID AnimeClick e ID TMDB, TheTVDB o AniList; per una stagione anche il suo numero e quanti episodi contiene. Mai titoli, utenti, percorsi, file, indirizzo del server, trame o immagini. Come per ogni servizio web, chi riceve la proposta vede l’indirizzo IP da cui arriva.', 'info', 'eye'));
        actions(main, {
            nextLabel: 'Salva e continua',
            nextIcon: 'save',
            beforeNext: function (buttonNode) {
                ui.setBusy(buttonNode, true, 'Salvataggio…');
                return AC.settings.savePatch({
                    EnableCommunityMappings: !!draft.EnableCommunityMappings,
                    CommunitySharingMode: draft.CommunitySharingMode
                }).then(function () { return true; }).catch(function (error) {
                    ui.toast('Non riesco a salvare: ' + truncate(error.message, 200), 'error');
                    ui.setBusy(buttonNode, false);
                    return false;
                });
            }
        });
        mascot(side, 'Una tua correzione può sistemare la libreria di tanti altri!');
    }

    function renderDone(main, side, cardNode) {
        heading('完了', 'Fatto', mode === 'update' ? 'Tutto aggiornato!' : 'Tutto pronto!',
            'Al prossimo aggiornamento dei metadati Jellyfin userà AnimeClick. Gli anime aggiornati compariranno nella vetrina di Inizio.')
            .forEach(function (node) { if (node) main.appendChild(node); });
        var config = AC.settings.saved() || {};
        var state = AC.settings.sourceState(config);
        var libs = AC.libraries.rows() || [];
        var active = libs.filter(function (lib) { return val(lib, 'state') === 'active'; }).length;
        var summary = function (ok, title, text) {
            return h('li', { class: 'ac-list-item' }, h('span', { class: 'ac-list-icon is-' + (ok ? 'ok' : 'warn') }, icon(ok ? 'check' : 'info')),
                h('div', null, h('strong', { text: title }), h('span', { class: 'ac-hint', text: text })));
        };
        main.appendChild(h('ul', { class: 'ac-list' },
            summary(true, 'AnimeClick', 'Pronto, senza chiavi.'),
            summary(state.tmdb, 'TMDB', state.tmdb ? 'Chiave salvata.' : 'Puoi aggiungerla quando vuoi in Fonti.'),
            summary(state.aniList, 'AniList', state.aniList ? 'Completa le opere senza chiave.' : 'Spenta: puoi accenderla in Fonti.'),
            summary(active > 0, 'Librerie', active ? plural(active, 'libreria usa', 'librerie usano') + ' AnimeClick come primo provider.' : 'Attiva AnimeClick nelle librerie anime da Inizio.'),
            summary(!!config.EnableCommunityMappings, 'Comunità', (config.EnableCommunityMappings ? 'Ricevi gli abbinamenti approvati. ' : 'Abbinamenti della comunità spenti. ')
                + (config.CommunitySharingMode === 'Always' ? 'Le tue correzioni vengono condivise.' : config.CommunitySharingMode === 'Never' ? 'Le tue correzioni restano tue.' : 'Ti chiederò se condividere ogni correzione.'))));
        main.appendChild(ui.hint('Puoi chiedere a Jellyfin di aggiornare subito una libreria da Dashboard → Librerie → «Scansiona tutte le librerie», oppure aspettare la scansione programmata.'));
        var row = h('div', { class: 'ac-setup-actions' },
            h('span', { style: 'margin-right:auto' }),
            ui.button('Apri la libreria', { variant: 'ghost', icon: 'library', onClick: function () { finish('library'); } }));
        var go = ui.button('Vai alla vetrina', { id: 'acSetupFinish', variant: 'primary', icon: 'sparkles' });
        go.addEventListener('click', function () { finish('home'); });
        row.appendChild(go);
        main.appendChild(row);
        mascot(side, 'Buona visione! ✨');
        if (cardNode) cardNode.classList.add('is-done');
    }

    AC.setup = {
        version: SETUP_VERSION,
        attach: function (pageElement) { page = pageElement; },
        needed: needed,
        open: open,
        steps: function (kind, done) { return selectSteps(kind, done).map(function (step) { return step.id; }); }
    };
}());
