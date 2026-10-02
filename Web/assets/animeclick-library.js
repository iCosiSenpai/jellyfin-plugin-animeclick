/* Library workspace: local inspection and visible server activities. */
(function () {
    'use strict';
    window.AC = window.AC || {};
    window.AC.createLibrary = function (d) {
        var timer = null, pollPending = false, scanPending = false, loaded = false;
        var activities = Object.create(null);
        var currentMode = 'titles';

        function visible() {
            var tab = d.val('acTabLibreria');
            return d.page.isConnected && tab && tab.getAttribute('aria-selected') === 'true' && !document.hidden;
        }
        function active(key) { return !!activities[key] && !!d.valueOf(activities[key], 'isActive'); }
        function stop() { clearTimeout(timer); timer = null; }
        function selectMode(mode) {
            currentMode = mode;
            ['titles', 'synopses'].forEach(function (key) {
                var selected = key === mode;
                var content = d.val(key === 'titles' ? 'acLibraryTitles' : 'acLibrarySynopses');
                if (content) content.hidden = !selected;
                d.val('acLibraryMode_' + key).setAttribute('aria-pressed', selected ? 'true' : 'false');
            });
        }
        function build(panel) {
            var head = d.el('div', 'ac-library-header');
            var copy = d.el('div', 'ac-stack ac-grow');
            copy.appendChild(d.el('h1', 'ac-title', 'La tua libreria'));
            copy.appendChild(d.el('p', 'ac-note', 'Controlla cosa manca, completa i metadati disponibili e segui ogni attività qui.'));
            head.appendChild(copy);
            var scan = d.el('button', 'ac-btn ac-btn-ghost', 'Aggiorna analisi');
            scan.type = 'button'; scan.id = 'acLibraryScan';
            scan.addEventListener('click', scanReports);
            head.appendChild(scan); panel.appendChild(head);
            var state = d.el('div', 'ac-library-scan');
            var progress = d.el('progress', 'ac-progress');
            progress.id = 'acLibraryScanProgress'; progress.max = 100; progress.hidden = true;
            progress.setAttribute('aria-label', 'Analisi locale della libreria');
            state.appendChild(progress);
            var message = d.el('p', 'ac-state', 'L’analisi legge i dati locali e non modifica la libreria.');
            message.id = 'acLibraryScanState'; message.setAttribute('role', 'status');
            state.appendChild(message); panel.appendChild(state);

            var jobs = d.makeCard('', 'Attività', 'Puoi chiudere la pagina: il lavoro continua sul server.');
            jobs.card.classList.add('ac-activities');
            ['titles', 'synopses'].forEach(function (key) {
                var job = d.el('div', 'ac-activity'); job.id = 'acActivity_' + key;
                var row = d.el('div', 'ac-row');
                row.appendChild(d.el('strong', 'ac-grow', key === 'titles' ? 'Titoli episodio' : 'Trame e sinossi'));
                var badge = d.el('span', 'ac-badge neutral', 'Inattiva'); badge.id = 'acActivityBadge_' + key;
                row.appendChild(badge);
                var cancel = d.el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Interrompi');
                cancel.id = 'acActivityCancel_' + key; cancel.type = 'button'; cancel.hidden = true;
                cancel.setAttribute('aria-label', 'Interrompi ' + (key === 'titles' ? 'titoli episodio' : 'trame e sinossi'));
                cancel.addEventListener('click', function () {
                    cancel.disabled = true;
                    d.request('POST', 'Plugins/AnimeClick/CancelActivity', { key: key }).then(poll).catch(function (error) {
                        d.val('acActivityMessage_' + key).textContent = 'Interruzione non riuscita: ' + error.message;
                    }).finally(function () { cancel.disabled = false; });
                });
                row.appendChild(cancel); job.appendChild(row);
                var bar = d.el('progress', 'ac-progress'); bar.max = 100; bar.value = 0;
                bar.id = 'acActivityProgress_' + key; bar.hidden = true;
                bar.setAttribute('aria-label', key === 'titles' ? 'Avanzamento titoli episodio' : 'Avanzamento trame e sinossi');
                job.appendChild(bar);
                var text = d.el('p', 'ac-state', 'Nessuna attività in corso.');
                text.id = 'acActivityMessage_' + key; text.setAttribute('role', 'status'); text.setAttribute('aria-live', 'polite');
                job.appendChild(text);
                var counts = d.el('p', 'ac-note'); counts.id = 'acActivityCounts_' + key;
                job.appendChild(counts); jobs.body.appendChild(job);
            });
            var pollingState = d.el('p', 'ac-state'); pollingState.id = 'acActivityConnection';
            pollingState.setAttribute('role', 'status'); jobs.body.appendChild(pollingState);
            panel.appendChild(jobs.card);

            var modes = d.el('div', 'ac-library-modes'); modes.setAttribute('role', 'group');
            modes.setAttribute('aria-label', 'Tipo di metadati');
            ['titles', 'synopses'].forEach(function (key) {
                var button = d.el('button', 'ac-btn ac-library-mode', key === 'titles' ? 'Titoli episodio' : 'Trame e sinossi');
                button.id = 'acLibraryMode_' + key; button.type = 'button';
                button.setAttribute('aria-pressed', key === currentMode ? 'true' : 'false');
                button.addEventListener('click', function () { selectMode(key); });
                modes.appendChild(button);
            });
            panel.appendChild(modes);
        }

        function scanReports() {
            if (scanPending) return Promise.resolve();
            scanPending = true;
            var button = d.val('acLibraryScan');
            var state = d.val('acLibraryScanState');
            d.setBusy(button, true, 'Aggiorna analisi', 'Analisi in corso…');
            d.val('acLibraryScanProgress').hidden = false;
            state.textContent = 'Lettura locale di titoli, serie, film e trame…';
            return Promise.allSettled([
                d.request('GET', 'Plugins/AnimeClick/LibraryAudit'),
                d.request('GET', 'Plugins/AnimeClick/LibraryQualityAudit')
            ]).then(function (results) {
                var failures = [];
                if (results[0].status === 'fulfilled') d.renderAudit(results[0].value);
                else failures.push('titoli: ' + results[0].reason.message);
                if (results[1].status === 'fulfilled') d.renderQualityAudit(results[1].value);
                else failures.push('trame: ' + results[1].reason.message);
                loaded = failures.length === 0;
                state.className = 'ac-state' + (failures.length ? ' error' : '');
                state.textContent = failures.length ? 'Analisi incompleta — ' + failures.join(' · ') + '. Premi Aggiorna analisi per riprovare.'
                    : 'Analisi aggiornata alle ' + new Date().toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' })
                        + '. Apri una serie per i dettagli.';
                selectMode(currentMode);
                updateControls();
            }).finally(function () {
                scanPending = false; d.val('acLibraryScanProgress').hidden = true;
                d.setBusy(button, false, 'Aggiorna analisi', 'Analisi in corso…');
                updateControls();
            });
        }
        function updateControls() {
            d.updateTitleControls(); d.updateQualityControls();
            var titles = d.val('acBtnRunTitles');
            if (titles) {
                var enabled = d.titleReport() && d.valueOf(d.titleReport(), 'episodeTitlesEnabled');
                titles.disabled = active('titles') || !enabled || scanPending;
                titles.textContent = active('titles') ? 'Ricontrollo in corso…' : 'Sistema tutti i titoli';
            }
            var synopses = d.val('acBtnQualityRepair');
            if (synopses && active('synopses')) { synopses.disabled = true; synopses.textContent = 'Completamento in corso…'; }
        }
        function poll() {
            stop();
            if (!visible() || pollPending) return Promise.resolve();
            pollPending = true;
            return d.request('GET', 'Plugins/AnimeClick/Activities', undefined, 15000).then(function (rows) {
                var refresh = false;
                d.val('acActivityConnection').textContent = '';
                (Array.isArray(rows) ? rows : []).forEach(function (job) {
                    var key = d.valueOf(job, 'key');
                    if (key !== 'titles' && key !== 'synopses') return;
                    var wasActive = active(key);
                    activities[key] = job;
                    if (wasActive && !active(key)) refresh = true;
                    var state = d.valueOf(job, 'state');
                    var labels = { Idle: 'Inattiva', Queued: 'In attesa', Running: 'In corso', Cancelling: 'Interruzione', Cancelled: 'Interrotta', Completed: 'Completata', Partial: 'Da verificare', Failed: 'Errore' };
                    var badge = d.val('acActivityBadge_' + key);
                    badge.textContent = labels[state] || state;
                    badge.className = 'ac-badge ' + (state === 'Completed' ? 'success' : (state === 'Failed' || state === 'Partial' ? 'warn' : 'neutral'));
                    var message = d.valueOf(job, 'message') || '';
                    var text = d.val('acActivityMessage_' + key);
                    if (text.textContent !== message) text.textContent = message;
                    var bar = d.val('acActivityProgress_' + key);
                    bar.hidden = state === 'Idle';
                    if (state === 'Queued' || (active(key) && !d.valueOf(job, 'total'))) bar.removeAttribute('value');
                    else bar.value = d.valueOf(job, 'progress') || 0;
                    var total = d.valueOf(job, 'total') || 0;
                    d.val('acActivityCounts_' + key).textContent = total ? (d.valueOf(job, 'processed') || 0) + '/' + total
                        + ' verificati · ' + (d.valueOf(job, 'applied') || 0) + ' aggiornati · '
                        + (d.valueOf(job, 'skipped') || 0) + ' saltati/in attesa · ' + (d.valueOf(job, 'errors') || 0) + ' errori' : '';
                    d.val('acActivityCancel_' + key).hidden = !active(key);
                    d.val('acActivityCancel_' + key).disabled = state === 'Cancelling';
                });
                updateControls();
                if (refresh) return scanReports();
            }).catch(function (error) {
                d.val('acActivityConnection').textContent = 'Stato non disponibile: ' + error.message + ' Il controllo riprenderà automaticamente.';
            }).finally(function () {
                pollPending = false;
                if (visible()) timer = setTimeout(poll, active('titles') || active('synopses') ? 2000 : 10000);
            });
        }
        function enter() {
            if (!loaded) scanReports();
            poll();
        }
        function onVisibility() { if (visible()) enter(); else stop(); }
        document.addEventListener('visibilitychange', onVisibility);
        d.page.addEventListener('pagehide', stop);
        return { build: build, enter: enter, stop: stop, poll: poll, active: active,
            dispose: function () { stop(); document.removeEventListener('visibilitychange', onVisibility); d.page.removeEventListener('pagehide', stop); } };
    };
}());
