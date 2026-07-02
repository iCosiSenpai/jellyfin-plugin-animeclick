/* AnimeClick — configuration page logic (AC.config).
   Form built from descriptors, dirty-state tracked, save bar appears on change.
   Pattern matches KometaThemes config.js architecture exactly. */
(function () {
    'use strict';

    var V = '0.3.2.0';
    var GUID = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';

    /* ===== util ===== */

    function esc(v) {
        return String(v == null ? '' : v)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }
    function el(tag, cls, text) {
        var n = document.createElement(tag);
        if (cls) n.className = cls;
        if (text != null) n.textContent = text;
        return n;
    }
    function clear(n) { while (n && n.firstChild) n.removeChild(n.firstChild); }

    /* ===== api (MediaBrowser auth) ===== */

    function authHeader() {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
                var t = ApiClient.accessToken();
                if (t) return 'MediaBrowser Token="' + t + '"';
            }
        } catch (e) { /* */ }
        return null;
    }
    function apiUrl(path) {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function')
                return ApiClient.getUrl(path);
        } catch (e) { /* */ }
        return path;
    }
    function request(method, path, body) {
        var headers = { Accept: 'application/json' };
        var auth = authHeader();
        if (auth) headers.Authorization = auth;
        var opts = { method: method, credentials: 'same-origin', headers: headers };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            opts.body = JSON.stringify(body);
        }
        return fetch(apiUrl(path), opts).then(function (r) {
            var isJson = (r.headers.get('Content-Type') || '').indexOf('json') > -1;
            if (!r.ok) {
                return (isJson ? r.json() : r.text()).catch(function () { return null; })
                    .then(function (p) {
                        var msg = (p && (p.error || p.message)) || (typeof p === 'string' && p) || ('HTTP ' + r.status);
                        var err = new Error(msg); err.status = r.status; throw err;
                    });
            }
            if (r.status === 204 || !isJson) return null;
            return r.json();
        });
    }

    /* ===== toast ===== */

    var toastHost;
    function ensureToastHost() {
        if (!toastHost || !document.body.contains(toastHost)) {
            toastHost = el('div', 'ac-toast-host');
            document.body.appendChild(toastHost);
        }
    }
    function toast(msg, type) {
        ensureToastHost();
        var t = el('div', 'ac-toast' + (type ? ' ' + type : ''), msg);
        toastHost.appendChild(t);
        setTimeout(function () {
            t.classList.add('leaving');
            setTimeout(function () { if (t.parentNode) t.parentNode.removeChild(t); }, 200);
        }, 3000);
    }

    /* ===== confirm modal ===== */

    function confirmModal(title, message) {
        return new Promise(function (resolve) {
            var veil = el('div', 'ac-modal-veil');
            var modal = el('div', 'ac-modal');
            var h = el('h3', null, title);
            var p = el('p', null, message);
            var row = el('div', 'ac-row');
            var btnCancel = el('button', 'ac-btn ac-btn-ghost', 'Annulla');
            var btnOk = el('button', 'ac-btn ac-btn-primary', 'Conferma');
            btnCancel.type = 'button';
            btnOk.type = 'button';
            row.appendChild(btnCancel);
            row.appendChild(btnOk);
            modal.appendChild(h);
            modal.appendChild(p);
            modal.appendChild(row);
            veil.appendChild(modal);
            document.body.appendChild(veil);
            btnCancel.onclick = function () { veil.remove(); resolve(false); };
            btnOk.onclick = function () { veil.remove(); resolve(true); };
        });
    }

    /* ===== config state ===== */

    var page, savedConfig;
    var dirty = false;

    function getPluginConfig() {
        return new Promise(function (resolve, reject) {
            try { ApiClient.getPluginConfiguration(GUID).then(resolve, reject); }
            catch (e) { reject(e); }
        });
    }
    function savePluginConfig(cfg) {
        return new Promise(function (resolve, reject) {
            try { ApiClient.updatePluginConfiguration(GUID, cfg).then(resolve, reject); }
            catch (e) { reject(e); }
        });
    }

    function markDirty() {
        dirty = true;
        var bar = page.querySelector('#acSaveBar');
        if (bar) bar.style.display = '';
    }
    function markClean() {
        dirty = false;
        var bar = page.querySelector('#acSaveBar');
        if (bar) bar.style.display = 'none';
    }

    /* ===== DOM helpers for building forms ===== */

    function makeCheck(id, label, desc) {
        var wrap = el('div', 'ac-check');
        var lbl = el('label');
        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.className = 'ac-checkbox';
        cb.id = id;
        lbl.appendChild(cb);
        var span = el('span');
        span.innerHTML = '<span style="font-weight:600">' + esc(label) + '</span>';
        if (desc) span.innerHTML += '<div class="ac-field-desc">' + desc + '</div>';
        lbl.appendChild(span);
        wrap.appendChild(lbl);
        cb.addEventListener('change', markDirty);
        return wrap;
    }
    function makeField(id, label, type, desc, attrs) {
        var wrap = el('div', 'ac-field');
        var lbl = el('label', null, label);
        lbl.setAttribute('for', id);
        wrap.appendChild(lbl);
        var input;
        if (type === 'select') {
            input = el('select', 'ac-select');
        } else {
            input = el('input', 'ac-input');
            input.type = type || 'text';
        }
        input.id = id;
        if (attrs) {
            Object.keys(attrs).forEach(function (k) { input.setAttribute(k, attrs[k]); });
        }
        wrap.appendChild(input);
        if (desc) {
            var d = el('div', 'ac-field-desc');
            d.innerHTML = desc;
            wrap.appendChild(d);
        }
        input.addEventListener('input', markDirty);
        input.addEventListener('change', markDirty);
        return wrap;
    }

    /* ===== tab switching ===== */

    function initTabs() {
        var tabs = page.querySelectorAll('.ac-tab');
        tabs.forEach(function (tab) {
            tab.addEventListener('click', function () {
                tabs.forEach(function (t) { t.setAttribute('aria-selected', 'false'); });
                tab.setAttribute('aria-selected', 'true');
                var panels = page.querySelectorAll('.ac-panel');
                panels.forEach(function (p) { p.classList.remove('active'); });
                var target = page.querySelector('.ac-panel[data-panel="' + tab.dataset.panel + '"]');
                if (target) target.classList.add('active');
            });
        });
    }

    /* ===== build panels ===== */

    function buildOverviewPanel() {
        var p = page.querySelector('#acPanelOverview');
        clear(p);

        // --- Provider status card ---
        var card1 = el('div', 'ac-card ac-card-flush');
        var head1 = el('div', 'ac-card-head');
        head1.appendChild(el('span', 'ac-subtitle', '🔌 Stato provider'));
        card1.appendChild(head1);
        var body1 = el('div', 'ac-card-body');

        ['tmdb', 'ollama', 'tvdb'].forEach(function (prov) {
            var names = { tmdb: 'TMDB', ollama: 'Ollama Cloud', tvdb: 'TheTVDB' };
            var row = el('div', 'ac-row');
            row.style.cssText = 'justify-content:space-between; padding:8px 0; border-bottom:1px solid var(--ac-border);';
            var left = el('div', 'ac-row');
            left.style.gap = '8px';
            var dot = el('span', 'ac-live-dot is-idle');
            dot.id = 'acDot_' + prov;
            left.appendChild(dot);
            left.appendChild(el('span', null, names[prov]));
            var badge = el('span', 'ac-badge neutral', '…');
            badge.id = 'acBadge_' + prov;
            left.appendChild(badge);
            row.appendChild(left);
            var btn = el('button', 'ac-btn ac-btn-sm', 'Test');
            btn.type = 'button';
            btn.id = 'acTest_' + prov;
            row.appendChild(btn);
            body1.appendChild(row);

            var detail = el('div', 'ac-log');
            detail.id = 'acDetail_' + prov;
            detail.style.display = 'none';
            body1.appendChild(detail);
        });
        card1.appendChild(body1);
        p.appendChild(card1);

        // --- Quick actions card ---
        var card2 = el('div', 'ac-card ac-card-flush');
        var head2 = el('div', 'ac-card-head');
        head2.appendChild(el('span', 'ac-subtitle', '⚡ Azioni rapide'));
        card2.appendChild(head2);
        var body2 = el('div', 'ac-card-body');
        var btnClear = el('button', 'ac-btn ac-btn-danger', '🗑 Svuota cache');
        btnClear.type = 'button';
        btnClear.id = 'acBtnClearCache';
        body2.appendChild(el('div', 'ac-field-desc', 'Svuota tutta la cache dei metadati scaricati. Utile per forzare un aggiornamento completo.'));
        body2.appendChild(btnClear);
        var cacheResult = el('div', 'ac-state');
        cacheResult.id = 'acCacheResult';
        body2.appendChild(cacheResult);
        card2.appendChild(body2);
        p.appendChild(card2);

        // --- Active features ---
        var card3 = el('div', 'ac-card ac-card-flush');
        var head3 = el('div', 'ac-card-head');
        head3.appendChild(el('span', 'ac-subtitle', '📋 Funzionalità attive'));
        card3.appendChild(head3);
        var body3 = el('div', 'ac-card-body');
        var chips = el('div', 'ac-row');
        chips.id = 'acFeatureChips';
        body3.appendChild(chips);
        card3.appendChild(body3);
        p.appendChild(card3);
    }

    function buildMetadatiPanel() {
        var p = page.querySelector('#acPanelMetadati');
        clear(p);

        // Titoli & Trama
        var c1 = el('div', 'ac-card ac-card-flush');
        var h1 = el('div', 'ac-card-head');
        h1.appendChild(el('span', 'ac-subtitle', '📝 Titoli & Trama'));
        c1.appendChild(h1);
        var b1 = el('div', 'ac-card-body');
        b1.appendChild(makeCheck('acPreferItalianTitle', 'Preferisci titolo italiano', 'Usa il titolo italiano come nome principale della serie/film.'));
        b1.appendChild(makeCheck('acEnablePlot', 'Importa trama', 'Importa la sinossi/trama in italiano da AnimeClick.'));
        b1.appendChild(makeCheck('acOverwriteNonItalianFields', 'Sovrascrivi campi non-italiani', 'Se attivo, AnimeClick sovrascrive anche titolo originale, studio, rating, data. Se disattivo, lascia questi campi agli altri provider (AniList/TMDB).'));
        c1.appendChild(b1);
        p.appendChild(c1);

        // Immagini
        var c2 = el('div', 'ac-card ac-card-flush');
        var h2 = el('div', 'ac-card-head');
        h2.appendChild(el('span', 'ac-subtitle', '🖼 Immagini'));
        c2.appendChild(h2);
        var b2 = el('div', 'ac-card-body');
        b2.appendChild(makeCheck('acEnableAnimeClickImages', 'Locandina AnimeClick come fallback', 'Fornisce la locandina italiana di AnimeClick come immagine di backup (priorità bassa: AniList/Fanart vincono se hanno immagini).'));
        c2.appendChild(b2);
        p.appendChild(c2);

        // Dettagli opzionali
        var c3 = el('div', 'ac-card ac-card-flush');
        var h3 = el('div', 'ac-card-head');
        h3.appendChild(el('span', 'ac-subtitle', '🔧 Dettagli opzionali'));
        c3.appendChild(h3);
        var b3 = el('div', 'ac-card-body');
        var grid = el('div', 'ac-grid-2');
        grid.appendChild(makeCheck('acEnableGenres', 'Generi', 'Importa generi in italiano (Azione, Avventura…).'));
        grid.appendChild(makeCheck('acEnableStudios', 'Studi', 'Importa gli studi di animazione.'));
        grid.appendChild(makeCheck('acEnableCommunityRating', 'Valutazione', 'Importa il rating medio della community.'));
        grid.appendChild(makeCheck('acEnableCast', 'Cast & Staff', 'Importa doppiatori, registi, autori.'));
        grid.appendChild(makeCheck('acEnableTags', 'Tag', 'Importa tag (Shounen, Seinen, ecc.).'));
        grid.appendChild(makeCheck('acEnableEpisodeTitles', 'Titoli episodi', 'Importa titoli italiani degli episodi dalla pagina /episodi.'));
        grid.appendChild(makeCheck('acEnableThemeSongs', 'Sigle', 'Importa nomi delle sigle (OP/ED) nei tag.'));
        grid.appendChild(makeCheck('acEnableCollections', 'Collezioni', 'Crea collezioni automatiche basate su sequel/prequel/spin-off.'));
        b3.appendChild(grid);
        c3.appendChild(b3);
        p.appendChild(c3);
    }

    function buildSinossiPanel() {
        var p = page.querySelector('#acPanelSinossi');
        clear(p);

        // Abilitazione
        var c1 = el('div', 'ac-card ac-card-flush');
        var h1 = el('div', 'ac-card-head');
        h1.appendChild(el('span', 'ac-subtitle', '🌐 Sinossi episodi in italiano'));
        c1.appendChild(h1);
        var b1 = el('div', 'ac-card-body');
        b1.appendChild(makeCheck('acEnableEpisodeSynopsisTranslation', 'Abilita traduzione sinossi episodi',
            'AnimeClick non pubblica sinossi per-episodio. Il plugin recupera l\'overview da TMDB/TVDB e la traduce in italiano via Ollama Cloud. Richiede API key TMDB e Ollama Cloud.'));
        c1.appendChild(b1);
        p.appendChild(c1);

        // TheTVDB
        var c2 = el('div', 'ac-card ac-card-flush');
        var h2 = el('div', 'ac-card-head');
        h2.appendChild(el('span', 'ac-subtitle', '📺 TheTVDB — fonte preferita'));
        c2.appendChild(h2);
        var b2 = el('div', 'ac-card-body');
        b2.appendChild(makeCheck('acEnableTvdbSynopsis', 'Usa TheTVDB per sinossi in italiano', 'Quando TVDB ha la traduzione IT, viene usata direttamente (zero chiamate Ollama). Altrimenti ricade su TMDB + Ollama Cloud.'));
        b2.appendChild(makeField('acTvdbApiKey', 'API Key TheTVDB', 'password', 'Ottieni una API key gratuita su <a href="https://thetvdb.com/dashboard" target="_blank">thetvdb.com/dashboard</a>.'));
        b2.appendChild(makeField('acTvdbLanguage', 'Lingua TVDB', 'text', 'Codice lingua a 3 caratteri (es: ita, eng, fra).'));
        c2.appendChild(b2);
        p.appendChild(c2);

        // TMDB + Ollama
        var c3 = el('div', 'ac-card ac-card-flush');
        var h3 = el('div', 'ac-card-head');
        h3.appendChild(el('span', 'ac-subtitle', '🤖 TMDB + Ollama Cloud — fallback'));
        c3.appendChild(h3);
        var b3 = el('div', 'ac-card-body');
        var grid = el('div', 'ac-grid-2');
        grid.appendChild(makeField('acTmdbApiKey', 'API Key TMDB', 'password', 'Ottieni su <a href="https://www.themoviedb.org/settings/api" target="_blank">themoviedb.org</a>.'));
        grid.appendChild(makeField('acOllamaCloudApiKey', 'API Key Ollama Cloud', 'password', 'Ottieni su <a href="https://ollama.com/settings/keys" target="_blank">ollama.com/settings/keys</a>.'));
        b3.appendChild(grid);
        b3.appendChild(makeField('acOllamaCloudEndpoint', 'Endpoint Ollama Cloud', 'url', 'URL dell\'API chat di Ollama Cloud.'));

        // Model select
        var modelField = el('div', 'ac-field');
        modelField.appendChild(el('label', null, 'Modello Ollama'));
        var sel = el('select', 'ac-select');
        sel.id = 'acOllamaCloudModel';
        var models = [
            { group: 'Consigliati', items: [
                ['gemma4:cloud', 'gemma4:cloud (consigliato)'],
                ['minimax-m2.1:cloud', 'minimax-m2.1:cloud'],
                ['qwen3.5:cloud', 'qwen3.5:cloud'],
                ['gpt-oss:cloud', 'gpt-oss:cloud']
            ]},
            { group: 'Altri', items: [
                ['glm-5.2:cloud', 'glm-5.2:cloud'],
                ['minimax-m3:cloud', 'minimax-m3:cloud'],
                ['kimi-k2.7-code:cloud', 'kimi-k2.7-code:cloud'],
                ['nemotron-3-ultra:cloud', 'nemotron-3-ultra:cloud'],
                ['glm-5.1:cloud', 'glm-5.1:cloud'],
                ['minimax-m2.7:cloud', 'minimax-m2.7:cloud'],
                ['nemotron-3-super:cloud', 'nemotron-3-super:cloud'],
                ['glm-5:cloud', 'glm-5:cloud'],
                ['minimax-m2.5:cloud', 'minimax-m2.5:cloud'],
                ['kimi-k2.6:cloud', 'kimi-k2.6:cloud'],
                ['deepseek-v4-pro:cloud', 'deepseek-v4-pro:cloud'],
                ['deepseek-v4-flash:cloud', 'deepseek-v4-flash:cloud'],
                ['kimi-k2.5:cloud', 'kimi-k2.5:cloud'],
                ['qwen3-coder:cloud', 'qwen3-coder:cloud'],
                ['glm-4.7:cloud', 'glm-4.7:cloud'],
                ['gemini-3-flash-preview:cloud', 'gemini-3-flash-preview:cloud']
            ]},
            { group: 'Custom', items: [
                ['__custom__', '✏️ Modello personalizzato…']
            ]}
        ];
        models.forEach(function (g) {
            var og = el('optgroup');
            og.label = g.group;
            g.items.forEach(function (m) {
                var opt = el('option', null, m[1]);
                opt.value = m[0];
                og.appendChild(opt);
            });
            sel.appendChild(og);
        });
        sel.addEventListener('change', function () {
            var customWrap = page.querySelector('#acCustomModelWrap');
            if (customWrap) customWrap.style.display = sel.value === '__custom__' ? '' : 'none';
            markDirty();
        });
        modelField.appendChild(sel);
        modelField.appendChild(el('div', 'ac-field-desc', 'Modello cloud per la traduzione EN→IT delle sinossi.'));
        b3.appendChild(modelField);

        var customWrap = makeField('acCustomModel', 'Nome modello personalizzato', 'text', 'Inserisci il nome esatto del modello cloud.');
        customWrap.id = 'acCustomModelWrap';
        customWrap.style.display = 'none';
        b3.appendChild(customWrap);

        b3.appendChild(makeField('acEpisodeTranslationTimeoutSec', 'Timeout traduzione (secondi)', 'number', 'Timeout per una singola chiamata di traduzione.', { min: '5', max: '120' }));
        c3.appendChild(b3);
        p.appendChild(c3);
    }

    function buildStrumentiPanel() {
        var p = page.querySelector('#acPanelStrumenti');
        clear(p);

        // Identifica & Aggiorna
        var c1 = el('div', 'ac-card ac-card-flush');
        var h1 = el('div', 'ac-card-head');
        h1.appendChild(el('span', 'ac-subtitle', '🔍 Identifica & Aggiorna'));
        c1.appendChild(h1);
        var b1 = el('div', 'ac-card-body');
        b1.appendChild(el('div', 'ac-field-desc', 'Identifica manualmente un elemento Jellyfin con un anime AnimeClick specifico, aggiorna i metadati e scarica le immagini da tutti i provider attivi.'));
        var g1 = el('div', 'ac-grid-2');
        g1.appendChild(makeField('acItemId', 'ID elemento Jellyfin', 'text', 'L\'ID interno dell\'elemento nella tua libreria.'));
        g1.appendChild(makeField('acAnimeClickId', 'ID/slug AnimeClick', 'text', 'Es: "naruto" oppure "2966-naruto".'));
        b1.appendChild(g1);
        b1.appendChild(makeCheck('acReplaceAllImages', 'Sostituisci tutte le immagini', 'Rimuovi le immagini esistenti e scarica di nuovo da tutti i provider.'));
        var btnId = el('button', 'ac-btn ac-btn-primary', '🚀 Identifica & Aggiorna');
        btnId.type = 'button';
        btnId.id = 'acBtnIdentify';
        b1.appendChild(btnId);
        var idResult = el('div', 'ac-log');
        idResult.id = 'acIdentifyResult';
        idResult.style.display = 'none';
        b1.appendChild(idResult);
        c1.appendChild(b1);
        p.appendChild(c1);

        // Ricerca, Cache & Avanzate
        var c2 = el('div', 'ac-card ac-card-flush');
        var h2 = el('div', 'ac-card-head');
        h2.appendChild(el('span', 'ac-subtitle', '⚙️ Ricerca, Cache & Avanzate'));
        c2.appendChild(h2);
        var b2 = el('div', 'ac-card-body');
        var g2 = el('div', 'ac-grid-2');
        g2.appendChild(makeField('acMaxSearchResults', 'Max risultati ricerca', 'number', 'Numero massimo di risultati per ricerca (1–25).', { min: '1', max: '25' }));
        g2.appendChild(makeField('acCacheHours', 'Cache metadati (ore)', 'number', 'Per quante ore i metadati restano in cache (1–720).', { min: '1', max: '720' }));
        g2.appendChild(makeField('acNegativeCacheHours', 'Cache negativa (ore)', 'number', 'Per quante ore i risultati vuoti restano in cache (1–168).', { min: '1', max: '168' }));
        g2.appendChild(makeField('acRequestDelay', 'Pausa tra richieste (ms)', 'number', 'Ritardo in millisecondi tra richieste HTTP ad AnimeClick (500–10000).', { min: '500', max: '10000' }));
        b2.appendChild(g2);
        b2.appendChild(makeCheck('acFilterToAnimeOnly', 'Filtra solo anime', 'Escludi manga, novel, drama e mostra solo risultati anime.'));
        b2.appendChild(makeField('acBaseUrl', 'URL base AnimeClick', 'url', 'Non modificare a meno che il sito non cambi dominio.'));
        b2.appendChild(makeField('acUserAgent', 'User-Agent', 'text', 'User-Agent per le richieste HTTP verso AnimeClick.'));
        c2.appendChild(b2);
        p.appendChild(c2);
    }

    /* ===== load config into form ===== */

    function val(id) { return page.querySelector('#' + id); }

    function loadForm(cfg) {
        savedConfig = cfg;

        // Metadati
        val('acPreferItalianTitle').checked = cfg.PreferItalianTitle;
        val('acEnablePlot').checked = cfg.EnablePlot;
        val('acOverwriteNonItalianFields').checked = cfg.OverwriteNonItalianFields;
        val('acEnableAnimeClickImages').checked = cfg.EnableAnimeClickImages;
        val('acEnableGenres').checked = cfg.EnableGenres;
        val('acEnableStudios').checked = cfg.EnableStudios;
        val('acEnableCommunityRating').checked = cfg.EnableCommunityRating;
        val('acEnableCast').checked = cfg.EnableCast;
        val('acEnableTags').checked = cfg.EnableTags;
        val('acEnableEpisodeTitles').checked = cfg.EnableEpisodeTitles;
        val('acEnableThemeSongs').checked = cfg.EnableThemeSongs;
        val('acEnableCollections').checked = cfg.EnableCollections;

        // Sinossi
        val('acEnableEpisodeSynopsisTranslation').checked = cfg.EnableEpisodeSynopsisTranslation;
        val('acEnableTvdbSynopsis').checked = cfg.EnableTvdbSynopsis;
        val('acTvdbApiKey').value = cfg.TvdbApiKey || '';
        val('acTvdbLanguage').value = cfg.TvdbLanguage || 'ita';
        val('acTmdbApiKey').value = cfg.TmdbApiKey || '';
        val('acOllamaCloudApiKey').value = cfg.OllamaCloudApiKey || '';
        val('acOllamaCloudEndpoint').value = cfg.OllamaCloudEndpoint || '';
        val('acEpisodeTranslationTimeoutSec').value = cfg.EpisodeTranslationTimeoutSec || 30;

        // Model select
        var modelSel = val('acOllamaCloudModel');
        var model = cfg.OllamaCloudModel || 'gemma4:cloud';
        var found = false;
        for (var i = 0; i < modelSel.options.length; i++) {
            if (modelSel.options[i].value === model) { modelSel.selectedIndex = i; found = true; break; }
        }
        if (!found) {
            modelSel.value = '__custom__';
            val('acCustomModel').value = model;
            var cw = page.querySelector('#acCustomModelWrap');
            if (cw) cw.style.display = '';
        } else {
            var cw2 = page.querySelector('#acCustomModelWrap');
            if (cw2) cw2.style.display = 'none';
        }

        // Strumenti
        val('acMaxSearchResults').value = cfg.MaxSearchResults || 10;
        val('acFilterToAnimeOnly').checked = cfg.FilterToAnimeOnly;
        val('acCacheHours').value = cfg.CacheHours || 48;
        val('acNegativeCacheHours').value = cfg.NegativeCacheHours || 12;
        val('acRequestDelay').value = cfg.RequestDelayMilliseconds || 1000;
        val('acBaseUrl').value = cfg.BaseUrl || 'https://www.animeclick.it';
        val('acUserAgent').value = cfg.UserAgent || '';

        updateFeatureChips(cfg);
        markClean();
    }

    /* ===== read form back to config ===== */

    function readForm(cfg) {
        cfg.PreferItalianTitle = val('acPreferItalianTitle').checked;
        cfg.EnablePlot = val('acEnablePlot').checked;
        cfg.OverwriteNonItalianFields = val('acOverwriteNonItalianFields').checked;
        cfg.EnableAnimeClickImages = val('acEnableAnimeClickImages').checked;
        cfg.EnableGenres = val('acEnableGenres').checked;
        cfg.EnableStudios = val('acEnableStudios').checked;
        cfg.EnableCommunityRating = val('acEnableCommunityRating').checked;
        cfg.EnableCast = val('acEnableCast').checked;
        cfg.EnableTags = val('acEnableTags').checked;
        cfg.EnableEpisodeTitles = val('acEnableEpisodeTitles').checked;
        cfg.EnableThemeSongs = val('acEnableThemeSongs').checked;
        cfg.EnableCollections = val('acEnableCollections').checked;

        cfg.EnableEpisodeSynopsisTranslation = val('acEnableEpisodeSynopsisTranslation').checked;
        cfg.EnableTvdbSynopsis = val('acEnableTvdbSynopsis').checked;
        cfg.TvdbApiKey = val('acTvdbApiKey').value.trim();
        cfg.TvdbLanguage = val('acTvdbLanguage').value.trim() || 'ita';
        cfg.TmdbApiKey = val('acTmdbApiKey').value.trim();
        cfg.OllamaCloudApiKey = val('acOllamaCloudApiKey').value.trim();
        cfg.OllamaCloudEndpoint = val('acOllamaCloudEndpoint').value.trim();
        cfg.EpisodeTranslationTimeoutSec = parseInt(val('acEpisodeTranslationTimeoutSec').value, 10) || 30;

        var modelSel = val('acOllamaCloudModel');
        cfg.OllamaCloudModel = modelSel.value === '__custom__'
            ? (val('acCustomModel').value.trim() || 'gemma4:cloud')
            : modelSel.value;

        cfg.MaxSearchResults = parseInt(val('acMaxSearchResults').value, 10) || 10;
        cfg.FilterToAnimeOnly = val('acFilterToAnimeOnly').checked;
        cfg.CacheHours = parseInt(val('acCacheHours').value, 10) || 48;
        cfg.NegativeCacheHours = parseInt(val('acNegativeCacheHours').value, 10) || 12;
        cfg.RequestDelayMilliseconds = parseInt(val('acRequestDelay').value, 10) || 1000;
        cfg.BaseUrl = val('acBaseUrl').value.trim() || 'https://www.animeclick.it';
        cfg.UserAgent = val('acUserAgent').value.trim();

        return cfg;
    }

    /* ===== feature chips ===== */

    function updateFeatureChips(cfg) {
        var container = page.querySelector('#acFeatureChips');
        if (!container) return;
        clear(container);
        var features = [
            ['Titolo IT', cfg.PreferItalianTitle],
            ['Trama', cfg.EnablePlot],
            ['Immagini AC', cfg.EnableAnimeClickImages],
            ['Generi', cfg.EnableGenres],
            ['Studi', cfg.EnableStudios],
            ['Rating', cfg.EnableCommunityRating],
            ['Cast', cfg.EnableCast],
            ['Tag', cfg.EnableTags],
            ['Ep. titoli', cfg.EnableEpisodeTitles],
            ['Sigle', cfg.EnableThemeSongs],
            ['Collezioni', cfg.EnableCollections],
            ['Sinossi EP', cfg.EnableEpisodeSynopsisTranslation],
            ['TVDB', cfg.EnableTvdbSynopsis],
            ['Solo anime', cfg.FilterToAnimeOnly]
        ];
        features.forEach(function (f) {
            var chip = el('span', 'ac-chip ' + (f[1] ? 'ac-chip-on' : 'ac-chip-off'), (f[1] ? '✓ ' : '✗ ') + f[0]);
            container.appendChild(chip);
        });
    }

    /* ===== wire actions ===== */

    function wireActions() {
        // Save
        page.querySelector('#acBtnSave').addEventListener('click', function () {
            getPluginConfig().then(function (cfg) {
                readForm(cfg);
                return savePluginConfig(cfg);
            }).then(function () {
                toast('Configurazione salvata', 'success');
                return getPluginConfig();
            }).then(function (cfg) {
                loadForm(cfg);
            }).catch(function (e) {
                toast('Errore: ' + e.message, 'error');
            });
        });

        // Discard
        page.querySelector('#acBtnDiscard').addEventListener('click', function () {
            if (savedConfig) loadForm(savedConfig);
            markClean();
        });

        // Test providers
        ['tmdb', 'ollama', 'tvdb'].forEach(function (prov) {
            var endpoints = { tmdb: 'Plugins/AnimeClick/TestTmdb', ollama: 'Plugins/AnimeClick/TestOllama', tvdb: 'Plugins/AnimeClick/TestTvdb' };
            page.querySelector('#acTest_' + prov).addEventListener('click', function () {
                var dot = page.querySelector('#acDot_' + prov);
                var badge = page.querySelector('#acBadge_' + prov);
                var detail = page.querySelector('#acDetail_' + prov);
                dot.className = 'ac-live-dot is-idle';
                badge.className = 'ac-badge neutral';
                badge.textContent = '…';

                request('POST', endpoints[prov]).then(function (r) {
                    var ok = r && (r.success || r.Success);
                    dot.className = 'ac-live-dot ' + (ok ? 'is-ok' : 'is-error');
                    badge.className = 'ac-badge ' + (ok ? 'success' : 'danger');
                    badge.textContent = ok ? 'Connesso' : 'Errore';
                    if (detail) {
                        detail.textContent = JSON.stringify(r, null, 2);
                        detail.style.display = '';
                    }
                }).catch(function (e) {
                    dot.className = 'ac-live-dot is-error';
                    badge.className = 'ac-badge danger';
                    badge.textContent = 'Errore';
                    if (detail) {
                        detail.textContent = e.message;
                        detail.style.display = '';
                    }
                });
            });
        });

        // Clear cache
        page.querySelector('#acBtnClearCache').addEventListener('click', function () {
            confirmModal('Svuota cache', 'Eliminare tutta la cache dei metadati scaricati?').then(function (ok) {
                if (!ok) return;
                var res = page.querySelector('#acCacheResult');
                res.className = 'ac-state';
                res.innerHTML = '<span class="ac-spinner"></span>Svuotamento in corso…';
                request('POST', 'Plugins/AnimeClick/ClearCache').then(function (r) {
                    res.className = 'ac-state success';
                    res.textContent = '✓ Cache svuotata' + (r && r.message ? ': ' + r.message : '');
                    toast('Cache svuotata', 'success');
                }).catch(function (e) {
                    res.className = 'ac-state error';
                    res.textContent = '✗ Errore: ' + e.message;
                    toast('Errore: ' + e.message, 'error');
                });
            });
        });

        // Identify & Refresh
        page.querySelector('#acBtnIdentify').addEventListener('click', function () {
            var itemId = val('acItemId').value.trim();
            var acId = val('acAnimeClickId').value.trim();
            var replace = val('acReplaceAllImages').checked;
            if (!itemId || !acId) {
                toast('Inserisci entrambi gli ID', 'error');
                return;
            }
            var resBox = page.querySelector('#acIdentifyResult');
            resBox.style.display = '';
            resBox.textContent = '';
            resBox.innerHTML = '<span class="ac-spinner"></span>Identificazione in corso…';

            request('POST', 'Plugins/AnimeClick/IdentifyAndRefresh', {
                itemId: itemId,
                animeClickId: acId,
                replaceAllImages: replace
            }).then(function (r) {
                resBox.textContent = JSON.stringify(r, null, 2);
                toast('Identificazione completata', 'success');
            }).catch(function (e) {
                resBox.textContent = '✗ Errore: ' + e.message;
                toast('Errore: ' + e.message, 'error');
            });
        });
    }

    /* ===== hero stats ===== */

    function updateHeroStats(cfg) {
        var statEl = page.querySelector('#acStatProviders');
        if (statEl) {
            var count = 0;
            if (cfg.TmdbApiKey) count++;
            if (cfg.OllamaCloudApiKey) count++;
            if (cfg.TvdbApiKey) count++;
            statEl.querySelector('.ac-stat-value').textContent = count + '/3';
            statEl.querySelector('.ac-stat-sub').textContent = 'configurati';
            statEl.className = 'ac-stat ' + (count === 3 ? 'good' : count > 0 ? 'warn' : '');
        }
        var cacheStat = page.querySelector('#acStatCache');
        if (cacheStat) {
            cacheStat.querySelector('.ac-stat-value').textContent = cfg.CacheHours + 'h';
            cacheStat.querySelector('.ac-stat-sub').textContent = 'durata cache';
        }
        var featStat = page.querySelector('#acStatFeatures');
        if (featStat) {
            var active = [cfg.PreferItalianTitle, cfg.EnablePlot, cfg.EnableAnimeClickImages,
                cfg.EnableGenres, cfg.EnableStudios, cfg.EnableCommunityRating,
                cfg.EnableCast, cfg.EnableTags, cfg.EnableEpisodeTitles,
                cfg.EnableThemeSongs, cfg.EnableCollections, cfg.EnableEpisodeSynopsisTranslation].filter(Boolean).length;
            featStat.querySelector('.ac-stat-value').textContent = active + '/12';
            featStat.querySelector('.ac-stat-sub').textContent = 'funzionalità attive';
            featStat.className = 'ac-stat ' + (active > 8 ? 'good' : active > 4 ? 'warn' : '');
        }
    }

    /* ===== main show ===== */

    function show(pageEl) {
        page = pageEl;

        // Build panels
        buildOverviewPanel();
        buildMetadatiPanel();
        buildSinossiPanel();
        buildStrumentiPanel();
        initTabs();
        wireActions();

        // Load config
        getPluginConfig().then(function (cfg) {
            loadForm(cfg);
            updateHeroStats(cfg);
        }).catch(function (e) {
            toast('Errore caricamento config: ' + e.message, 'error');
        });
    }

    /* ===== export ===== */

    window.AC = window.AC || {};
    window.AC.config = { show: show };

})();
