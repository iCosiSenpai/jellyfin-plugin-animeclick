/* AnimeClick per Jellyfin — libreria: dati condivisi, attività in background, griglia e dettaglio. */
(function () {
    'use strict';

    var AC = window.AnimeClickUI;
    var h = AC.dom.h, icon = AC.dom.icon, clear = AC.dom.clear, replace = AC.dom.replace;
    var val = AC.util.val, list = AC.util.list, num = AC.util.num, pct = AC.util.pct;
    var truncate = AC.util.truncate, plural = AC.util.plural, normalize = AC.util.normalize;
    var ui = AC.ui, api = AC.api;

    var PAGE_SIZE = 36;
    var EPISODE_PAGE = 20;
    var REREAD_LIMIT = 10;

    var page = null;

    function el(id) {
        return page ? page.querySelector('#' + id) : null;
    }

    function key(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    /* ===== shared data ===== */

    var data = { titles: null, quality: null, catalog: null, loadedAt: null, failures: [], loading: null };

    /** Loads the two local audits and the catalog together. Concurrent callers share one request. */
    function loadReports(force) {
        if (data.loading) return data.loading;
        if (!force && data.loadedAt) return Promise.resolve(data);
        AC.bus.emit('reports-loading');
        data.loading = Promise.allSettled([
            api.request('GET', 'Plugins/AnimeClick/LibraryAudit'),
            api.request('GET', 'Plugins/AnimeClick/LibraryQualityAudit'),
            api.request('GET', 'Plugins/AnimeClick/Catalog')
        ]).then(function (results) {
            var failures = [];
            if (results[0].status === 'fulfilled') data.titles = results[0].value; else failures.push('titoli: ' + results[0].reason.message);
            if (results[1].status === 'fulfilled') data.quality = results[1].value; else failures.push('trame: ' + results[1].reason.message);
            if (results[2].status === 'fulfilled') data.catalog = list(results[2].value); else failures.push('catalogo: ' + results[2].reason.message);
            data.failures = failures;
            data.loadedAt = new Date();
            entriesCache = null;
            return data;
        }).finally(function () {
            data.loading = null;
            AC.bus.emit('reports', data);
        });
        return data.loading;
    }

    var entriesCache = null;

    /** One entry per anime, joining the catalog with the title and synopsis audits. */
    function entries() {
        if (entriesCache) return entriesCache;
        var map = Object.create(null);
        function entry(id, name, year) {
            var k = key(id);
            if (!map[k]) map[k] = { id: id, key: k, name: name || 'Senza titolo', year: year || null, type: 'Series', animeClickId: null, hasPrimaryImage: true };
            return map[k];
        }
        list(data.catalog).forEach(function (item) {
            var e = entry(val(item, 'id'), val(item, 'name'), val(item, 'year'));
            e.type = val(item, 'type') || 'Series';
            e.animeClickId = val(item, 'animeClickId');
            e.hasPrimaryImage = !!val(item, 'hasPrimaryImage');
            e.updatedAt = val(item, 'updatedAt');
        });
        list(val(data.titles, 'series')).forEach(function (series) {
            var e = entry(val(series, 'id'), val(series, 'name'), val(series, 'year'));
            e.titles = series;
            if (!e.animeClickId) e.animeClickId = val(series, 'animeClickId') || null;
        });
        list(val(data.quality, 'series')).forEach(function (group) {
            var e = entry(val(group, 'id'), val(group, 'name'), val(group, 'year'));
            e.synopses = group;
            var first = list(val(group, 'items'))[0];
            if (first && val(first, 'itemType') === 'Movie') e.type = 'Movie';
            if (!e.animeClickId && !e.titles) e.animeClickId = 'identificato';
        });
        entriesCache = Object.keys(map).map(function (k) { return measure(map[k]); });
        return entriesCache;
    }

    function measure(e) {
        var titles = e.titles;
        e.titleTotal = titles ? val(titles, 'episodeCount') || 0 : 0;
        e.titleMissing = titles ? val(titles, 'missingTitleCount') || 0 : 0;
        var group = e.synopses;
        var english = group ? val(group, 'englishCount') || 0 : 0;
        var missing = group ? val(group, 'missingCount') || 0 : 0;
        e.synTotal = group ? val(group, 'itemCount') || 0 : (titles ? e.titleTotal + 1 : 1);
        e.synBad = english + missing;
        e.synUnknown = group ? val(group, 'unknownCount') || 0 : 0;
        e.synGood = Math.max(e.synTotal - e.synBad - e.synUnknown, 0);
        e.identified = !!e.animeClickId && !(titles && val(titles, 'reason') === 'NotIdentified' && !val(titles, 'animeClickId'));
        e.work = e.titleMissing + e.synBad;
        e.status = !e.identified ? 'unidentified' : e.work > 0 ? 'attention' : 'complete';
        return e;
    }

    /* ===== background activities ===== */

    var jobs = { titles: { key: 'titles', state: 'Idle' }, synopses: { key: 'synopses', state: 'Idle' } };
    var jobTimer = null;
    var jobPending = false;
    var jobError = '';

    function isActive(name) {
        return !!jobs[name] && !!val(jobs[name], 'isActive');
    }

    function shouldPoll() {
        return !!page && page.isConnected && !document.hidden && AC.app && ['home', 'library'].indexOf(AC.app.current()) > -1;
    }

    function stopPolling() {
        clearTimeout(jobTimer);
        jobTimer = null;
    }

    function poll() {
        stopPolling();
        if (!shouldPoll() || jobPending) return Promise.resolve();
        jobPending = true;
        return api.request('GET', 'Plugins/AnimeClick/Activities', undefined, 15000).then(function (rows) {
            var finished = false;
            list(rows).forEach(function (job) {
                var name = val(job, 'key');
                if (!jobs[name]) return;
                var wasActive = isActive(name);
                jobs[name] = job;
                if (wasActive && !isActive(name)) finished = true;
            });
            jobError = '';
            AC.bus.emit('jobs', jobs);
            if (finished) loadReports(true);
        }).catch(function (error) {
            jobError = 'Stato non disponibile: ' + error.message + ' Il controllo riprenderà automaticamente.';
            AC.bus.emit('jobs', jobs);
        }).finally(function () {
            jobPending = false;
            if (shouldPoll()) jobTimer = setTimeout(poll, isActive('titles') || isActive('synopses') ? 2000 : 10000);
        });
    }

    var STATE_LABELS = {
        Idle: 'Inattiva', Queued: 'In attesa', Running: 'In corso', Cancelling: 'Interruzione', Cancelled: 'Interrotta',
        Completed: 'Completata', Partial: 'Da verificare', Failed: 'Errore'
    };
    var JOB_NAMES = { titles: 'Titoli episodio', synopses: 'Trame e sinossi' };

    function jobBadgeTone(state) {
        return state === 'Completed' ? 'ok' : state === 'Failed' || state === 'Partial' ? 'warn' : state === 'Running' || state === 'Queued' ? 'red' : '';
    }

    /** One activity panel. `prefix` keeps IDs unique between the home dashboard and the library page. */
    function jobPanel(name, prefix) {
        var cancel = ui.button('Interrompi', { id: prefix + 'Cancel_' + name, small: true, variant: 'ghost', icon: 'stop', ariaLabel: 'Interrompi ' + JOB_NAMES[name].toLowerCase() });
        cancel.hidden = true;
        cancel.addEventListener('click', function () { cancelJob(name, cancel); });
        var bar = ui.progress(0);
        bar.id = prefix + 'Progress_' + name;
        bar.setAttribute('aria-label', 'Avanzamento ' + JOB_NAMES[name].toLowerCase());
        bar.hidden = true;
        var node = h('div', { class: 'ac-job', id: prefix + 'Job_' + name },
            h('div', { class: 'ac-row' }, h('strong', { class: 'ac-grow', text: JOB_NAMES[name] }), h('span', { id: prefix + 'Badge_' + name }, ui.badge('Inattiva')), cancel),
            bar,
            h('p', { class: 'ac-job-message', id: prefix + 'Message_' + name, role: 'status', 'aria-live': 'polite', text: 'Nessuna attività in corso.' }),
            h('p', { class: 'ac-job-counts', id: prefix + 'Counts_' + name }));
        updateJobPanel(name, prefix);
        return node;
    }

    function updateJobPanel(name, prefix) {
        var job = jobs[name];
        var root = el(prefix + 'Job_' + name);
        if (!root) return;
        var state = val(job, 'state') || 'Idle';
        var active = isActive(name);
        root.classList.toggle('is-running', active);
        replace(el(prefix + 'Badge_' + name), ui.badge(STATE_LABELS[state] || state, jobBadgeTone(state)));
        var message = val(job, 'message') || 'Nessuna attività in corso.';
        var messageNode = el(prefix + 'Message_' + name);
        if (messageNode.textContent !== message) messageNode.textContent = message;
        var bar = el(prefix + 'Progress_' + name);
        var total = val(job, 'total') || 0;
        bar.hidden = state === 'Idle';
        ui.setProgress(bar, val(job, 'progress') || 0, state === 'Queued' || (active && !total));
        var counts = el(prefix + 'Counts_' + name);
        if (total) {
            replace(counts,
                h('span', null, h('b', { text: num(val(job, 'processed')) + '/' + num(total) }), ' verificati'),
                h('span', null, h('b', { text: num(val(job, 'applied')) }), ' aggiornati'),
                h('span', null, h('b', { text: num(val(job, 'skipped')) }), ' saltati o in attesa'),
                h('span', null, h('b', { text: num(val(job, 'errors')) }), ' errori'));
        } else {
            clear(counts);
        }
        var cancel = el(prefix + 'Cancel_' + name);
        cancel.hidden = !active;
        cancel.disabled = state === 'Cancelling';
    }

    function cancelJob(name, buttonNode) {
        buttonNode.disabled = true;
        api.request('POST', 'Plugins/AnimeClick/CancelActivity', { key: name }).then(poll).catch(function (error) {
            ui.toast('Interruzione non riuscita: ' + error.message, 'error');
        }).finally(function () { buttonNode.disabled = false; });
    }

    /* ===== repair actions ===== */

    /**
     * Episodes "Sistema tutti i titoli" will inspect, as the server counts them with the task's own rule.
     * Older servers did not send it: fall back to the previous estimate.
     */
    function titlesToCheck(report) {
        var checkable = val(report, 'checkableTitleCount');
        if (checkable != null) return checkable;
        return (val(report, 'alternativeTitleLookupEnabled') ? val(report, 'missingTitleCount') : val(report, 'recoverableTitleCount')) || 0;
    }

    function runTitles(buttonNode) {
        var recoverable = titlesToCheck(data.titles);
        return ui.confirm({
            title: 'Sistema tutti i titoli',
            confirmLabel: 'Avvia',
            message: [
                'Ricontrollare' + (recoverable ? ' i ' + num(recoverable) + ' episodi con titolo da sistemare' : ' gli episodi con titolo da sistemare') + '?',
                'Verranno completati i nomi vuoti, generici o derivati dal file e convertiti quelli riconoscibilmente inglesi. Titoli italiani o incerti, campi bloccati, numerazione, abbinamenti, immagini e trame sono conservati.',
                'Le fonti alternative e la traduzione AI seguono le preferenze salvate; il servizio AI può avere un costo. Il lavoro continua sul server anche se chiudi la pagina.'
            ]
        }).then(function (yes) {
            if (!yes) return;
            ui.setBusy(buttonNode, true, 'Avvio…');
            return api.request('POST', 'Plugins/AnimeClick/RunMissingTitlesTask').then(function (response) {
                ui.toast(val(response, 'message') || 'Ricontrollo dei titoli avviato', 'success');
                return poll();
            }).catch(function (error) {
                ui.toast('Avvio non riuscito: ' + truncate(error.message, 200), 'error');
            }).finally(function () { ui.setBusy(buttonNode, false); refreshControls(); });
        });
    }

    function runSynopses(buttonNode) {
        var actionable = val(data.quality, 'repairableCount') || 0;
        return ui.confirm({
            title: 'Completa tutte le sinossi',
            confirmLabel: 'Avvia',
            message: [
                'Avviare il completamento automatico' + (actionable ? ' di ' + num(actionable) + ' elementi' : '') + '?',
                'Procede a lotti da ' + (val(data.quality, 'maximumRepairItems') || 100) + ' distribuiti fra serie diverse e si ferma da solo quando non resta niente da fare. Non tocca testi italiani né campi bloccati e continua anche se chiudi la pagina.'
            ]
        }).then(function (yes) {
            if (!yes) return;
            ui.setBusy(buttonNode, true, 'Avvio…');
            return api.request('POST', 'Plugins/AnimeClick/RunSynopsisRepairTask').then(function (response) {
                ui.toast(val(response, 'message') || 'Completamento delle sinossi avviato', 'success');
                return poll();
            }).catch(function (error) {
                ui.toast('Avvio non riuscito: ' + truncate(error.message, 200), 'error');
            }).finally(function () { ui.setBusy(buttonNode, false); refreshControls(); });
        });
    }

    /* One item per group per pass, so a single long series without sources cannot fill a whole batch. */
    function suppressedIds() {
        var report = data.quality;
        if (!report) return [];
        var maximum = val(report, 'maximumRepairItems') || 100;
        var buckets = list(val(report, 'series')).map(function (group) {
            return list(val(group, 'items')).filter(function (item) {
                return val(item, 'suppressed') && val(item, 'repairState') === 'no-source' && val(item, 'id');
            });
        }).filter(function (items) { return items.length; });
        var ids = [];
        for (var depth = 0; ids.length < maximum; depth++) {
            var progressed = false;
            for (var index = 0; index < buckets.length && ids.length < maximum; index++) {
                if (depth < buckets[index].length) { ids.push(val(buckets[index][depth], 'id')); progressed = true; }
            }
            if (!progressed) break;
        }
        return ids;
    }

    function retryNoSource(buttonNode) {
        var ids = suppressedIds();
        if (!ids.length) { ui.toast('Nessun elemento è stato escluso per mancanza di fonti.', 'error'); return; }
        ui.confirm({
            title: 'Riprova gli elementi senza fonte',
            confirmLabel: 'Riprova',
            message: ['Accodare di nuovo ' + num(ids.length) + ' elementi per cui nessuna fonte aveva la trama?',
                'Utile dopo aver aggiunto una chiave TMDB o TheTVDB o configurato la traduzione. Il server ricontrolla lingua, blocchi e impostazioni prima di accodarli.']
        }).then(function (yes) {
            if (!yes) return;
            ui.setBusy(buttonNode, true, 'Accodamento…');
            api.request('POST', 'Plugins/AnimeClick/LibraryQualityRepair', { itemIds: ids, force: true }).then(function (result) {
                var queued = val(result, 'queuedCount') || 0;
                ui.toast(queued ? num(queued) + ' elementi accodati: analizza di nuovo fra qualche minuto.' : 'Nessun elemento risulta ancora riparabile.', queued ? 'success' : 'error');
            }).catch(function (error) {
                ui.toast(truncate(error.message, 240), 'error');
            }).finally(function () { ui.setBusy(buttonNode, false); });
        });
    }

    /* ===== library view ===== */

    var view = { query: '', filter: 'attention', sort: 'work', limit: PAGE_SIZE, scanning: false };
    var FILTERS = [
        ['attention', 'Da sistemare'], ['titles', 'Titoli mancanti'], ['synopses', 'Trame da completare'],
        ['unidentified', 'Da identificare'], ['complete', 'Completi'], ['all', 'Tutti']
    ];

    function matches(e, filter) {
        if (filter === 'attention') return e.status !== 'complete';
        if (filter === 'titles') return e.titleMissing > 0;
        if (filter === 'synopses') return e.synBad > 0;
        if (filter === 'unidentified') return e.status === 'unidentified';
        if (filter === 'complete') return e.status === 'complete';
        return true;
    }

    function filtered() {
        var query = normalize(view.query);
        var rows = entries().filter(function (e) {
            if (!matches(e, view.filter)) return false;
            if (!query) return true;
            return normalize([e.name, e.year, e.animeClickId, e.titles ? val(e.titles, 'reasonLabel') : ''].join(' ')).indexOf(query) > -1;
        });
        var byName = function (a, b) { return a.name.localeCompare(b.name, 'it', { sensitivity: 'base' }); };
        rows.sort(view.sort === 'name' ? byName
            : view.sort === 'year' ? function (a, b) { return (b.year || 0) - (a.year || 0) || byName(a, b); }
                : view.sort === 'recent' ? function (a, b) { return String(b.updatedAt || '').localeCompare(String(a.updatedAt || '')) || byName(a, b); }
                    : function (a, b) { return (b.status === 'unidentified') - (a.status === 'unidentified') || b.work - a.work || byName(a, b); });
        return rows;
    }

    function cardBadge(e) {
        if (e.status === 'unidentified') return ui.badge('Da identificare', 'bad', 'alert');
        if (e.titleMissing) return ui.badge(plural(e.titleMissing, 'titolo', 'titoli'), 'warn');
        if (e.synBad) return ui.badge(plural(e.synBad, 'trama', 'trame'), 'warn');
        return ui.badge('Completo', 'ok', 'check');
    }

    function animeCard(e) {
        var cover = ui.poster(e, { height: 360 });
        cover.appendChild(h('span', { class: 'ac-poster-badge' }, cardBadge(e)));
        return h('button', { type: 'button', class: 'ac-poster-card ac-anime-card', data: { id: e.key }, on: { click: function () { openDetail(e.key); } } },
            cover,
            h('span', { class: 'ac-poster-title', text: e.name }),
            h('span', { class: 'ac-poster-meta', text: (e.type === 'Movie' ? 'Film' : 'Serie') + (e.year ? ' · ' + e.year : '') }),
            h('span', { class: 'ac-meters' },
                e.type === 'Movie' ? null : ui.meterRow('Titoli', e.titleTotal - e.titleMissing, e.titleTotal),
                ui.meterRow('Trame', e.synGood, e.synTotal)));
    }

    function build(container) {
        page = container.closest('#AnimeClickConfigPage') || container.ownerDocument;
        var scan = ui.button('Aggiorna analisi', { id: 'acLibraryScan', icon: 'refresh', variant: 'ghost' });
        scan.addEventListener('click', function () { scanNow(); });
        container.appendChild(ui.sectionHead('ライブラリ', 'La tua libreria', 'Ogni anime, a colpo d’occhio',
            'Lo stato di titoli e trame per ogni serie e film. L’analisi legge solo i dati locali e non modifica nulla.', scan));
        var scanBar = ui.progress(0, true);
        scanBar.id = 'acLibraryScanProgress';
        scanBar.setAttribute('aria-label', 'Analisi locale della libreria');
        scanBar.hidden = true;
        container.appendChild(h('div', { class: 'ac-stack-s' }, scanBar, h('p', { id: 'acLibraryScanState', class: 'ac-status', role: 'status', text: 'L’analisi legge i dati locali e non modifica la libreria.' })));
        container.appendChild(h('div', { id: 'acLibraryWarnings' }));

        var titles = ui.card({ icon: 'title', title: 'Titoli degli episodi', text: 'Completa i nomi mancanti o generici e cerca l’italiano per quelli inglesi.', id: 'acRepairTitles' });
        titles.body.appendChild(h('div', { class: 'ac-repair-top' }, h('div', { id: 'acTitlesRing' }), h('div', { id: 'acAuditSummary', class: 'ac-numbers', 'aria-label': 'Riepilogo dei titoli' })));
        var runTitlesButton = ui.button('Sistema tutti i titoli', { id: 'acBtnRunTitles', variant: 'primary', icon: 'sparkles' });
        runTitlesButton.addEventListener('click', function () { runTitles(runTitlesButton); });
        titles.body.appendChild(h('div', { class: 'ac-row' }, runTitlesButton));
        titles.body.appendChild(jobPanel('titles', 'acActivity'));

        var synopses = ui.card({ icon: 'book', title: 'Trame e sinossi', text: 'Recupera le trame vuote o in inglese dalle fonti abilitate.', id: 'acRepairSynopses' });
        synopses.body.appendChild(h('div', { class: 'ac-repair-top' }, h('div', { id: 'acSynopsesRing' }), h('div', { id: 'acQualitySummary', class: 'ac-numbers', 'aria-label': 'Riepilogo delle trame' })));
        var runSynopsesButton = ui.button('Completa tutte le sinossi', { id: 'acBtnQualityRepair', variant: 'primary', icon: 'sparkles' });
        runSynopsesButton.addEventListener('click', function () { runSynopses(runSynopsesButton); });
        var retry = ui.button('Riprova senza fonte', { id: 'acBtnQualityRetryNoSource', variant: 'ghost', icon: 'undo' });
        retry.addEventListener('click', function () { retryNoSource(retry); });
        synopses.body.appendChild(h('div', { class: 'ac-row' }, runSynopsesButton, retry));
        synopses.body.appendChild(jobPanel('synopses', 'acActivity'));

        container.appendChild(h('div', { class: 'ac-repair-grid' }, titles.card, synopses.card));
        container.appendChild(h('p', { id: 'acActivityConnection', class: 'ac-status is-bad', role: 'status' }));

        var search = h('div', { class: 'ac-search' }, icon('search'),
            h('input', { id: 'acLibrarySearch', class: 'ac-input', type: 'search', placeholder: 'Cerca per titolo, anno o ID', autocomplete: 'off', 'aria-label': 'Cerca nella libreria' }));
        var sort = ui.select('acLibrarySort', null, [
            { value: 'work', label: 'Prima da sistemare' }, { value: 'name', label: 'Nome A–Z' },
            { value: 'recent', label: 'Aggiornati di recente' }, { value: 'year', label: 'Anno, dal più recente' }
        ]);
        sort.setAttribute('aria-label', 'Ordina');
        var reread = ui.button('Rileggi da AnimeClick', { id: 'acBtnRereadVisible', icon: 'refresh', variant: 'ghost', small: true, title: 'Rilegge le schede delle serie mostrate che possono ancora migliorare, fino a ' + REREAD_LIMIT + ' alla volta.' });
        reread.addEventListener('click', function () { rereadVisible(reread); });
        container.appendChild(h('div', { class: 'ac-toolbar' },
            h('div', { class: 'ac-toolbar-row' }, search, sort),
            h('div', { class: 'ac-toolbar-row' }, h('div', { id: 'acLibraryFilters', class: 'ac-chips ac-grow', role: 'group', 'aria-label': 'Filtra' }), reread),
            h('p', { id: 'acLibraryCount', class: 'ac-muted', role: 'status' })));
        container.appendChild(h('div', { id: 'acLibraryGrid', class: 'ac-anime-grid' }));
        var more = ui.button('Mostra altri', { id: 'acLibraryMore', variant: 'ghost', cls: 'ac-more' });
        more.hidden = true;
        more.addEventListener('click', function () {
            var previous = view.limit;
            view.limit += PAGE_SIZE;
            renderGrid();
            var cards = el('acLibraryGrid').querySelectorAll('.ac-anime-card');
            if (cards[previous]) cards[previous].focus();
        });
        container.appendChild(more);

        el('acLibrarySearch').addEventListener('input', function () { view.query = this.value; view.limit = PAGE_SIZE; renderGrid(); });
        sort.addEventListener('change', function () { view.sort = this.value; view.limit = PAGE_SIZE; renderGrid(); });

        AC.bus.on('reports', render);
        AC.bus.on('reports-loading', function () { renderScanning(true); });
        AC.bus.on('jobs', function () {
            updateJobPanel('titles', 'acActivity');
            updateJobPanel('synopses', 'acActivity');
            var connection = el('acActivityConnection');
            if (connection) connection.textContent = jobError;
            refreshControls();
        });
        AC.bus.on('library-changed', function () { data.loadedAt = null; });
        renderSummary();
        renderGrid();
    }

    function scanNow() {
        return loadReports(true);
    }

    function renderScanning(active) {
        view.scanning = active;
        var bar = el('acLibraryScanProgress');
        if (bar) bar.hidden = !active;
        var scan = el('acLibraryScan');
        if (scan) ui.setBusy(scan, active, 'Analisi in corso…');
        if (active) ui.status(el('acLibraryScanState'), 'Lettura locale di titoli, serie, film e trame…');
        refreshControls();
    }

    function render() {
        renderScanning(false);
        var state = el('acLibraryScanState');
        if (state) {
            if (data.failures.length) {
                ui.status(state, 'Analisi incompleta — ' + data.failures.join(' · ') + '. Premi «Aggiorna analisi» per riprovare.', 'bad');
            } else if (data.loadedAt) {
                ui.status(state, 'Analisi aggiornata alle ' + data.loadedAt.toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' }) + '. Apri un anime per i dettagli.');
            }
        }
        renderSummary();
        renderGrid();
        refreshControls();
    }

    function number(label, value, tone, filter) {
        var content = [h('b', { text: num(value) }), h('span', { text: label })];
        if (!filter) return h('div', { class: 'ac-number' + (tone ? ' is-' + tone : '') }, content);
        return h('button', {
            type: 'button', class: 'ac-number' + (tone ? ' is-' + tone : ''), title: 'Mostra questi anime',
            on: { click: function () { setFilter(filter); el('acLibraryGrid').scrollIntoView({ behavior: 'smooth', block: 'start' }); } }
        }, content);
    }

    function renderSummary() {
        var warnings = el('acLibraryWarnings');
        if (!warnings) return;
        clear(warnings);
        var titles = data.titles;
        var quality = data.quality;
        if (titles && !val(titles, 'episodeTitlesEnabled')) {
            warnings.appendChild(ui.callout('Titoli degli episodi disattivati', 'In Preferenze l’opzione dei titoli episodio è spenta: finché resta così nessun titolo viene scritto.', 'warn'));
        }

        var episodes = val(titles, 'episodeCount') || 0;
        var missing = val(titles, 'missingTitleCount') || 0;
        var alternatives = !!val(titles, 'alternativeTitleLookupEnabled');
        var toCheck = titlesToCheck(titles);
        replace(el('acTitlesRing'), titles ? ui.ring(episodes - missing, episodes, { label: 'Titoli presenti' }) : ui.skeleton(96));
        replace(el('acAuditSummary'), titles ? [
            number('Da verificare', toCheck, toCheck ? 'warn' : 'ok', toCheck ? 'titles' : null),
            number('In attesa di AnimeClick', val(titles, 'waitingTitleCount') || 0, 'info'),
            number(alternatives ? 'Assenti su AnimeClick' : 'Senza titolo disponibile', val(titles, 'unavailableTitleCount') || 0, ''),
            number('Titoli presenti', Math.max(episodes - missing, 0), 'ok')
        ] : null);

        var items = val(quality, 'itemCount') || 0;
        var italian = val(quality, 'italianCount') || 0;
        var repairable = val(quality, 'repairableCount') || 0;
        replace(el('acSynopsesRing'), quality ? ui.ring(italian, items, { label: 'Trame in italiano' }) : ui.skeleton(96));
        var cells = quality ? [
            number('In italiano', italian, 'ok'),
            number('Da completare', repairable, repairable ? 'warn' : 'ok', repairable ? 'synopses' : null)
        ] : [];
        if (quality && val(quality, 'waitingTranslationCount')) cells.push(number('In traduzione', val(quality, 'waitingTranslationCount'), 'info'));
        if (quality && val(quality, 'noSourceCount')) cells.push(number('Senza fonte', val(quality, 'noSourceCount'), ''));
        if (quality && val(quality, 'unknownCount')) cells.push(number('Lingua incerta', val(quality, 'unknownCount'), ''));
        if (quality && val(quality, 'lockedCount')) cells.push(number('Bloccate', val(quality, 'lockedCount'), 'warn'));
        replace(el('acQualitySummary'), cells);
        renderFilters();
    }

    function setFilter(filter) {
        view.filter = filter;
        view.limit = PAGE_SIZE;
        renderFilters();
        renderGrid();
    }

    function renderFilters() {
        var host = el('acLibraryFilters');
        if (!host) return;
        var all = entries();
        replace(host, FILTERS.map(function (filter) {
            var count = all.filter(function (e) { return matches(e, filter[0]); }).length;
            return h('button', {
                type: 'button', class: 'ac-chip', id: 'acFilter_' + filter[0], 'aria-pressed': view.filter === filter[0] ? 'true' : 'false',
                on: { click: function () { setFilter(filter[0]); } }
            }, filter[1], h('b', { text: num(count) }));
        }));
    }

    function renderGrid() {
        var grid = el('acLibraryGrid');
        if (!grid) return;
        clear(grid);
        var more = el('acLibraryMore');
        if (!data.loadedAt) {
            for (var i = 0; i < 6; i++) grid.appendChild(h('div', { class: 'ac-stack-s' }, ui.skeleton(230), ui.skeleton(14)));
            more.hidden = true;
            return;
        }
        var rows = filtered();
        var count = el('acLibraryCount');
        if (count) count.textContent = rows.length ? 'Mostrati ' + num(Math.min(rows.length, view.limit)) + ' di ' + plural(rows.length, 'anime', 'anime') + '.' : '';
        if (!entries().length) {
            grid.appendChild(ui.empty('Nessun anime usa ancora AnimeClick',
                'Attiva AnimeClick nelle librerie anime e aggiorna i metadati: le serie compariranno qui.',
                ui.button('Controlla le librerie', { icon: 'folder', onClick: function () { AC.app.go('home'); var target = el('acHomeLibraries'); if (target) target.scrollIntoView({ behavior: 'smooth' }); } })));
        } else if (!rows.length) {
            grid.appendChild(ui.empty(view.query ? 'Nessun risultato' : view.filter === 'attention' ? 'Tutto in ordine!' : 'Niente in questo filtro',
                view.query ? 'Nessun anime corrisponde alla ricerca e al filtro scelti.' : view.filter === 'attention' ? 'Titoli e trame risultano completi: buona visione.' : 'Prova un altro filtro.'));
        } else {
            var fragment = document.createDocumentFragment();
            rows.slice(0, view.limit).forEach(function (e) { fragment.appendChild(animeCard(e)); });
            grid.appendChild(fragment);
        }
        more.hidden = rows.length <= view.limit;
        if (!more.hidden) more.textContent = 'Mostra altri ' + Math.min(PAGE_SIZE, rows.length - view.limit);
    }

    function refreshControls() {
        var titlesButton = el('acBtnRunTitles');
        if (!titlesButton || titlesButton.getAttribute('aria-busy') === 'true') return;
        var titlesActive = isActive('titles');
        titlesButton.disabled = titlesActive || view.scanning || !data.titles || !val(data.titles, 'episodeTitlesEnabled');
        titlesButton.lastChild.textContent = titlesActive ? 'Ricontrollo in corso…' : 'Sistema tutti i titoli';

        var synopsesButton = el('acBtnQualityRepair');
        var repairable = val(data.quality, 'repairableCount') || 0;
        if (synopsesButton.getAttribute('aria-busy') !== 'true') {
            var synopsesActive = isActive('synopses');
            synopsesButton.disabled = synopsesActive || view.scanning || !repairable;
            synopsesButton.lastChild.textContent = synopsesActive ? 'Completamento in corso…'
                : repairable ? 'Completa tutte le sinossi (' + num(repairable) + ')' : 'Niente da completare';
        }
        var retry = el('acBtnQualityRetryNoSource');
        if (retry.getAttribute('aria-busy') !== 'true') {
            var suppressed = suppressedIds().length;
            retry.disabled = !suppressed || isActive('synopses') || view.scanning;
            retry.lastChild.textContent = suppressed ? 'Riprova senza fonte (' + num(suppressed) + ')' : 'Riprova senza fonte';
        }
        var reread = el('acBtnRereadVisible');
        if (reread && reread.getAttribute('aria-busy') !== 'true') reread.disabled = view.scanning || !rereadCandidates().length || isActive('titles');
    }

    /* ===== re-reading series from AnimeClick ===== */

    // Rereading only helps where the cached verdict may be wrong: a card that lists episodes without
    // titles has already been proved at the source.
    var REREADABLE = { CatalogNotCached: true, NotMatched: true, RowVanished: true, PendingRefresh: true, NumberingCollision: true };

    function canReread(e) {
        if (!e.titles || !val(e.titles, 'animeClickId') || !e.titleMissing) return false;
        var reasons = [val(e.titles, 'reason')].concat(list(val(e.titles, 'seasons')).map(function (season) { return val(season, 'reason'); }));
        return reasons.some(function (reason) { return REREADABLE[reason]; });
    }

    function rereadCandidates() {
        return data.loadedAt ? filtered().filter(canReread).slice(0, REREAD_LIMIT) : [];
    }

    function replaceSeries(fresh) {
        var series = list(val(data.titles, 'series'));
        var id = key(val(fresh, 'id'));
        for (var i = 0; i < series.length; i++) {
            if (key(val(series[i], 'id')) === id) { series[i] = fresh; break; }
        }
        entriesCache = null;
    }

    function reread(targets, buttonNode) {
        var errors = [];
        var index = 0;
        ui.setBusy(buttonNode, true, 'Lettura 0/' + targets.length + '…');
        function next() {
            if (index >= targets.length) return Promise.resolve();
            var target = targets[index];
            buttonNode.textContent = 'Lettura ' + (index + 1) + '/' + targets.length + '…';
            return api.request('POST', 'Plugins/AnimeClick/LibraryAuditSeries', { itemId: target.id })
                .then(replaceSeries)
                .catch(function (error) { errors.push(target.name + ': ' + truncate(error.message, 140)); })
                .then(function () { index++; return next(); });
        }
        return next().then(function () {
            render();
            ui.toast(errors.length ? 'Lettura completata con errori: ' + errors[0] : plural(targets.length, 'scheda riletta', 'schede rilette') + ' da AnimeClick.', errors.length ? 'error' : 'success');
        }).finally(function () { ui.setBusy(buttonNode, false); refreshControls(); });
    }

    function rereadVisible(buttonNode) {
        var targets = rereadCandidates();
        if (!targets.length) { ui.toast('Nessuna serie mostrata può migliorare con una nuova lettura.', 'error'); return; }
        ui.confirm({
            title: 'Rileggi ' + plural(targets.length, 'serie', 'serie'),
            confirmLabel: 'Rileggi',
            message: ['Le schede vengono lette una alla volta per non sovraccaricare AnimeClick: ' + targets.map(function (e) { return e.name; }).join(', ') + '.',
                'È solo una diagnosi: i metadati della libreria non cambiano.']
        }).then(function (yes) { if (yes) reread(targets, buttonNode); });
    }

    /* ===== detail panel ===== */

    var REASON_TONE = {
        Ok: 'ok', PendingRefresh: 'warn', Locked: 'warn', TitleNotPublished: 'info', CardHasNoTitles: '', CatalogNotCached: '',
        NotIdentified: 'bad', NumberingCollision: 'bad', NotMatched: 'bad', CardNotResolved: 'bad', RowVanished: 'bad'
    };
    var QUALITY_LABEL = { Italian: 'Italiano', English: 'Inglese', Missing: 'Mancante', Unknown: 'Lingua incerta' };
    var QUALITY_TONE = { Italian: 'ok', English: 'warn', Missing: 'bad', Unknown: '' };
    var REPAIR_STATE = {
        'waiting-translation': ['Traduzione in corso', 'info', 'La traduzione è già accodata: si applica da sola quando il servizio risponde.'],
        'no-source': ['Nessuna fonte', '', 'AnimeClick, TheTVDB e TMDB non hanno questa trama. «Riprova senza fonte» la ricontrolla.'],
        blocked: ['Valore cambiato', 'warn', 'Il testo è cambiato durante la riparazione, quindi non è stato sovrascritto.'],
        error: ['Errore', 'bad', 'L’ultimo tentativo è finito con un errore: i dettagli sono nel log di Jellyfin.']
    };

    var detail = null;

    function animeClickUrl(e) {
        var base = (AC.settings.saved() && AC.settings.saved().BaseUrl) || 'https://www.animeclick.it';
        var id = e.animeClickId && e.animeClickId !== 'identificato' ? e.animeClickId : (e.titles && val(e.titles, 'animeClickId'));
        return id && /^\d+(\/[\w-]+)?$/.test(id) ? base.replace(/\/+$/, '') + '/anime/' + id : null;
    }

    function openDetail(entryKey) {
        var e = entries().find(function (item) { return item.key === entryKey; });
        if (!e) {
            ui.toast('Questo anime non compare nell’ultima analisi: aggiornala e riprova.', 'error');
            return;
        }
        if (detail && detail.isOpen()) detail.close();
        detail = ui.drawer({ label: e.name, onClose: function () { detail = null; } });
        detail.entryKey = entryKey;
        renderDetail(e);
    }

    function renderDetail(e) {
        var head = detail.head;
        clear(head);
        if (e.hasPrimaryImage) {
            head.appendChild(h('div', { class: 'ac-hero-bg is-blur', style: 'background-image:url("' + api.image(e.id, 'Primary', { fillHeight: 300, quality: 60 }) + '")' }));
        }
        head.appendChild(h('div', { class: 'ac-hero-shade' }));
        var url = animeClickUrl(e);
        head.appendChild(ui.poster(e, { height: 300 }));
        head.appendChild(h('div', { class: 'ac-drawer-title' },
            h('p', { class: 'ac-jp' }, e.type === 'Movie' ? 'Film' : 'Serie', e.year ? h('i', { text: String(e.year) }) : null),
            h('h2', { text: e.name }),
            h('div', { class: 'ac-row' }, cardBadge(e),
                url ? h('a', { class: 'ac-btn ac-btn--sm ac-btn--ghost', href: url, target: '_blank', rel: 'noopener noreferrer' }, icon('external'), 'AnimeClick') : null,
                h('a', { class: 'ac-btn ac-btn--sm ac-btn--ghost', href: api.itemUrl(e.id) }, icon('tv'), 'Apri in Jellyfin'))));

        var body = detail.body;
        clear(body);

        if (!e.identified) {
            body.appendChild(ui.callout('Non ancora identificato', 'Jellyfin non ha collegato questo titolo a una scheda AnimeClick. Indicala tu: il plugin la verifica e aggiorna i metadati.', 'warn'));
        }

        if (e.titles) {
            var section = h('section', { class: 'ac-stack-s' }, h('h3', { class: 'ac-h3', text: 'Titoli degli episodi' }));
            var reason = val(e.titles, 'reason');
            section.appendChild(h('div', { class: 'ac-row' },
                ui.badge(e.titleMissing ? plural(e.titleMissing, 'titolo da sistemare', 'titoli da sistemare') : 'Tutti i titoli presenti', e.titleMissing ? 'warn' : 'ok'),
                h('span', { class: 'ac-muted', text: 'su ' + plural(e.titleTotal, 'episodio', 'episodi') })));
            if (reason && reason !== 'Ok') section.appendChild(ui.callout(null, val(e.titles, 'reasonLabel') || reason, REASON_TONE[reason] === 'bad' ? 'bad' : REASON_TONE[reason] === 'warn' ? 'warn' : 'info'));
            var seasons = list(val(e.titles, 'seasons'));
            if (seasons.length) {
                section.appendChild(h('div', { class: 'ac-seasons' }, seasons.map(function (season) {
                    var number = val(season, 'seasonNumber');
                    var chip = ui.badge((number == null ? 'Speciali' : 'S' + number) + ' · ' + num(val(season, 'missingTitleCount')), REASON_TONE[val(season, 'reason')] || '');
                    var card = val(season, 'animeClickId');
                    chip.title = (val(season, 'reasonLabel') || '') + (card ? '\nScheda usata: ' + card + (val(season, 'cardIsResolved') ? ' (della stagione)' : ' (della serie)') : '');
                    return chip;
                })));
            }
            var actions = h('div', { class: 'ac-row' });
            if (val(e.titles, 'animeClickId')) {
                var rereadButton = ui.button('Rileggi da AnimeClick', { small: true, icon: 'refresh', title: 'Rilegge la scheda e ricalcola la causa.' });
                rereadButton.addEventListener('click', function () {
                    reread([e], rereadButton).then(function () {
                        if (detail && detail.entryKey === e.key) renderDetail(entries().find(function (item) { return item.key === e.key; }) || e);
                    });
                });
                var purge = ui.button('Svuota la cache della serie', { small: true, variant: 'ghost', icon: 'trash', title: 'Il prossimo aggiornamento rileggerà le schede di questa serie.' });
                purge.addEventListener('click', function () {
                    ui.setBusy(purge, true, 'Svuotamento…');
                    api.request('POST', 'Plugins/AnimeClick/ClearCache', { animeClickId: val(e.titles, 'animeClickId') }).then(function (response) {
                        ui.toast('Cache della serie svuotata: ' + (val(response, 'removed') || 0) + ' elementi.', 'success');
                    }).catch(function (error) { ui.toast(truncate(error.message, 240), 'error'); })
                        .finally(function () { ui.setBusy(purge, false); });
                });
                actions.appendChild(rereadButton);
                actions.appendChild(purge);
            }
            section.appendChild(actions);
            body.appendChild(section);
        }

        var items = e.synopses ? list(val(e.synopses, 'items')) : [];
        var synSection = h('section', { class: 'ac-stack-s' }, h('h3', { class: 'ac-h3', text: 'Trame' }));
        if (!items.length) {
            synSection.appendChild(ui.badge(e.synTotal > 1 ? 'Tutte le trame sono in italiano' : 'Trama in italiano', 'ok', 'check'));
        } else {
            synSection.appendChild(h('div', { class: 'ac-row' },
                e.synBad ? ui.badge(plural(e.synBad, 'trama da completare', 'trame da completare'), 'warn') : null,
                e.synUnknown ? ui.badge(plural(e.synUnknown, 'di lingua incerta', 'di lingua incerta')) : null));
            var listHost = h('div', { class: 'ac-episodes' });
            synSection.appendChild(listHost);
            var shown = EPISODE_PAGE;
            var renderItems = function () {
                clear(listHost);
                items.slice(0, shown).forEach(function (item) { listHost.appendChild(episodeRow(item)); });
                if (shown < items.length) {
                    listHost.appendChild(ui.button('Mostra altri ' + Math.min(EPISODE_PAGE, items.length - shown), {
                        small: true, variant: 'ghost', onClick: function () { shown += EPISODE_PAGE; renderItems(); }
                    }));
                }
            };
            renderItems();
        }
        body.appendChild(synSection);

        body.appendChild(h('section', { class: 'ac-stack-s' },
            h('h3', { class: 'ac-h3', text: 'Abbinamento' }),
            h('p', { class: 'ac-hint', text: e.identified ? 'Se la scheda AnimeClick non è quella giusta, correggila: il plugin verifica la nuova scheda prima di salvarla.' : 'Scegli la scheda giusta su AnimeClick.' }),
            h('div', { class: 'ac-row' }, ui.button(e.identified ? 'Correggi abbinamento' : 'Identifica su AnimeClick', {
                small: true, variant: e.identified ? 'ghost' : 'primary', icon: 'link',
                onClick: function () { detail.close(); AC.settings.startIdentify({ id: e.id, name: e.name, type: e.type, year: e.year, hasPrimaryImage: e.hasPrimaryImage }); }
            }))));
    }

    function episodeRow(item) {
        var type = val(item, 'itemType');
        var name = val(item, 'name') || 'Senza titolo';
        var heading = type === 'Episode'
            ? 'S' + (val(item, 'seasonNumber') == null ? '?' : val(item, 'seasonNumber')) + 'E' + (val(item, 'episodeNumber') == null ? '?' : val(item, 'episodeNumber')) + ' · ' + name
            : (type === 'Movie' ? 'Film' : 'Serie') + ' · ' + name;
        var status = val(item, 'status');
        var badges = [ui.badge(QUALITY_LABEL[status] || status || 'Sconosciuto', QUALITY_TONE[status])];
        if (val(item, 'locked')) badges.push(ui.badge('Bloccato', 'warn', 'lock'));
        else if (!val(item, 'languageRepairable') && (status === 'English' || status === 'Missing')) badges.push(ui.badge('Funzione disattivata'));
        var repair = REPAIR_STATE[val(item, 'repairState')];
        if (repair) {
            var chip = ui.badge(repair[0], repair[1]);
            chip.title = repair[2] + (val(item, 'attemptCount') ? ' Tentativi: ' + val(item, 'attemptCount') + '.' : '');
            badges.push(chip);
        }
        return h('div', { class: 'ac-episode' },
            h('div', { class: 'ac-episode-head' }, h('strong', { text: heading }), badges),
            val(item, 'preview') ? h('p', { text: val(item, 'preview') }) : null);
    }

    AC.library = {
        build: build,
        attach: function (pageElement) { page = pageElement; },
        enter: function () {
            if (!data.loadedAt && !data.loading) loadReports(false);
            poll();
        },
        leave: function () { if (!shouldPoll()) stopPolling(); },
        poll: poll,
        stop: stopPolling,
        loadReports: loadReports,
        data: function () { return data; },
        entries: entries,
        jobs: function () { return jobs; },
        jobError: function () { return jobError; },
        isActive: isActive,
        jobPanel: jobPanel,
        updateJobPanel: updateJobPanel,
        runTitles: runTitles,
        titlesToCheck: titlesToCheck,
        runSynopses: runSynopses,
        openDetail: openDetail,
        setFilter: setFilter,
        reset: function () {
            data.titles = data.quality = data.catalog = data.loadedAt = null;
            data.failures = [];
            entriesCache = null;
            jobs = { titles: { key: 'titles', state: 'Idle' }, synopses: { key: 'synopses', state: 'Idle' } };
        }
    };
}());
