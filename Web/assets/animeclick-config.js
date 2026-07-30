/* AnimeClick — premium configuration dashboard.
   Cloud-first onboarding, authority visibility and privacy-safe diagnostics. */
(function () {
    'use strict';

    var V = '0.4.5.0';
    var GUID = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';
    var page;
    var savedConfig;
    var dirty = false;
    var toastHost;

    /* ===== utilities ===== */

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function el(tag, cls, text) {
        var node = document.createElement(tag);
        if (cls) node.className = cls;
        if (text != null) node.textContent = text;
        return node;
    }

    function clear(node) {
        while (node && node.firstChild) node.removeChild(node.firstChild);
    }

    function valueOf(obj, name) {
        if (!obj) return undefined;
        if (obj[name] !== undefined) return obj[name];
        var pascal = name.charAt(0).toUpperCase() + name.slice(1);
        return obj[pascal];
    }

    function asArray(value) {
        return Array.isArray(value) ? value : [];
    }

    function truncate(value, max) {
        var text = String(value == null ? '' : value);
        return text.length > max ? text.slice(0, max - 1) + '…' : text;
    }

    function setBusy(button, busy, idleLabel, busyLabel) {
        if (!button) return;
        button.disabled = !!busy;
        button.textContent = busy ? busyLabel : idleLabel;
    }

    function val(id) {
        return page.querySelector('#' + id);
    }

    /* ===== authenticated API ===== */

    function authHeader() {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
                var token = ApiClient.accessToken();
                if (token) return 'MediaBrowser Token="' + token + '"';
            }
        } catch (error) {
            // Jellyfin may inject ApiClient after the page markup; requests will still
            // use same-origin credentials when no explicit token is available.
        }
        return null;
    }

    function apiUrl(path) {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function') {
                return ApiClient.getUrl(path);
            }
        } catch (error) {
            // Fall back to a relative server URL.
        }
        return path;
    }

    function request(method, path, body) {
        var headers = { Accept: 'application/json' };
        var auth = authHeader();
        if (auth) headers.Authorization = auth;
        var options = { method: method, credentials: 'same-origin', headers: headers };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }

        return fetch(apiUrl(path), options).then(function (response) {
            var isJson = (response.headers.get('Content-Type') || '').toLowerCase().indexOf('json') > -1;
            if (!response.ok) {
                return (isJson ? response.json() : response.text()).catch(function () { return null; })
                    .then(function (payload) {
                        var message = payload && (payload.error || payload.message);
                        if (!message && typeof payload === 'string') message = payload;
                        var error = new Error(message || ('HTTP ' + response.status));
                        error.status = response.status;
                        throw error;
                    });
            }
            if (response.status === 204 || !isJson) return null;
            return response.json();
        });
    }

    function getPluginConfig() {
        return new Promise(function (resolve, reject) {
            try {
                ApiClient.getPluginConfiguration(GUID).then(resolve, reject);
            } catch (error) {
                reject(error);
            }
        });
    }

    function savePluginConfig(config) {
        return new Promise(function (resolve, reject) {
            try {
                ApiClient.updatePluginConfiguration(GUID, config).then(resolve, reject);
            } catch (error) {
                reject(error);
            }
        });
    }

    /* ===== feedback ===== */

    function ensureToastHost() {
        if (!toastHost || !document.body.contains(toastHost)) {
            toastHost = el('div', 'ac-toast-host');
            document.body.appendChild(toastHost);
        }
    }

    function toast(message, type) {
        ensureToastHost();
        var item = el('div', 'ac-toast' + (type ? ' ' + type : ''), message);
        toastHost.appendChild(item);
        setTimeout(function () {
            item.classList.add('leaving');
            setTimeout(function () {
                if (item.parentNode) item.parentNode.removeChild(item);
            }, 200);
        }, 3400);
    }

    function confirmModal(title, message) {
        return new Promise(function (resolve) {
            var veil = el('div', 'ac-modal-veil');
            var modal = el('div', 'ac-modal');
            var heading = el('h3', null, title);
            var copy = el('p', null, message);
            var actions = el('div', 'ac-row');
            var cancel = el('button', 'ac-btn ac-btn-ghost', 'Annulla');
            var confirm = el('button', 'ac-btn ac-btn-primary', 'Conferma');
            cancel.type = 'button';
            confirm.type = 'button';
            actions.appendChild(cancel);
            actions.appendChild(confirm);
            modal.appendChild(heading);
            modal.appendChild(copy);
            modal.appendChild(actions);
            veil.appendChild(modal);
            document.body.appendChild(veil);
            cancel.onclick = function () { veil.remove(); resolve(false); };
            confirm.onclick = function () { veil.remove(); resolve(true); };
        });
    }

    function markDirty() {
        dirty = true;
        var bar = page.querySelector('#acSaveBar');
        if (bar) bar.style.display = '';
        updateProviderPresence();
    }

    function markClean() {
        dirty = false;
        var bar = page.querySelector('#acSaveBar');
        if (bar) bar.style.display = 'none';
    }

    /* ===== form primitives ===== */

    function makeCard(kicker, title, copy) {
        var card = el('section', 'ac-card ac-card-flush');
        var head = el('div', 'ac-card-head ac-card-head-copy');
        var titleBox = el('div', 'ac-stack ac-card-titlebox');
        if (kicker) titleBox.appendChild(el('p', 'ac-kicker', kicker));
        titleBox.appendChild(el('h2', 'ac-subtitle', title));
        if (copy) titleBox.appendChild(el('p', 'ac-note', copy));
        head.appendChild(titleBox);
        var body = el('div', 'ac-card-body');
        card.appendChild(head);
        card.appendChild(body);
        return { card: card, head: head, body: body };
    }

    function makeCheck(id, label, description, trackDirty) {
        var wrap = el('div', 'ac-check');
        var labelNode = el('label');
        var checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'ac-checkbox';
        checkbox.id = id;
        labelNode.appendChild(checkbox);
        var copy = el('span');
        copy.innerHTML = '<span class="ac-check-title">' + esc(label) + '</span>';
        if (description) copy.innerHTML += '<span class="ac-field-desc">' + description + '</span>';
        labelNode.appendChild(copy);
        wrap.appendChild(labelNode);
        if (trackDirty !== false) checkbox.addEventListener('change', markDirty);
        return wrap;
    }

    function makeField(id, label, type, description, attrs, trackDirty) {
        var wrap = el('div', 'ac-field');
        var labelNode = el('label', null, label);
        labelNode.setAttribute('for', id);
        var input = el('input', 'ac-input');
        input.type = type || 'text';
        input.id = id;
        Object.keys(attrs || {}).forEach(function (key) {
            input.setAttribute(key, attrs[key]);
        });
        wrap.appendChild(labelNode);
        wrap.appendChild(input);
        if (description) {
            var desc = el('div', 'ac-field-desc');
            desc.innerHTML = description;
            wrap.appendChild(desc);
        }
        if (trackDirty !== false) {
            input.addEventListener('input', markDirty);
            input.addEventListener('change', markDirty);
        }
        return wrap;
    }

    function makeSecretField(id, label, description) {
        var wrap = el('div', 'ac-field');
        var labelNode = el('label', null, label);
        labelNode.setAttribute('for', id);
        var control = el('div', 'ac-input-action');
        var input = el('input', 'ac-input');
        input.type = 'password';
        input.id = id;
        input.autocomplete = 'new-password';
        var toggle = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Mostra');
        toggle.type = 'button';
        toggle.setAttribute('data-secret-toggle', id);
        control.appendChild(input);
        control.appendChild(toggle);
        wrap.appendChild(labelNode);
        wrap.appendChild(control);
        if (description) {
            var desc = el('div', 'ac-field-desc');
            desc.innerHTML = description;
            wrap.appendChild(desc);
        }
        input.addEventListener('input', markDirty);
        input.addEventListener('change', markDirty);
        return wrap;
    }

    function makeTextArea(id, label, description, trackDirty) {
        var wrap = el('div', 'ac-field');
        var labelNode = el('label', null, label);
        labelNode.setAttribute('for', id);
        var input = el('textarea', 'ac-input ac-textarea');
        input.id = id;
        input.rows = 5;
        wrap.appendChild(labelNode);
        wrap.appendChild(input);
        if (description) wrap.appendChild(el('div', 'ac-field-desc', description));
        if (trackDirty !== false) input.addEventListener('input', markDirty);
        return wrap;
    }

    function makeDetails(summary, copy) {
        var details = el('details', 'ac-details');
        var summaryNode = el('summary', null, summary);
        details.appendChild(summaryNode);
        var body = el('div', 'ac-details-body');
        if (copy) body.appendChild(el('p', 'ac-note', copy));
        details.appendChild(body);
        return { details: details, body: body };
    }

    function makeCallout(title, copy, tone) {
        var callout = el('div', 'ac-callout' + (tone ? ' ' + tone : ''));
        callout.appendChild(el('strong', null, title));
        callout.appendChild(el('span', null, copy));
        return callout;
    }

    /* ===== navigation ===== */

    function activateTab(tab, shouldFocus) {
        if (!tab) return;
        page.querySelectorAll('.ac-tab').forEach(function (item) {
            var selected = item === tab;
            item.setAttribute('aria-selected', selected ? 'true' : 'false');
            item.tabIndex = selected ? 0 : -1;
        });
        page.querySelectorAll('.ac-panel').forEach(function (panel) {
            var selected = panel.dataset.panel === tab.dataset.panel;
            panel.classList.toggle('active', selected);
            panel.setAttribute('aria-hidden', selected ? 'false' : 'true');
        });
        if (shouldFocus) tab.focus();
    }

    function initTabs() {
        var tabs = Array.prototype.slice.call(page.querySelectorAll('.ac-tab'));
        tabs.forEach(function (tab, index) {
            tab.addEventListener('click', function () {
                activateTab(tab, false);
            });
            tab.addEventListener('keydown', function (event) {
                var nextIndex = null;
                if (event.key === 'ArrowRight') nextIndex = (index + 1) % tabs.length;
                if (event.key === 'ArrowLeft') nextIndex = (index - 1 + tabs.length) % tabs.length;
                if (event.key === 'Home') nextIndex = 0;
                if (event.key === 'End') nextIndex = tabs.length - 1;
                if (nextIndex == null) return;
                event.preventDefault();
                activateTab(tabs[nextIndex], true);
            });
        });
        activateTab(page.querySelector('.ac-tab[aria-selected="true"]') || tabs[0], false);
    }

    /* ===== overview ===== */

    function addPriorityTile(container, label, value, copy, tone) {
        var tile = el('div', 'ac-priority-tile' + (tone ? ' ' + tone : ''));
        tile.appendChild(el('span', 'ac-kicker', label));
        tile.appendChild(el('strong', 'ac-priority-value', value));
        tile.appendChild(el('span', 'ac-note', copy));
        container.appendChild(tile);
    }

    function buildOverviewPanel() {
        var panel = page.querySelector('#acPanelOverview');
        clear(panel);

        var authority = makeCard(
            'AnimeClick-first',
            'Metadati italiani protetti',
            'AnimeClick viene eseguito per primo e i valori prodotti nel refresh sono riapplicati dopo il merge, rispettando i lock di Jellyfin.'
        );
        var priorityGrid = el('div', 'ac-priority-grid');
        addPriorityTile(priorityGrid, 'Testo', 'Ordine 0', 'Titoli, trama, generi, tag e cast restano autorevoli.', 'good');
        addPriorityTile(priorityGrid, 'Immagini', 'Fallback 100', 'I provider ad alta risoluzione mantengono la precedenza.', 'neutral');
        addPriorityTile(priorityGrid, 'Sinossi episodi', 'AnimeClick → IT → EN', 'Prima AnimeClick; Ollama traduce soltanto l’ultima fonte inglese.', 'warn');
        authority.body.appendChild(priorityGrid);
        panel.appendChild(authority.card);

        var providers = makeCard(
            'Connettività',
            'Salute dei provider di fallback',
            'I test usano i valori attualmente inseriti nel modulo. Le API key non vengono mai incluse nei risultati mostrati.'
        );
        ['tmdb', 'ollama', 'tvdb'].forEach(function (provider) {
            var names = { tmdb: 'TMDB', ollama: 'Ollama Cloud', tvdb: 'TheTVDB' };
            var roles = {
                tmdb: 'Italiano nativo e fonte inglese',
                ollama: 'Ultimo fallback EN→IT, cloud-only',
                tvdb: 'Prima fonte esterna italiana'
            };
            var row = el('div', 'ac-provider-row');
            var identity = el('div', 'ac-provider-identity');
            var dot = el('span', 'ac-live-dot is-idle');
            dot.id = 'acDot_' + provider;
            var copy = el('div', 'ac-stack ac-provider-copy');
            copy.appendChild(el('strong', null, names[provider]));
            copy.appendChild(el('span', 'ac-field-desc', roles[provider]));
            identity.appendChild(dot);
            identity.appendChild(copy);
            var actions = el('div', 'ac-row');
            var badge = el('span', 'ac-badge neutral', 'Non verificato');
            badge.id = 'acBadge_' + provider;
            var button = el('button', 'ac-btn ac-btn-sm', 'Verifica');
            button.type = 'button';
            button.setAttribute('data-ac-test', provider);
            actions.appendChild(badge);
            actions.appendChild(button);
            row.appendChild(identity);
            row.appendChild(actions);
            providers.body.appendChild(row);
            var detail = el('div', 'ac-state ac-provider-detail');
            detail.id = 'acDetail_' + provider;
            detail.style.display = 'none';
            providers.body.appendChild(detail);
        });
        panel.appendChild(providers.card);

        var libraries = makeCard(
            'Jellyfin',
            'Priorità effettiva nelle librerie',
            'La verifica distingue l’ordine configurato dai provider realmente abilitati. Le librerie non anime possono lasciare AnimeClick disattivato intenzionalmente.'
        );
        var refresh = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Aggiorna');
        refresh.type = 'button';
        refresh.id = 'acBtnRefreshLibraries';
        libraries.head.appendChild(refresh);
        var libraryResult = el('div', 'ac-library-list');
        libraryResult.id = 'acLibraryHealth';
        libraryResult.appendChild(el('div', 'ac-state', 'Caricamento librerie…'));
        libraries.body.appendChild(libraryResult);
        panel.appendChild(libraries.card);

        var features = makeCard(
            'Copertura',
            'Funzionalità attive',
            'Riepilogo della configurazione salvata. I campi non disponibili restano agli altri provider Jellyfin.'
        );
        var chips = el('div', 'ac-row');
        chips.id = 'acFeatureChips';
        features.body.appendChild(chips);
        panel.appendChild(features.card);
    }

    /* ===== metadata ===== */

    function buildMetadatiPanel() {
        var panel = page.querySelector('#acPanelMetadati');
        clear(panel);
        panel.appendChild(makeCallout(
            'Strategia fill-gaps sicura',
            'AnimeClick protegge i metadati italiani che possiede; gli altri provider possono completare soltanto i campi mancanti.',
            'good'
        ));

        var primary = makeCard('Essenziali', 'Identità italiana', 'I valori principali che definiscono la scheda nel catalogo.');
        primary.body.appendChild(makeCheck('acPreferItalianTitle', 'Titolo italiano', 'Usa il titolo AnimeClick come nome principale.'));
        primary.body.appendChild(makeCheck('acEnablePlot', 'Trama italiana', 'Importa la sinossi AnimeClick quando disponibile.'));
        panel.appendChild(primary.card);

        var enrichment = makeCard('Copertura', 'Arricchimento semantico', 'Ogni dato viene scritto soltanto nel campo Jellyfin semanticamente corrispondente.');
        var grid = el('div', 'ac-grid-2');
        grid.appendChild(makeCheck('acEnableGenres', 'Generi', 'Generi localizzati in italiano.'));
        grid.appendChild(makeCheck('acEnableTags', 'Tag e origine', 'Target, tag generici e opera di origine.'));
        grid.appendChild(makeCheck('acEnableProductionLocations', 'Nazionalità', 'Mappata come località di produzione.'));
        grid.appendChild(makeCheck('acEnableTrailers', 'Trailer e PV', 'Solo video YouTube esplicitamente etichettati.'));
        grid.appendChild(makeCheck('acEnableCast', 'Cast e staff', 'Doppiatori e ruoli staff granulari.'));
        grid.appendChild(makeCheck('acEnableEpisodeTitles', 'Titoli episodi', 'Titoli italiani dalla lista episodi.'));
        grid.appendChild(makeCheck('acEnableThemeSongs', 'Sigle', 'Nomi di opening ed ending nei tag.'));
        enrichment.body.appendChild(grid);
        panel.appendChild(enrichment.card);

        var images = makeCard('Artwork', 'Immagini come fallback', 'AnimeClick resta deliberatamente dopo i provider di artwork ad alta risoluzione.');
        images.body.appendChild(makeCheck('acEnableAnimeClickImages', 'Abilita locandina AnimeClick', 'Usala solo quando i provider immagini precedenti non producono un poster.'));
        images.body.appendChild(makeField(
            'acMinPosterWidth',
            'Larghezza minima locandina',
            'number',
            'Sotto questa soglia il poster viene scartato. 0 disabilita il filtro; 400 px è il valore consigliato.',
            { min: '0', max: '2000', step: '50' }
        ));
        panel.appendChild(images.card);

        var advanced = makeDetails(
            'Opzioni avanzate e potenzialmente invasive',
            'Queste impostazioni non sono necessarie per il flusso AnimeClick-first consigliato.'
        );
        advanced.body.appendChild(makeCheck(
            'acOverwriteNonItalianFields',
            'Sovrascrivi campi non italiani',
            'Consente ad AnimeClick di sostituire anche titolo originale, studio, rating e data. Lascia disattivato per un merge conservativo.'
        ));
        advanced.body.appendChild(makeCheck(
            'acEnableStudios',
            'Studi AnimeClick',
            'Effettivo soltanto quando la sovrascrittura dei campi non italiani è attiva.'
        ));
        advanced.body.appendChild(makeCheck(
            'acEnableCommunityRating',
            'Valutazione community AnimeClick',
            'Effettiva soltanto quando la sovrascrittura dei campi non italiani è attiva.'
        ));
        advanced.body.appendChild(makeCheck(
            'acEnableCollections',
            'Collezioni automatiche',
            'Crea raggruppamenti da relazioni sequel, prequel e spin-off.'
        ));
        panel.appendChild(advanced.details);
    }

    /* ===== synopsis fallback ===== */

    function appendChainStep(chain, number, title, copy, badge) {
        var step = el('div', 'ac-chain-step');
        step.appendChild(el('span', 'ac-step-number', String(number)));
        var content = el('div', 'ac-stack ac-grow');
        var heading = el('div', 'ac-row');
        heading.appendChild(el('strong', null, title));
        if (badge) heading.appendChild(el('span', 'ac-badge neutral', badge));
        content.appendChild(heading);
        content.appendChild(el('span', 'ac-field-desc', copy));
        step.appendChild(content);
        chain.appendChild(step);
    }

    function buildSinossiPanel() {
        var panel = page.querySelector('#acPanelSinossi');
        clear(panel);

        var onboarding = makeCard(
            'Sinossi episodi',
            'Prima AnimeClick, poi le fonti di riserva',
            'Titoli e sinossi restano indipendenti. Per la sinossi il plugin controlla prima la pagina dell’episodio AnimeClick e ignora descrizioni vuote o segnaposto come “Episodio 12”.'
        );
        onboarding.body.appendChild(makeCheck(
            'acEnableEpisodeSynopsisTranslation',
            'Abilita le sinossi degli episodi',
            'Funziona anche con il solo AnimeClick. Le API esterne e Ollama servono esclusivamente ad aumentare la copertura.'
        ));
        var chain = el('div', 'ac-chain');
        appendChainStep(chain, 1, 'AnimeClick', 'Sinossi italiana presente nella pagina dell’episodio.', 'prima scelta');
        appendChainStep(chain, 2, 'Italiano di riserva', 'TheTVDB ita, poi TMDB it-IT.', 'senza AI');
        appendChainStep(chain, 3, 'Inglese + Ollama', 'TMDB en-US, poi TheTVDB eng; Ollama traduce il testo EN→IT.', 'ultima scelta');
        onboarding.body.appendChild(chain);
        panel.appendChild(onboarding.card);

        var sources = makeCard('Sorgenti opzionali', 'Aumenta la copertura', 'Non servono chiavi per usare le sinossi AnimeClick. TheTVDB e TMDB cercano le puntate mancanti; Ollama traduce soltanto una sinossi inglese trovata da uno di questi servizi.');
        var sourceGrid = el('div', 'ac-grid-2');
        var tvdbBlock = el('div', 'ac-credential-block');
        var tvdbHead = el('div', 'ac-row ac-credential-head');
        tvdbHead.appendChild(el('strong', null, 'TheTVDB'));
        tvdbHead.appendChild(el('span', 'ac-badge neutral', 'prima fonte esterna'));
        tvdbBlock.appendChild(tvdbHead);
        tvdbBlock.appendChild(makeCheck('acEnableTvdbSynopsis', 'Usa fonte italiana TVDB', 'Salta Ollama quando esiste una overview ita.'));
        tvdbBlock.appendChild(makeSecretField(
            'acTvdbApiKey',
            'API key TheTVDB',
            'Disponibile dal <a href="https://thetvdb.com/dashboard" target="_blank" rel="noopener noreferrer">dashboard TheTVDB</a>.'
        ));
        var tvdbTest = el('button', 'ac-btn ac-btn-sm', 'Verifica TheTVDB');
        tvdbTest.type = 'button';
        tvdbTest.setAttribute('data-ac-test', 'tvdb');
        tvdbBlock.appendChild(tvdbTest);
        var tvdbResult = el('div', 'ac-state');
        tvdbResult.id = 'acInlineResult_tvdb';
        tvdbBlock.appendChild(tvdbResult);
        sourceGrid.appendChild(tvdbBlock);

        var cloudBlock = el('div', 'ac-credential-block featured');
        var cloudHead = el('div', 'ac-row ac-credential-head');
        cloudHead.appendChild(el('strong', null, 'TMDB + Ollama Cloud'));
        cloudHead.appendChild(el('span', 'ac-badge success', 'cloud'));
        cloudBlock.appendChild(cloudHead);
        cloudBlock.appendChild(makeSecretField(
            'acTmdbApiKey',
            'API key TMDB',
            'Creala nelle <a href="https://developer.themoviedb.org/docs/getting-started" target="_blank" rel="noopener noreferrer">impostazioni API TMDB</a>.'
        ));
        cloudBlock.appendChild(makeSecretField(
            'acOllamaCloudApiKey',
            'API key Ollama Cloud',
            'Creala su <a href="https://ollama.com/settings/keys" target="_blank" rel="noopener noreferrer">ollama.com/settings/keys</a>.'
        ));
        cloudBlock.appendChild(makeField(
            'acOllamaCloudModel',
            'Modello cloud',
            'text',
            'Consigliato: <strong>gpt-oss:20b-cloud</strong> (piccolo, economico, buona resa EN→IT). Puoi indicare un altro modello Ollama Cloud (suffisso -cloud), es. gemma4:31b-cloud.',
            { spellcheck: 'false', autocomplete: 'off', placeholder: 'gpt-oss:20b-cloud' }
        ));
        var modelReset = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Ripristina modello consigliato');
        modelReset.type = 'button';
        modelReset.id = 'acBtnRecommendedModel';
        cloudBlock.appendChild(modelReset);
        var cloudTests = el('div', 'ac-row');
        var tmdbTest = el('button', 'ac-btn ac-btn-sm', 'Verifica TMDB');
        tmdbTest.type = 'button';
        tmdbTest.setAttribute('data-ac-test', 'tmdb');
        var ollamaTest = el('button', 'ac-btn ac-btn-sm', 'Verifica Ollama');
        ollamaTest.type = 'button';
        ollamaTest.setAttribute('data-ac-test', 'ollama');
        cloudTests.appendChild(tmdbTest);
        cloudTests.appendChild(ollamaTest);
        cloudBlock.appendChild(cloudTests);
        var tmdbResult = el('div', 'ac-state');
        tmdbResult.id = 'acInlineResult_tmdb';
        var ollamaResult = el('div', 'ac-state');
        ollamaResult.id = 'acInlineResult_ollama';
        cloudBlock.appendChild(tmdbResult);
        cloudBlock.appendChild(ollamaResult);
        sourceGrid.appendChild(cloudBlock);
        sources.body.appendChild(sourceGrid);

        var providerAttribution = el('p', 'ac-field-desc');
        providerAttribution.innerHTML = 'Metadata provided by TheTVDB. Considera di <a href="https://thetvdb.com/subscribe" target="_blank" rel="noopener noreferrer">supportare TheTVDB</a>. Dati TMDB usati secondo i relativi termini API.';
        sources.body.appendChild(providerAttribution);
        panel.appendChild(sources.card);

        var advanced = makeDetails(
            'Parametri avanzati cloud e cache',
            'I valori predefiniti sono ottimizzati per Ollama Cloud e per un NAS senza accelerazione GPU.'
        );
        advanced.body.appendChild(makeField(
            'acOllamaCloudEndpoint',
            'Endpoint Ollama Cloud',
            'url',
            'Endpoint chat ufficiale. Non inserire endpoint locali per questo profilo cloud-only.',
            { placeholder: 'https://ollama.com/api/chat' }
        ));
        var advancedGrid = el('div', 'ac-grid-2');
        advancedGrid.appendChild(makeField(
            'acEpisodeTranslationTimeoutSec',
            'Timeout richiesta',
            'number',
            'Secondi per una singola chiamata.',
            { min: '5', max: '120' }
        ));
        advancedGrid.appendChild(makeField(
            'acTranslationCacheHours',
            'Cache traduzioni',
            'number',
            'Ore di conservazione. Il default equivale a circa 10 anni e viene comunque invalidato quando cambia il contenuto.',
            { min: '1', max: '87600' }
        ));
        advanced.body.appendChild(advancedGrid);
        panel.appendChild(advanced.details);

        var preview = makeCard('Diagnostica', 'Test e anteprima traduzione', 'Usa gli stessi prompt, modello, cache e limite di concorrenza della pipeline reale. Le credenziali non sono restituite dalla API.');
        preview.body.appendChild(makeTextArea(
            'acPreviewSource',
            'Testo inglese',
            'Incolla una breve sinossi da tradurre. Massimo 8000 caratteri.',
            false
        ));
        var previewButton = el('button', 'ac-btn ac-btn-primary', 'Genera anteprima');
        previewButton.type = 'button';
        previewButton.id = 'acBtnPreviewTranslation';
        preview.body.appendChild(previewButton);
        var previewResult = el('div', 'ac-preview-result');
        previewResult.id = 'acTranslationPreviewResult';
        previewResult.style.display = 'none';
        preview.body.appendChild(previewResult);
        panel.appendChild(preview.card);

        var fallback = makeCard('Pipeline reale', 'Prova la sinossi di un episodio', 'Inserisci la serie e il numero della puntata: il plugin trova da solo la pagina episodio AnimeClick e mostra quale fonte ha vinto. Usa soltanto la configurazione già salvata.');
        var fallbackGrid = el('div', 'ac-grid-3');
        fallbackGrid.appendChild(makeField('acFallbackAnimeId', 'ID AnimeClick della serie', 'text', 'Esempio: 72/naruto.', {}, false));
        fallbackGrid.appendChild(makeField('acFallbackSeason', 'Stagione', 'number', '0 per gli special.', { min: '0', value: '1' }, false));
        fallbackGrid.appendChild(makeField('acFallbackEpisode', 'Episodio', 'number', 'Numero episodio; per gli special usa stagione 0.', { min: '1', value: '1' }, false));
        fallback.body.appendChild(fallbackGrid);
        var fallbackButton = el('button', 'ac-btn', 'Esegui catena salvata');
        fallbackButton.type = 'button';
        fallbackButton.id = 'acBtnPreviewFallback';
        fallback.body.appendChild(fallbackButton);
        var fallbackResult = el('div', 'ac-preview-result');
        fallbackResult.id = 'acFallbackPreviewResult';
        fallbackResult.style.display = 'none';
        fallback.body.appendChild(fallbackResult);
        panel.appendChild(fallback.card);
    }

    /* ===== tools ===== */

    function buildStrumentiPanel() {
        var panel = page.querySelector('#acPanelStrumenti');
        clear(panel);

        var identify = makeCard('Manutenzione elemento', 'Identifica e aggiorna', 'Associa un elemento Jellyfin a un ID AnimeClick e avvia un refresh completo dei provider configurati.');
        var identifyGrid = el('div', 'ac-grid-2');
        identifyGrid.appendChild(makeField('acItemId', 'ID elemento Jellyfin', 'text', 'ID interno dell’elemento.', {}, false));
        identifyGrid.appendChild(makeField('acAnimeClickId', 'ID AnimeClick', 'text', 'Numero o numero/slug.', {}, false));
        identify.body.appendChild(identifyGrid);
        identify.body.appendChild(makeCheck(
            'acReplaceAllImages',
            'Sostituisci tutte le immagini',
            'Rimuove gli artwork esistenti prima del refresh. Operazione reversibile con un nuovo refresh immagini.',
            false
        ));
        var identifyButton = el('button', 'ac-btn ac-btn-primary', 'Identifica e aggiorna');
        identifyButton.type = 'button';
        identifyButton.id = 'acBtnIdentify';
        identify.body.appendChild(identifyButton);
        var identifyResult = el('div', 'ac-preview-result');
        identifyResult.id = 'acIdentifyResult';
        identifyResult.style.display = 'none';
        identify.body.appendChild(identifyResult);
        panel.appendChild(identify.card);

        var cache = makeCard('Cache', 'Invalidazione controllata', 'Svuota tutta la cache soltanto quando vuoi forzare una nuova acquisizione dei metadati.');
        var cacheActions = el('div', 'ac-row');
        var clearButton = el('button', 'ac-btn ac-btn-danger', 'Svuota tutta la cache');
        clearButton.type = 'button';
        clearButton.id = 'acBtnClearCache';
        cacheActions.appendChild(clearButton);
        var cacheResult = el('span', 'ac-state');
        cacheResult.id = 'acCacheResult';
        cacheActions.appendChild(cacheResult);
        cache.body.appendChild(cacheActions);
        panel.appendChild(cache.card);

        var episodeLayout = makeDetails(
            'Resolver episodi e stagioni',
            'Il resolver v5 usa automaticamente gruppi AnimeClick, timeline canonica e struttura reale della libreria. Inserisci un override soltanto per serie note con layout eccezionale.'
        );
        episodeLayout.body.appendChild(makeCallout(
            'Fail-safe per serie anomale',
            'Una riga per serie: ID=flat, ID=explicit oppure ID=13,24 con confini cumulativi. Le righe vuote, i commenti con # e le righe non valide vengono ignorati.',
            'warn'
        ));
        var overrideField = makeTextArea(
            'acEpisodeLayoutOverrides',
            'Override layout episodi',
            'Esempi: 72=flat · 123=explicit · 456=13,24. In 13,24 la stagione 1 termina al globale 13 e la stagione 2 al globale 24.'
        );
        var overrideInput = overrideField.querySelector('textarea');
        overrideInput.rows = 7;
        overrideInput.spellcheck = false;
        overrideInput.autocomplete = 'off';
        overrideInput.placeholder = '# Solo per eccezioni confermate\n72=flat\n123=explicit\n456=13,24';
        episodeLayout.body.appendChild(overrideField);
        episodeLayout.body.appendChild(el(
            'p',
            'ac-field-desc warn',
            'Un override errato può impedire un match che il resolver automatico avrebbe risolto: preferisci sempre la modalità automatica.'
        ));
        panel.appendChild(episodeLayout.details);

        var advanced = makeDetails(
            'Ricerca, rete e compatibilità',
            'Modifica questi valori solo per diagnostica o se AnimeClick cambia comportamento.'
        );
        var grid = el('div', 'ac-grid-2');
        grid.appendChild(makeField('acMaxSearchResults', 'Risultati ricerca', 'number', 'Da 1 a 25.', { min: '1', max: '25' }));
        grid.appendChild(makeField('acCacheHours', 'Cache metadati', 'number', 'Ore, da 1 a 720.', { min: '1', max: '720' }));
        grid.appendChild(makeField('acNegativeCacheHours', 'Cache negativa', 'number', 'Ore, da 1 a 168.', { min: '1', max: '168' }));
        grid.appendChild(makeField('acRequestDelay', 'Pausa richieste', 'number', 'Millisecondi tra richieste AnimeClick.', { min: '500', max: '10000' }));
        advanced.body.appendChild(grid);
        advanced.body.appendChild(makeField('acBaseUrl', 'URL base AnimeClick', 'url', 'Modifica solo in caso di cambio dominio.'));
        advanced.body.appendChild(makeField('acUserAgent', 'User-Agent', 'text', 'Identificativo HTTP del plugin.', { spellcheck: 'false' }));
        panel.appendChild(advanced.details);
    }

    /* ===== configuration mapping ===== */

    function setChecked(id, value, fallback) {
        var input = val(id);
        input.checked = value == null ? fallback : !!value;
    }

    function setValue(id, value, fallback) {
        var input = val(id);
        input.value = value == null || value === '' ? fallback : value;
    }

    function normalizeOllamaEndpoint(value) {
        try {
            var endpoint = new URL(String(value || '').trim());
            if (endpoint.protocol !== 'https:' || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
                return null;
            }
            return endpoint.href;
        } catch (error) {
            return null;
        }
    }

    function ollamaEndpointChanged() {
        var current = normalizeOllamaEndpoint(val('acOllamaCloudEndpoint').value);
        var stored = normalizeOllamaEndpoint(savedConfig && savedConfig.OllamaCloudEndpoint);
        return current == null || stored == null || current !== stored;
    }

    function ollamaCredentialAvailable() {
        return !!val('acOllamaCloudApiKey').value.trim()
            || (!ollamaEndpointChanged() && !!(savedConfig && savedConfig.OllamaCloudApiKey));
    }

    function loadForm(config) {
        savedConfig = config;

        setChecked('acPreferItalianTitle', config.PreferItalianTitle, true);
        setChecked('acEnablePlot', config.EnablePlot, true);
        setChecked('acOverwriteNonItalianFields', config.OverwriteNonItalianFields, false);
        setChecked('acEnableAnimeClickImages', config.EnableAnimeClickImages, true);
        setValue('acMinPosterWidth', config.MinPosterWidth, 400);
        setChecked('acEnableGenres', config.EnableGenres, true);
        setChecked('acEnableStudios', config.EnableStudios, true);
        setChecked('acEnableCommunityRating', config.EnableCommunityRating, true);
        setChecked('acEnableCast', config.EnableCast, true);
        setChecked('acEnableTags', config.EnableTags, true);
        setChecked('acEnableProductionLocations', config.EnableProductionLocations, true);
        setChecked('acEnableTrailers', config.EnableTrailers, true);
        setChecked('acEnableEpisodeTitles', config.EnableEpisodeTitles, true);
        setValue('acEpisodeLayoutOverrides', config.EpisodeLayoutOverrides, '');
        setChecked('acEnableThemeSongs', config.EnableThemeSongs, true);
        setChecked('acEnableCollections', config.EnableCollections, false);

        setChecked('acEnableEpisodeSynopsisTranslation', config.EnableEpisodeSynopsisTranslation, false);
        setChecked('acEnableTvdbSynopsis', config.EnableTvdbSynopsis, false);
        setValue('acTvdbApiKey', config.TvdbApiKey, '');
        setValue('acTmdbApiKey', config.TmdbApiKey, '');
        var ollamaKeyInput = val('acOllamaCloudApiKey');
        ollamaKeyInput.value = '';
        ollamaKeyInput.placeholder = config.OllamaCloudApiKey
            ? 'Chiave salvata — lascia vuoto per mantenerla'
            : 'Inserisci la chiave Ollama Cloud';
        setValue('acOllamaCloudEndpoint', config.OllamaCloudEndpoint, 'https://ollama.com/api/chat');
        setValue('acOllamaCloudModel', config.OllamaCloudModel, 'gpt-oss:20b-cloud');
        setValue('acEpisodeTranslationTimeoutSec', config.EpisodeTranslationTimeoutSec, 30);
        setValue('acTranslationCacheHours', config.TranslationCacheHours, 87600);

        setValue('acMaxSearchResults', config.MaxSearchResults, 10);
        setValue('acCacheHours', config.CacheHours, 48);
        setValue('acNegativeCacheHours', config.NegativeCacheHours, 12);
        setValue('acRequestDelay', config.RequestDelayMilliseconds, 1000);
        setValue('acBaseUrl', config.BaseUrl, 'https://www.animeclick.it');
        setValue('acUserAgent', config.UserAgent, '');

        updateFeatureChips(config);
        updateHeroStats(config);
        updateProviderPresence();
        markClean();
    }

    function readForm(config) {
        config.PreferItalianTitle = val('acPreferItalianTitle').checked;
        config.EnablePlot = val('acEnablePlot').checked;
        config.OverwriteNonItalianFields = val('acOverwriteNonItalianFields').checked;
        config.EnableAnimeClickImages = val('acEnableAnimeClickImages').checked;
        config.MinPosterWidth = parseInt(val('acMinPosterWidth').value, 10) || 0;
        config.EnableGenres = val('acEnableGenres').checked;
        config.EnableStudios = val('acEnableStudios').checked;
        config.EnableCommunityRating = val('acEnableCommunityRating').checked;
        config.EnableCast = val('acEnableCast').checked;
        config.EnableTags = val('acEnableTags').checked;
        config.EnableProductionLocations = val('acEnableProductionLocations').checked;
        config.EnableTrailers = val('acEnableTrailers').checked;
        config.EnableEpisodeTitles = val('acEnableEpisodeTitles').checked;
        config.EpisodeLayoutOverrides = val('acEpisodeLayoutOverrides').value.trim();
        config.EnableThemeSongs = val('acEnableThemeSongs').checked;
        config.EnableCollections = val('acEnableCollections').checked;

        config.EnableEpisodeSynopsisTranslation = val('acEnableEpisodeSynopsisTranslation').checked;
        config.EnableTvdbSynopsis = val('acEnableTvdbSynopsis').checked;
        config.TvdbApiKey = val('acTvdbApiKey').value.trim();
        config.TmdbApiKey = val('acTmdbApiKey').value.trim();
        var enteredOllamaKey = val('acOllamaCloudApiKey').value.trim();
        var enteredOllamaEndpoint = val('acOllamaCloudEndpoint').value.trim()
            || 'https://ollama.com/api/chat';
        var normalizedOllamaEndpoint = normalizeOllamaEndpoint(enteredOllamaEndpoint);
        var freshOllamaEndpoint = normalizeOllamaEndpoint(config.OllamaCloudEndpoint);
        if (normalizedOllamaEndpoint == null) {
            throw new Error('L’endpoint Ollama deve essere HTTPS e non può includere credenziali, query o frammenti.');
        }
        if (enteredOllamaKey) {
            config.OllamaCloudApiKey = enteredOllamaKey;
        } else if (freshOllamaEndpoint == null || normalizedOllamaEndpoint !== freshOllamaEndpoint) {
            // The configuration may have changed in another admin tab after this
            // page loaded. Never pair its newly persisted key with our stale URL.
            throw new Error('Il profilo Ollama è cambiato sul server: ricarica la pagina o reinserisci la chiave.');
        }
        config.OllamaCloudEndpoint = normalizedOllamaEndpoint;
        config.OllamaCloudModel = val('acOllamaCloudModel').value.trim() || 'gpt-oss:20b-cloud';
        config.EpisodeTranslationTimeoutSec = parseInt(val('acEpisodeTranslationTimeoutSec').value, 10) || 30;
        config.TranslationCacheHours = parseInt(val('acTranslationCacheHours').value, 10) || 87600;

        config.MaxSearchResults = parseInt(val('acMaxSearchResults').value, 10) || 10;
        config.CacheHours = parseInt(val('acCacheHours').value, 10) || 48;
        config.NegativeCacheHours = parseInt(val('acNegativeCacheHours').value, 10) || 12;
        config.RequestDelayMilliseconds = parseInt(val('acRequestDelay').value, 10) || 1000;
        config.BaseUrl = val('acBaseUrl').value.trim() || 'https://www.animeclick.it';
        config.UserAgent = val('acUserAgent').value.trim();
        return config;
    }

    function validateForm() {
        var enteredEndpoint = val('acOllamaCloudEndpoint').value.trim()
            || 'https://ollama.com/api/chat';
        if (normalizeOllamaEndpoint(enteredEndpoint) == null) {
            return 'L’endpoint Ollama deve essere HTTPS e non può includere credenziali, query o frammenti.';
        }

        var ollamaKey = val('acOllamaCloudApiKey').value.trim();
        if (ollamaEndpointChanged() && !ollamaKey) {
            return 'Reinserisci la chiave Ollama dopo aver cambiato endpoint.';
        }

        var model = val('acOllamaCloudModel').value.trim();
        if (ollamaCredentialAvailable() && !/cloud$/i.test(model)) {
            return 'Il profilo cloud-only richiede un modello con tag cloud.';
        }

        return null;
    }

    /* ===== summaries ===== */

    function updateFeatureChips(config) {
        var container = page.querySelector('#acFeatureChips');
        if (!container) return;
        clear(container);
        var features = [
            ['Titolo IT', config.PreferItalianTitle],
            ['Trama IT', config.EnablePlot],
            ['Generi', config.EnableGenres],
            ['Tag', config.EnableTags],
            ['Nazionalità', config.EnableProductionLocations],
            ['Trailer/PV', config.EnableTrailers],
            ['Cast/Staff', config.EnableCast],
            ['Studi', config.EnableStudios],
            ['Rating', config.EnableCommunityRating],
            ['Titoli episodi', config.EnableEpisodeTitles],
            ['Sigle', config.EnableThemeSongs],
            ['Poster fallback', config.EnableAnimeClickImages],
            ['Sinossi episodi', config.EnableEpisodeSynopsisTranslation],
            ['TVDB IT', config.EnableTvdbSynopsis]
        ];
        features.forEach(function (feature) {
            var enabled = !!feature[1];
            container.appendChild(el(
                'span',
                'ac-chip ' + (enabled ? 'ac-chip-on' : 'ac-chip-off'),
                (enabled ? 'Attivo · ' : 'Off · ') + feature[0]
            ));
        });
    }

    function updateHeroStats(config) {
        var providerStat = page.querySelector('#acStatProviders');
        if (providerStat) {
            var count = [config.TmdbApiKey, config.OllamaCloudApiKey, config.TvdbApiKey].filter(Boolean).length;
            providerStat.querySelector('.ac-stat-value').textContent = count + '/3';
            providerStat.querySelector('.ac-stat-sub').textContent = 'fallback configurati';
            providerStat.className = 'ac-stat ' + (count === 3 ? 'good' : count > 0 ? 'warn' : '');
        }
        var cacheStat = page.querySelector('#acStatCache');
        if (cacheStat) {
            cacheStat.querySelector('.ac-stat-value').textContent = (config.CacheHours || 48) + 'h';
            cacheStat.querySelector('.ac-stat-sub').textContent = 'cache metadati';
        }
        var featureStat = page.querySelector('#acStatFeatures');
        if (featureStat) {
            var active = [
                config.PreferItalianTitle,
                config.EnablePlot,
                config.EnableAnimeClickImages,
                config.EnableGenres,
                config.OverwriteNonItalianFields && config.EnableStudios,
                config.OverwriteNonItalianFields && config.EnableCommunityRating,
                config.EnableCast,
                config.EnableTags,
                config.EnableProductionLocations,
                config.EnableTrailers,
                config.EnableEpisodeTitles,
                config.EnableThemeSongs,
                config.EnableEpisodeSynopsisTranslation
            ].filter(Boolean).length;
            featureStat.querySelector('.ac-stat-value').textContent = active + '/13';
            featureStat.querySelector('.ac-stat-sub').textContent = 'funzionalità attive';
            featureStat.className = 'ac-stat ' + (active >= 10 ? 'good' : active >= 6 ? 'warn' : '');
        }
    }

    function setProviderState(provider, state, label, detail) {
        var dot = page.querySelector('#acDot_' + provider);
        var badge = page.querySelector('#acBadge_' + provider);
        var detailNode = page.querySelector('#acDetail_' + provider);
        if (dot) dot.className = 'ac-live-dot ' + (state === 'ok' ? 'is-ok' : state === 'error' ? 'is-error' : 'is-idle');
        if (badge) {
            badge.className = 'ac-badge ' + (state === 'ok' ? 'success' : state === 'error' ? 'danger' : 'neutral');
            badge.textContent = label;
        }
        if (detailNode) {
            detailNode.textContent = detail || '';
            detailNode.style.display = detail ? '' : 'none';
        }
    }

    function updateProviderPresence() {
        if (!page || !val('acTmdbApiKey')) return;
        var present = {
            tmdb: !!val('acTmdbApiKey').value.trim(),
            ollama: ollamaCredentialAvailable(),
            tvdb: !!val('acTvdbApiKey').value.trim()
        };
        Object.keys(present).forEach(function (provider) {
            setProviderState(provider, 'idle', present[provider] ? 'Da verificare' : 'Non configurato', '');
        });
    }

    /* ===== library provider health ===== */

    function includesName(list, expected) {
        var target = expected.toLowerCase();
        return list.some(function (item) { return String(item).toLowerCase() === target; });
    }

    function activeOrder(typeOptions, kind) {
        var enabled = asArray(valueOf(typeOptions, kind + 'Fetchers'));
        var ordered = asArray(valueOf(typeOptions, kind + 'FetcherOrder'));
        var result = ordered.filter(function (name) { return includesName(enabled, name); });
        enabled.forEach(function (name) {
            if (!includesName(result, name)) result.push(name);
        });
        return result;
    }

    function analyzeProvider(typeOptions, kind) {
        var enabled = asArray(valueOf(typeOptions, kind + 'Fetchers'));
        if (!includesName(enabled, 'AnimeClick')) {
            return { enabled: false, label: 'AnimeClick non abilitato', tone: 'neutral' };
        }
        var order = activeOrder(typeOptions, kind);
        var index = order.findIndex(function (name) { return String(name).toLowerCase() === 'animeclick'; });
        var position = index < 0 ? 1 : index + 1;
        if (kind === 'Metadata') {
            return {
                enabled: true,
                label: position === 1 ? 'Metadata #1' : 'Metadata #' + position,
                tone: position === 1 ? 'success' : 'warn'
            };
        }
        var isFallback = order.length <= 1 || position === order.length;
        return {
            enabled: true,
            label: isFallback ? 'Immagini fallback #' + position : 'Immagini #' + position,
            tone: isFallback ? 'success' : 'warn'
        };
    }

    function renderLibraryHealth(folders) {
        var host = page.querySelector('#acLibraryHealth');
        clear(host);
        if (!folders.length) {
            host.appendChild(el('div', 'ac-empty', 'Nessuna libreria disponibile.'));
            return;
        }

        var relevantTypes = ['Series', 'Season', 'Episode', 'Movie'];
        folders.forEach(function (folder) {
            var options = valueOf(folder, 'libraryOptions') || {};
            var typeOptions = asArray(valueOf(options, 'typeOptions')).filter(function (entry) {
                return relevantTypes.indexOf(String(valueOf(entry, 'type'))) > -1;
            });
            if (!typeOptions.length) return;

            var library = el('div', 'ac-library-card');
            var heading = el('div', 'ac-library-heading');
            heading.appendChild(el('strong', null, valueOf(folder, 'name') || 'Libreria'));
            var collectionType = valueOf(folder, 'collectionType');
            if (collectionType) heading.appendChild(el('span', 'ac-badge neutral', collectionType));
            library.appendChild(heading);

            typeOptions.forEach(function (entry) {
                var row = el('div', 'ac-library-type');
                row.appendChild(el('span', 'ac-library-type-name', valueOf(entry, 'type') || 'Tipo'));
                var badges = el('div', 'ac-row');
                var metadata = analyzeProvider(entry, 'Metadata');
                var metadataBadge = el('span', 'ac-badge ' + metadata.tone, metadata.label);
                badges.appendChild(metadataBadge);
                var images = analyzeProvider(entry, 'Image');
                if (images.enabled) badges.appendChild(el('span', 'ac-badge ' + images.tone, images.label));
                row.appendChild(badges);
                library.appendChild(row);
            });
            host.appendChild(library);
        });

        if (!host.children.length) {
            host.appendChild(el('div', 'ac-empty', 'Nessun tipo Series, Season, Episode o Movie trovato.'));
        }
    }

    function loadLibraryHealth() {
        var host = page.querySelector('#acLibraryHealth');
        if (!host) return;
        clear(host);
        host.appendChild(el('div', 'ac-state', 'Verifica delle priorità in corso…'));
        request('GET', 'Library/VirtualFolders').then(function (folders) {
            renderLibraryHealth(asArray(folders));
        }).catch(function (error) {
            clear(host);
            host.appendChild(makeCallout('Verifica non disponibile', truncate(error.message, 240), 'warn'));
        });
    }

    /* ===== diagnostics ===== */

    function providerPayload(provider) {
        if (provider === 'tmdb') {
            return { apiKey: val('acTmdbApiKey').value.trim() };
        }
        if (provider === 'tvdb') {
            return {
                apiKey: val('acTvdbApiKey').value.trim()
            };
        }

        var payload = {
            model: val('acOllamaCloudModel').value.trim() || 'gpt-oss:20b-cloud',
            timeoutSec: parseInt(val('acEpisodeTranslationTimeoutSec').value, 10) || 30
        };
        var enteredApiKey = val('acOllamaCloudApiKey').value.trim();
        if (ollamaEndpointChanged() || enteredApiKey) {
            var visibleEndpoint = val('acOllamaCloudEndpoint').value.trim();
            payload.endpoint = normalizeOllamaEndpoint(visibleEndpoint) || visibleEndpoint;
        }
        if (enteredApiKey) {
            payload.apiKey = enteredApiKey;
        }
        return payload;
    }

    function formatProviderResult(provider, result, success) {
        var status = valueOf(result, 'statusCode');
        var error = valueOf(result, 'errorMessage');
        if (!success) return truncate(error || ('Verifica fallita' + (status ? ' · HTTP ' + status : '')), 300);
        if (provider === 'tmdb') {
            return 'Connessione valida' + (status ? ' · HTTP ' + status : '') +
                (valueOf(result, 'sampleName') ? ' · Risultato: ' + truncate(valueOf(result, 'sampleName'), 80) : '');
        }
        if (provider === 'tvdb') {
            return 'Autenticazione e endpoint episodi validi' +
                (valueOf(result, 'effectiveLanguage') ? ' · lingua ' + valueOf(result, 'effectiveLanguage') : '');
        }
        return 'Ollama Cloud raggiungibile' + (status ? ' · HTTP ' + status : '') +
            ' · modello ' + (valueOf(result, 'model') || val('acOllamaCloudModel').value.trim());
    }

    function runProviderTest(provider, button) {
        var endpoints = {
            tmdb: 'Plugins/AnimeClick/TestTmdb',
            ollama: 'Plugins/AnimeClick/TestOllama',
            tvdb: 'Plugins/AnimeClick/TestTvdb'
        };
        var inline = page.querySelector('#acInlineResult_' + provider);
        setBusy(button, true, button.getAttribute('data-idle-label') || button.textContent, 'Verifica…');
        if (!button.getAttribute('data-idle-label')) button.setAttribute('data-idle-label', button.textContent === 'Verifica…' ? 'Verifica' : button.textContent);
        if (inline) {
            inline.className = 'ac-state';
            inline.textContent = 'Connessione in corso…';
        }
        setProviderState(provider, 'idle', 'Verifica…', '');

        request('POST', endpoints[provider], providerPayload(provider)).then(function (result) {
            var success = !!valueOf(result, 'success');
            var message = formatProviderResult(provider, result, success);
            setProviderState(provider, success ? 'ok' : 'error', success ? 'Connesso' : 'Errore', message);
            if (inline) {
                inline.className = 'ac-state ' + (success ? 'success' : 'error');
                inline.textContent = message;
            }
            toast(message, success ? 'success' : 'error');
        }).catch(function (error) {
            var message = truncate(error.message, 300);
            setProviderState(provider, 'error', 'Errore', message);
            if (inline) {
                inline.className = 'ac-state error';
                inline.textContent = message;
            }
            toast('Verifica fallita: ' + message, 'error');
        }).finally(function () {
            var idle = button.getAttribute('data-idle-label') || 'Verifica';
            setBusy(button, false, idle, 'Verifica…');
        });
    }

    function renderTranslationPreview(result) {
        var host = page.querySelector('#acTranslationPreviewResult');
        host.style.display = '';
        clear(host);
        var success = !!valueOf(result, 'success');
        if (!success) {
            host.appendChild(makeCallout('Nessuna traduzione', valueOf(result, 'errorMessage') || 'Esegui prima il test Ollama.', 'warn'));
            return;
        }
        var meta = el('div', 'ac-row');
        meta.appendChild(el('span', 'ac-badge success', 'EN → IT'));
        meta.appendChild(el('span', 'ac-badge neutral', valueOf(result, 'model') || 'cloud'));
        host.appendChild(meta);
        host.appendChild(el('p', 'ac-preview-copy', valueOf(result, 'translation')));
    }

    function renderFallbackPreview(result) {
        var host = page.querySelector('#acFallbackPreviewResult');
        host.style.display = '';
        clear(host);
        var success = !!valueOf(result, 'success');
        if (!success) {
            host.appendChild(makeCallout('Nessuna fonte disponibile', valueOf(result, 'errorMessage') || 'La catena non ha prodotto una sinossi.', 'warn'));
        } else {
            var meta = el('div', 'ac-row');
            meta.appendChild(el('span', 'ac-badge success', valueOf(result, 'source') || 'Fonte'));
            meta.appendChild(el('span', 'ac-badge neutral', valueOf(result, 'sourceLanguage') || 'it'));
            if (valueOf(result, 'usedOllama')) meta.appendChild(el('span', 'ac-badge warn', valueOf(result, 'model') || 'Ollama Cloud'));
            host.appendChild(meta);
            host.appendChild(el('p', 'ac-preview-copy', valueOf(result, 'overview')));
        }

        if (!valueOf(result, 'animeClickMatchConclusive')) {
            host.appendChild(makeCallout(
                'Verifica AnimeClick non conclusiva',
                'Questa prova usa soltanto serie, stagione e numero. L’esito è indicativo: il refresh reale usa anche titolo, file e struttura della libreria e può quindi scegliere un abbinamento diverso.',
                'warn'
            ));
        }

        var chain = asArray(valueOf(result, 'chain'));
        if (chain.length) {
            var chainRow = el('div', 'ac-fallback-report');
            chain.forEach(function (step) {
                var configured = !!valueOf(step, 'configured');
                chainRow.appendChild(el(
                    'span',
                    'ac-chip ' + (configured ? 'ac-chip-on' : 'ac-chip-off'),
                    (valueOf(step, 'source') || 'Fonte') + ' · ' + (valueOf(step, 'language') || '')
                ));
            });
            host.appendChild(chainRow);
        }
    }

    /* ===== actions ===== */

    function saveCurrentConfiguration() {
        var validationError = validateForm();
        if (validationError) {
            toast(validationError, 'error');
            return Promise.reject(new Error(validationError));
        }
        return getPluginConfig().then(function (config) {
            return savePluginConfig(readForm(config));
        }).then(function () {
            return getPluginConfig();
        }).then(function (config) {
            loadForm(config);
            toast('Configurazione salvata', 'success');
            return config;
        });
    }

    function wireActions() {
        page.querySelector('#acBtnSave').addEventListener('click', function () {
            var button = this;
            setBusy(button, true, 'Salva modifiche', 'Salvataggio…');
            saveCurrentConfiguration().catch(function (error) {
                if (error.message !== validateForm()) toast('Salvataggio fallito: ' + truncate(error.message, 240), 'error');
            }).finally(function () {
                setBusy(button, false, 'Salva modifiche', 'Salvataggio…');
            });
        });

        page.querySelector('#acBtnDiscard').addEventListener('click', function () {
            if (savedConfig) loadForm(savedConfig);
        });

        page.querySelectorAll('[data-secret-toggle]').forEach(function (button) {
            button.addEventListener('click', function () {
                var input = val(button.getAttribute('data-secret-toggle'));
                var reveal = input.type === 'password';
                input.type = reveal ? 'text' : 'password';
                button.textContent = reveal ? 'Nascondi' : 'Mostra';
            });
        });

        page.querySelectorAll('[data-ac-test]').forEach(function (button) {
            button.setAttribute('data-idle-label', button.textContent);
            button.addEventListener('click', function () {
                runProviderTest(button.getAttribute('data-ac-test'), button);
            });
        });

        page.querySelector('#acBtnRecommendedModel').addEventListener('click', function () {
            val('acOllamaCloudModel').value = 'gpt-oss:20b-cloud';
            markDirty();
            toast('Modello consigliato ripristinato', 'success');
        });

        page.querySelector('#acBtnRefreshLibraries').addEventListener('click', loadLibraryHealth);

        page.querySelector('#acBtnPreviewTranslation').addEventListener('click', function () {
            var source = val('acPreviewSource').value.trim();
            if (!source) {
                toast('Inserisci una breve sinossi inglese', 'error');
                return;
            }
            var button = this;
            setBusy(button, true, 'Genera anteprima', 'Traduzione…');
            var payload = providerPayload('ollama');
            payload.sourceText = source;
            request('POST', 'Plugins/AnimeClick/PreviewTranslation', payload).then(function (result) {
                renderTranslationPreview(result);
            }).catch(function (error) {
                renderTranslationPreview({ success: false, errorMessage: truncate(error.message, 300) });
            }).finally(function () {
                setBusy(button, false, 'Genera anteprima', 'Traduzione…');
            });
        });

        page.querySelector('#acBtnPreviewFallback').addEventListener('click', function () {
            if (dirty) {
                toast('Salva prima la configurazione: la pipeline usa soltanto valori persistiti.', 'error');
                return;
            }
            var animeClickId = val('acFallbackAnimeId').value.trim();
            var season = parseInt(val('acFallbackSeason').value, 10);
            var episode = parseInt(val('acFallbackEpisode').value, 10);
            if (!animeClickId || isNaN(season) || isNaN(episode)) {
                toast('Inserisci ID AnimeClick, stagione ed episodio validi.', 'error');
                return;
            }
            var button = this;
            setBusy(button, true, 'Esegui catena salvata', 'Analisi…');
            request('POST', 'Plugins/AnimeClick/PreviewEpisodeFallback', {
                animeClickId: animeClickId,
                season: season,
                episode: episode
            }).then(renderFallbackPreview).catch(function (error) {
                renderFallbackPreview({ success: false, errorMessage: truncate(error.message, 300), chain: [] });
            }).finally(function () {
                setBusy(button, false, 'Esegui catena salvata', 'Analisi…');
            });
        });

        page.querySelector('#acBtnClearCache').addEventListener('click', function () {
            var button = this;
            confirmModal('Svuota tutta la cache', 'Vuoi invalidare metadati, risoluzioni e traduzioni memorizzate?').then(function (confirmed) {
                if (!confirmed) return;
                var result = page.querySelector('#acCacheResult');
                setBusy(button, true, 'Svuota tutta la cache', 'Svuotamento…');
                result.className = 'ac-state';
                result.textContent = 'Operazione in corso…';
                request('POST', 'Plugins/AnimeClick/ClearCache', {}).then(function (response) {
                    var removed = valueOf(response, 'removed');
                    result.className = 'ac-state success';
                    result.textContent = 'Cache svuotata' + (removed != null ? ' · ' + removed + ' elementi' : '');
                    toast('Cache svuotata', 'success');
                }).catch(function (error) {
                    result.className = 'ac-state error';
                    result.textContent = truncate(error.message, 240);
                }).finally(function () {
                    setBusy(button, false, 'Svuota tutta la cache', 'Svuotamento…');
                });
            });
        });

        page.querySelector('#acBtnIdentify').addEventListener('click', function () {
            var itemId = val('acItemId').value.trim();
            var animeClickId = val('acAnimeClickId').value.trim();
            if (!itemId || !animeClickId) {
                toast('Inserisci entrambi gli ID.', 'error');
                return;
            }
            var button = this;
            var result = page.querySelector('#acIdentifyResult');
            result.style.display = '';
            clear(result);
            result.appendChild(el('div', 'ac-state', 'Identificazione in corso…'));
            setBusy(button, true, 'Identifica e aggiorna', 'Aggiornamento…');
            request('POST', 'Plugins/AnimeClick/IdentifyAndRefresh', {
                itemId: itemId,
                animeClickId: animeClickId,
                replaceAllMetadata: false,
                replaceAllImages: val('acReplaceAllImages').checked
            }).then(function (response) {
                var success = !!valueOf(response, 'success');
                clear(result);
                result.appendChild(makeCallout(
                    success ? 'Aggiornamento completato' : 'Operazione incompleta',
                    success ? 'L’ID AnimeClick è stato salvato e il refresh è stato richiesto.' : truncate(valueOf(response, 'error') || 'Errore sconosciuto.', 300),
                    success ? 'good' : 'warn'
                ));
                toast(success ? 'Identificazione completata' : 'Identificazione incompleta', success ? 'success' : 'error');
            }).catch(function (error) {
                clear(result);
                result.appendChild(makeCallout('Errore', truncate(error.message, 300), 'warn'));
                toast('Identificazione fallita', 'error');
            }).finally(function () {
                setBusy(button, false, 'Identifica e aggiorna', 'Aggiornamento…');
            });
        });
    }

    /* ===== lifecycle ===== */

    function buildPage() {
        buildOverviewPanel();
        buildMetadatiPanel();
        buildSinossiPanel();
        buildStrumentiPanel();
        initTabs();
        wireActions();
    }

    function show(pageElement) {
        page = pageElement;
        if (page.getAttribute('data-ac-built') !== V) {
            buildPage();
            page.setAttribute('data-ac-built', V);
        }

        getPluginConfig().then(function (config) {
            loadForm(config);
            loadLibraryHealth();
        }).catch(function (error) {
            toast('Impossibile caricare la configurazione: ' + truncate(error.message, 240), 'error');
        });
    }

    window.AC = window.AC || {};
    window.AC.config = { show: show };
}());
