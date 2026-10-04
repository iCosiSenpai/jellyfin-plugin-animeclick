/* AnimeClick per Jellyfin — Inizio: vetrina degli ultimi anime aggiornati e cruscotto. */
(function () {
    'use strict';

    var AC = window.AnimeClickUI;
    var h = AC.dom.h, icon = AC.dom.icon, clear = AC.dom.clear, replace = AC.dom.replace;
    var val = AC.util.val, list = AC.util.list, num = AC.util.num, plural = AC.util.plural;
    var truncate = AC.util.truncate, relTime = AC.util.relTime;
    var ui = AC.ui, api = AC.api;

    var HERO_ITEMS = 5;
    var HERO_INTERVAL = 8000;

    var page = null;
    var showcase = null;
    var showcaseAt = 0;
    var showcaseLoading = null;
    var heroIndex = 0;
    var heroTimer = null;
    var heroPaused = false;

    function el(id) {
        return page ? page.querySelector('#' + id) : null;
    }

    function reducedMotion() {
        return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    /* ===== libraries (shared with the setup) ===== */

    var libraries = { rows: null, error: null, loading: null };
    var TYPE_NAMES = { Series: 'Serie', Season: 'Stagioni', Episode: 'Episodi', Movie: 'Film' };
    var COLLECTION_NAMES = { tvshows: 'Serie TV', movies: 'Film', mixed: 'Contenuti misti' };

    function loadLibraries(force) {
        if (libraries.loading) return libraries.loading;
        if (libraries.rows && !force) return Promise.resolve(libraries.rows);
        libraries.loading = api.request('GET', 'Plugins/AnimeClick/Libraries').then(function (rows) {
            libraries.rows = list(rows);
            libraries.error = null;
            return libraries.rows;
        }).catch(function (error) {
            libraries.error = error.message;
            throw error;
        }).finally(function () {
            libraries.loading = null;
            AC.bus.emit('libraries', libraries);
        });
        return libraries.loading;
    }

    function libraryState(lib) {
        var state = val(lib, 'state');
        if (state === 'active') return { label: 'Attivo', tone: 'ok', icon: 'check' };
        if (state === 'partial') return { label: 'Attivo in parte', tone: 'warn', icon: 'alert' };
        if (state === 'unmanaged') return { label: 'Impostazioni predefinite', tone: '', icon: 'info' };
        return { label: 'Non attivo', tone: '', icon: null };
    }

    function libraryTypes(lib) {
        return h('div', { class: 'ac-lib-types' }, list(val(lib, 'types')).map(function (type) {
            var name = TYPE_NAMES[val(type, 'type')] || val(type, 'type');
            if (!val(type, 'configured')) return ui.badge(name + ': predefinito');
            var first = val(type, 'metadataFirst');
            var on = val(type, 'metadataEnabled');
            var chip = ui.badge(name, first ? 'ok' : on ? 'warn' : '', first ? 'check' : null);
            chip.title = first ? 'AnimeClick è il primo provider dei metadati'
                : on ? 'AnimeClick è attivo ma non è il primo provider' : 'AnimeClick non è attivo';
            return chip;
        }));
    }

    function enableLibrary(lib) {
        return api.request('POST', 'Plugins/AnimeClick/Libraries/Enable', { libraryId: val(lib, 'id') }).then(function (result) {
            var updated = val(result, 'library');
            if (updated && libraries.rows) {
                libraries.rows = libraries.rows.map(function (row) { return val(row, 'id') === val(updated, 'id') ? updated : row; });
            }
            AC.bus.emit('libraries', libraries);
            return result;
        });
    }

    function confirmEnable(names) {
        return ui.confirm({
            title: names.length === 1 ? 'Attivare AnimeClick in «' + names[0] + '»?' : 'Attivare AnimeClick in ' + names.length + ' librerie?',
            confirmLabel: 'Attiva',
            message: [
                'AnimeClick diventa il primo provider dei metadati di serie, stagioni, episodi e film' + (names.length > 1 ? ' in: ' + names.join(', ') : '') + ', e viene aggiunto alle immagini.',
                'Gli altri provider restano attivi. Le schede si aggiornano al prossimo aggiornamento dei metadati della libreria.'
            ]
        });
    }

    /* ===== showcase ===== */

    function loadShowcase(force) {
        if (showcaseLoading) return showcaseLoading;
        if (showcase && !force && Date.now() - showcaseAt < 60000) return Promise.resolve(showcase);
        showcaseLoading = api.request('GET', 'Plugins/AnimeClick/Showcase?limit=16').then(function (result) {
            showcase = result || { items: [] };
            showcaseAt = Date.now();
            return showcase;
        }).finally(function () { showcaseLoading = null; });
        return showcaseLoading;
    }

    function heroItems() {
        return list(val(showcase, 'items')).slice(0, HERO_ITEMS);
    }

    function renderHero() {
        var host = el('acHomeHero');
        if (!host) return;
        stopHero();
        var items = heroItems();
        if (!items.length) {
            replace(host, h('section', { class: 'ac-hero ac-hero--empty', 'aria-label': 'Vetrina' },
                h('div', { class: 'ac-hero-lines' }), h('div', { class: 'ac-hero-tone' }),
                h('div', { class: 'ac-hero-body' },
                    h('img', { class: 'ac-hero-mascot', src: api.asset('AnimeClickMascot'), alt: '' }),
                    h('div', { class: 'ac-hero-copy' },
                        h('span', { class: 'ac-hero-kicker' }, icon('sparkles'), 'La tua vetrina'),
                        h('h1', { class: 'ac-hero-title', text: 'Qui compariranno i tuoi anime' }),
                        h('p', { class: 'ac-hero-overview', text: 'Ogni volta che Jellyfin aggiorna un anime con AnimeClick, lo trovi qui con locandina, trama e generi in italiano.' }),
                        h('div', { class: 'ac-row' }, ui.button('Controlla le librerie', {
                            variant: 'primary', icon: 'folder',
                            onClick: function () { var target = el('acHomeLibraries'); if (target) target.scrollIntoView({ behavior: 'smooth' }); }
                        }))))));
            return;
        }
        if (heroIndex >= items.length) heroIndex = 0;
        var item = items[heroIndex];
        var id = val(item, 'id');
        var backdrop = val(item, 'hasBackdropImage');
        var bgUrl = backdrop ? api.image(id, 'Backdrop', { maxWidth: 1600, quality: 80 })
            : val(item, 'hasPrimaryImage') ? api.image(id, 'Primary', { fillHeight: 500, quality: 70 }) : null;
        var genres = list(val(item, 'genres'));
        var meta = [];
        if (val(item, 'year')) meta.push(h('span', { text: String(val(item, 'year')) }));
        meta.push(ui.badge(val(item, 'type') === 'Movie' ? 'Film' : 'Serie'));
        genres.forEach(function (genre) { meta.push(ui.badge(genre)); });
        if (val(item, 'communityRating')) meta.push(h('span', { text: '★ ' + Number(val(item, 'communityRating')).toFixed(1) }));
        var dots = items.length > 1 ? h('div', { class: 'ac-hero-nav', role: 'group', 'aria-label': 'Scegli l’anime in vetrina' }, items.map(function (other, index) {
            return h('button', {
                type: 'button', class: 'ac-hero-dot', 'aria-label': val(other, 'name'), 'aria-current': index === heroIndex ? 'true' : 'false',
                on: { click: function () { heroIndex = index; renderHero(); } }
            });
        })) : null;
        var link = val(item, 'animeClickUrl');
        var hero = h('section', { class: 'ac-hero', 'aria-roledescription': 'vetrina', 'aria-label': 'Ultimo anime aggiornato: ' + val(item, 'name') },
            bgUrl ? h('div', { class: 'ac-hero-bg' + (backdrop ? '' : ' is-blur'), style: 'background-image:url("' + bgUrl + '")' }) : null,
            h('div', { class: 'ac-hero-shade' }), h('div', { class: 'ac-hero-lines' }), h('div', { class: 'ac-hero-tone' }),
            dots,
            h('div', { class: 'ac-hero-body' },
                h('div', { class: 'ac-hero-copy' },
                    h('span', { class: 'ac-hero-kicker' }, icon('sparkles'), 'Aggiornato ' + (relTime(val(item, 'updatedAt')) || 'di recente')),
                    h('h1', { class: 'ac-hero-title', text: val(item, 'name') }),
                    val(item, 'originalTitle') ? h('p', { class: 'ac-muted', text: val(item, 'originalTitle') }) : null,
                    h('div', { class: 'ac-hero-sub' }, meta),
                    val(item, 'overview') ? h('p', { class: 'ac-hero-overview', text: val(item, 'overview') }) : null,
                    h('div', { class: 'ac-row' },
                        ui.button('Stato dei metadati', { variant: 'primary', icon: 'library', onClick: function () { openInLibrary(id); } }),
                        h('a', { class: 'ac-btn ac-btn--ghost', href: api.itemUrl(id) }, icon('tv'), 'Apri in Jellyfin'),
                        link ? h('a', { class: 'ac-btn ac-btn--ghost', href: link, target: '_blank', rel: 'noopener noreferrer' }, icon('external'), 'AnimeClick') : null)),
                h('div', { class: 'ac-hero-poster' }, ui.poster(item, { height: 600 }))));
        replace(host, hero);
        requestAnimationFrame(function () { hero.classList.add('is-on'); });
        hero.addEventListener('mouseenter', function () { heroPaused = true; });
        hero.addEventListener('mouseleave', function () { heroPaused = false; });
        hero.addEventListener('focusin', function () { heroPaused = true; });
        hero.addEventListener('focusout', function () { heroPaused = false; });
        if (items.length > 1 && !reducedMotion()) {
            heroTimer = setTimeout(function () {
                if (AC.app.current() !== 'home' || !page.isConnected) return;
                if (!heroPaused && !document.hidden) heroIndex = (heroIndex + 1) % items.length;
                renderHero();
            }, HERO_INTERVAL);
        }
    }

    function stopHero() {
        clearTimeout(heroTimer);
        heroTimer = null;
    }

    function openInLibrary(id) {
        AC.app.go('library');
        AC.library.loadReports(false).then(function () {
            AC.library.openDetail(String(id).replace(/-/g, '').toLowerCase());
        });
    }

    function renderRail() {
        var host = el('acHomeRail');
        if (!host) return;
        var items = list(val(showcase, 'items'));
        if (!items.length) { clear(host); host.hidden = true; return; }
        host.hidden = false;
        replace(host,
            h('div', { class: 'ac-rail-head' },
                h('h2', { class: 'ac-h2 ac-grow' }, 'Ultimi aggiornati da AnimeClick'),
                h('span', { class: 'ac-jp', 'aria-hidden': 'true' }, h('i', { text: '新着' }))),
            h('div', { class: 'ac-rail-track', role: 'list' }, items.map(function (item) {
                return h('div', { role: 'listitem' }, h('button', {
                    type: 'button', class: 'ac-poster-card', on: { click: function () { openInLibrary(val(item, 'id')); } }
                },
                ui.poster(item, { height: 330 }),
                h('span', { class: 'ac-poster-title', text: val(item, 'name') }),
                h('span', { class: 'ac-poster-meta', text: relTime(val(item, 'updatedAt')) || (val(item, 'year') ? String(val(item, 'year')) : '') })));
            })));
    }

    /* ===== dashboard ===== */

    function renderStats() {
        var host = el('acHomeStats');
        if (!host) return;
        var titles = AC.library.data().titles;
        var stat = function (value, label) { return h('div', { class: 'ac-stat' }, h('b', { text: value == null ? '—' : num(value) }), h('span', { text: label })); };
        replace(host,
            stat(showcase ? (val(showcase, 'seriesCount') || 0) + (val(showcase, 'movieCount') || 0) : null, 'Anime'),
            stat(showcase ? val(showcase, 'seriesCount') || 0 : null, 'Serie'),
            stat(showcase ? val(showcase, 'movieCount') || 0 : null, 'Film'),
            stat(titles ? val(titles, 'episodeCount') || 0 : null, 'Episodi'));
    }

    function renderHealth() {
        var host = el('acHomeHealthBody');
        if (!host) return;
        var data = AC.library.data();
        if (!data.loadedAt) {
            replace(host, h('div', { class: 'ac-health-rings' }, ui.skeleton(124), ui.skeleton(124)));
            return;
        }
        var titles = data.titles, quality = data.quality;
        var episodes = val(titles, 'episodeCount') || 0;
        var missing = val(titles, 'missingTitleCount') || 0;
        var toCheck = AC.library.titlesToCheck(titles);
        var items = val(quality, 'itemCount') || 0;
        var italian = val(quality, 'italianCount') || 0;
        var legend = function (rows) {
            return h('div', { class: 'ac-legend' }, rows.filter(Boolean).map(function (row) {
                return h('span', null, h('i', { class: row[2] ? 'is-' + row[2] : '' }), num(row[0]) + ' ' + row[1]);
            }));
        };
        replace(host,
            data.failures.length ? ui.callout('Analisi incompleta', data.failures.join(' · '), 'warn') : null,
            h('div', { class: 'ac-health-rings' },
                h('div', { class: 'ac-ring-block' },
                    titles ? ui.ring(episodes - missing, episodes, { label: 'Titoli in italiano' }) : ui.skeleton(96),
                    h('div', { class: 'ac-ring-copy' }, h('strong', { text: 'Titoli degli episodi' }), titles ? legend([
                        [Math.max(episodes - missing, 0), 'presenti', 'ok'],
                        toCheck ? [toCheck, 'da verificare', 'warn'] : null,
                        val(titles, 'waitingTitleCount') ? [val(titles, 'waitingTitleCount'), 'in attesa di AnimeClick', 'info'] : null
                    ]) : null)),
                h('div', { class: 'ac-ring-block' },
                    quality ? ui.ring(italian, items, { label: 'Trame in italiano' }) : ui.skeleton(96),
                    h('div', { class: 'ac-ring-copy' }, h('strong', { text: 'Trame' }), quality ? legend([
                        [italian, 'in italiano', 'ok'],
                        val(quality, 'repairableCount') ? [val(quality, 'repairableCount'), 'da completare', 'warn'] : null,
                        val(quality, 'noSourceCount') ? [val(quality, 'noSourceCount'), 'senza fonte', ''] : null
                    ]) : null))));
    }

    function todoItem(iconName, tone, title, text, action) {
        return h('li', { class: 'ac-list-item' },
            h('span', { class: 'ac-list-icon is-' + tone }, icon(iconName)),
            h('div', null, h('strong', { text: title }), text ? h('span', { class: 'ac-hint', text: text }) : null),
            action || null);
    }

    function renderTodo() {
        var host = el('acHomeTodo');
        if (!host) return;
        var items = [];
        var config = AC.settings.saved() || {};
        var data = AC.library.data();
        var libs = libraries.rows;
        if (libs) {
            var active = libs.filter(function (lib) { return val(lib, 'state') === 'active'; });
            var partial = libs.filter(function (lib) { return val(lib, 'state') === 'partial'; });
            if (!libs.length) {
                items.push(todoItem('folder', 'bad', 'Nessuna libreria video', 'Crea in Jellyfin una libreria di serie o film per i tuoi anime.'));
            } else if (!active.length && !partial.length) {
                items.push(todoItem('folder', 'bad', 'AnimeClick non è attivo in nessuna libreria', 'Attivalo nelle librerie anime qui sotto.',
                    ui.button('Vedi', { small: true, onClick: function () { el('acHomeLibraries').scrollIntoView({ behavior: 'smooth' }); } })));
            } else if (partial.length) {
                items.push(todoItem('folder', 'warn', 'Attivazione da completare', plural(partial.length, 'libreria usa', 'librerie usano') + ' AnimeClick, ma non come primo provider.',
                    ui.button('Vedi', { small: true, onClick: function () { el('acHomeLibraries').scrollIntoView({ behavior: 'smooth' }); } })));
            }
        }
        if (!config.TmdbApiKey) {
            items.push(todoItem('key', 'warn', 'Aggiungi la chiave TMDB', 'Consigliata: identifica meglio le opere e porta immagini ad alta risoluzione.',
                ui.button('Fonti', { small: true, onClick: function () { AC.app.go('sources'); } })));
        }
        if (config.EnableEpisodeTitles === false) {
            items.push(todoItem('title', 'warn', 'I titoli degli episodi sono spenti', 'Nessun titolo episodio viene scritto finché resta così.',
                ui.button('Preferenze', { small: true, onClick: function () { AC.app.go('preferences'); } })));
        }
        if (data.loadedAt) {
            var toCheck = AC.library.titlesToCheck(data.titles);
            if (toCheck && !AC.library.isActive('titles')) {
                items.push(todoItem('title', 'red', plural(toCheck, 'titolo da sistemare', 'titoli da sistemare'), 'Un clic e il server li ricontrolla tutti.',
                    ui.button('Libreria', { small: true, onClick: function () { AC.app.go('library'); AC.library.setFilter('titles'); } })));
            }
            var repairable = val(data.quality, 'repairableCount');
            if (repairable && !AC.library.isActive('synopses')) {
                items.push(todoItem('book', 'red', plural(repairable, 'trama da completare', 'trame da completare'), 'Inglesi o vuote, con una fonte disponibile.',
                    ui.button('Libreria', { small: true, onClick: function () { AC.app.go('library'); AC.library.setFilter('synopses'); } })));
            }
            var unidentified = AC.library.entries().filter(function (e) { return e.status === 'unidentified'; }).length;
            if (unidentified) {
                items.push(todoItem('link', 'bad', plural(unidentified, 'anime da identificare', 'anime da identificare'), 'Jellyfin non ha trovato la scheda AnimeClick.',
                    ui.button('Libreria', { small: true, onClick: function () { AC.app.go('library'); AC.library.setFilter('unidentified'); } })));
            }
        }
        if (!items.length) {
            items.push(todoItem('check', 'ok', libs && data.loadedAt ? 'Tutto in ordine' : 'Controllo in corso…',
                libs && data.loadedAt ? 'Niente da fare per ora: buona visione!' : 'Sto leggendo librerie e analisi locali.'));
        }
        replace(host, items);
    }

    function renderSources() {
        var host = el('acHomeSources');
        if (!host) return;
        var state = AC.settings.sourceState();
        var pill = function (name, on, text) {
            return h('button', { type: 'button', class: 'ac-source-pill', on: { click: function () { AC.app.go('sources'); } } },
                h('span', { class: 'ac-led' + (on ? ' is-ok' : '') }),
                h('div', null, h('strong', { text: name }), h('small', { class: on ? 'is-ok' : '', text: text || (on ? 'Configurata' : 'Non configurata') })));
        };
        replace(host,
            pill('AnimeClick', true, 'Sempre attiva'),
            pill('TMDB', state.tmdb),
            pill('Fanart', state.fanart),
            pill('TheTVDB', state.tvdb),
            pill('Traduzione AI', state.ai));
    }

    function renderLibraries() {
        var host = el('acHomeLibrariesBody');
        if (!host) return;
        if (libraries.error && !libraries.rows) {
            replace(host, ui.callout('Librerie non disponibili', truncate(libraries.error, 240), 'warn'));
            return;
        }
        if (!libraries.rows) {
            replace(host, h('div', { class: 'ac-libs' }, ui.skeleton(110), ui.skeleton(110)));
            return;
        }
        if (!libraries.rows.length) {
            replace(host, ui.empty('Nessuna libreria di serie o film', 'Crea una libreria in Jellyfin (Dashboard → Librerie), poi torna qui.'));
            return;
        }
        replace(host, h('div', { class: 'ac-libs' }, libraries.rows.map(function (lib) {
            var state = libraryState(lib);
            var enable = null;
            if (val(lib, 'canEnable')) {
                enable = ui.button(val(lib, 'state') === 'active' ? 'Aggiungi alle immagini' : 'Attiva AnimeClick', { small: true, variant: val(lib, 'state') === 'active' ? 'ghost' : 'primary', icon: 'play' });
                enable.addEventListener('click', function () {
                    confirmEnable([val(lib, 'name')]).then(function (yes) {
                        if (!yes) return;
                        ui.setBusy(enable, true, 'Attivazione…');
                        enableLibrary(lib).then(function () {
                            ui.toast('AnimeClick attivo in «' + val(lib, 'name') + '»', 'success');
                        }).catch(function (error) {
                            ui.toast(truncate(error.message, 240), 'error');
                            ui.setBusy(enable, false);
                        });
                    });
                });
            }
            return h('div', { class: 'ac-lib' },
                h('div', { class: 'ac-lib-head' },
                    h('span', { class: 'ac-list-icon' + (state.tone ? ' is-' + state.tone : '') }, icon(val(lib, 'collectionType') === 'movies' ? 'film' : 'tv')),
                    h('div', null, h('strong', { text: val(lib, 'name') }), h('span', { class: 'ac-muted', text: COLLECTION_NAMES[val(lib, 'collectionType')] || 'Contenuti misti' })),
                    ui.badge(state.label, state.tone, state.icon)),
                libraryTypes(lib),
                enable ? h('div', { class: 'ac-row' }, enable) : null);
        })));
    }

    function renderJobs() {
        AC.library.updateJobPanel('titles', 'acHome');
        AC.library.updateJobPanel('synopses', 'acHome');
        var error = el('acHomeJobError');
        if (error) error.textContent = AC.library.jobError();
    }

    function build(container) {
        page = container.closest('#AnimeClickConfigPage') || container.ownerDocument;
        container.appendChild(h('div', { id: 'acHomeHero' }, h('div', { class: 'ac-hero' }, h('div', { class: 'ac-hero-body' }, h('div', { class: 'ac-stack', style: 'width:100%' }, ui.skeleton(26), ui.skeleton(54), ui.skeleton(70))))));
        container.appendChild(h('section', { id: 'acHomeRail', class: 'ac-rail', 'aria-label': 'Ultimi anime aggiornati', hidden: true }));

        var health = ui.card({ icon: 'activity', title: 'Salute della collezione', text: 'Quanta parte della libreria è già in italiano.', cls: 'ac-health ac-card--accent',
            aside: ui.button('Apri la libreria', { small: true, variant: 'ghost', icon: 'arrowRight', onClick: function () { AC.app.go('library'); } }) });
        health.body.appendChild(h('div', { id: 'acHomeHealthBody', class: 'ac-stack' }));
        health.body.appendChild(h('div', { id: 'acHomeStats', class: 'ac-stats' }));

        var activity = ui.card({ icon: 'refresh', title: 'Attività', text: 'Continuano sul server anche a pagina chiusa.', cls: 'ac-activity-card' });
        activity.body.appendChild(h('div', { class: 'ac-jobs' }, AC.library.jobPanel('titles', 'acHome'), AC.library.jobPanel('synopses', 'acHome')));
        activity.body.appendChild(h('p', { id: 'acHomeJobError', class: 'ac-status is-bad', role: 'status' }));

        var todo = ui.card({ icon: 'checklist', title: 'Da fare', text: 'Quello che merita un’occhiata adesso.', cls: 'ac-todo' });
        todo.body.appendChild(h('ul', { id: 'acHomeTodo', class: 'ac-list' }));

        var sources = ui.card({ icon: 'plug', title: 'Fonti', text: 'AnimeClick funziona da solo; le altre fonti completano ciò che manca.', cls: 'ac-sources-card' });
        sources.body.appendChild(h('div', { id: 'acHomeSources', class: 'ac-source-mini' }));

        var refreshLibs = ui.button('Aggiorna', { small: true, variant: 'ghost', icon: 'refresh', id: 'acBtnRefreshLibraries' });
        refreshLibs.addEventListener('click', function () {
            ui.setBusy(refreshLibs, true, 'Lettura…');
            loadLibraries(true).catch(function () { /* shown in the card */ }).finally(function () { ui.setBusy(refreshLibs, false); });
        });
        var libs = ui.card({ id: 'acHomeLibraries', icon: 'folder', title: 'Librerie', text: 'Dove AnimeClick fornisce i metadati. Attivalo solo nelle librerie anime.', cls: 'ac-libraries-card', aside: refreshLibs });
        libs.body.appendChild(h('div', { id: 'acHomeLibrariesBody' }));

        container.appendChild(h('div', { class: 'ac-dash' }, health.card, activity.card, todo.card, sources.card, libs.card));

        AC.bus.on('reports', function () { renderHealth(); renderStats(); renderTodo(); });
        AC.bus.on('jobs', function () { renderJobs(); renderTodo(); });
        AC.bus.on('libraries', function () { renderLibraries(); renderTodo(); });
        AC.bus.on('config', function () { renderSources(); renderTodo(); });
        AC.bus.on('library-changed', function () { showcaseAt = 0; });
        renderHealth();
        renderStats();
        renderTodo();
        renderLibraries();
        renderJobs();
    }

    function enter() {
        renderSources();
        loadShowcase(false).then(function () {
            renderHero();
            renderRail();
            renderStats();
        }).catch(function (error) {
            replace(el('acHomeHero'), ui.callout('Vetrina non disponibile', truncate(error.message, 240), 'warn'));
        });
        loadLibraries(false).catch(function () { /* shown in the card */ });
        AC.library.loadReports(false);
        AC.library.poll();
    }

    AC.home = {
        build: build,
        attach: function (pageElement) { page = pageElement; },
        enter: enter,
        leave: stopHero,
        reset: function () { showcase = null; showcaseAt = 0; libraries.rows = null; libraries.error = null; stopHero(); }
    };

    AC.libraries = {
        load: loadLibraries,
        rows: function () { return libraries.rows; },
        error: function () { return libraries.error; },
        enable: enableLibrary,
        confirmEnable: confirmEnable,
        state: libraryState,
        types: libraryTypes,
        collectionName: function (lib) { return COLLECTION_NAMES[val(lib, 'collectionType')] || 'Contenuti misti'; }
    };
}());
