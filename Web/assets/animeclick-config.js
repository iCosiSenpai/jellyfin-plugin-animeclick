/* AnimeClick — premium configuration dashboard.
   Cloud-first onboarding, authority visibility and privacy-safe diagnostics. */
(function () {
    'use strict';

    var V = '0.5.1.0';
    var GUID = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';
    var page;
    var savedConfig;
    var aiProviders = [];
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

    function makeSelect(id, label, description, options) {
        var wrap = el('div', 'ac-field');
        var labelNode = el('label', null, label);
        labelNode.setAttribute('for', id);
        var select = el('select', 'ac-select');
        select.id = id;
        (options || []).forEach(function (option) {
            var node = el('option', null, option.label);
            node.value = option.value;
            select.appendChild(node);
        });
        wrap.appendChild(labelNode);
        wrap.appendChild(select);
        if (description) {
            var desc = el('div', 'ac-field-desc');
            desc.innerHTML = description;
            wrap.appendChild(desc);
        }
        select.addEventListener('change', markDirty);
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
        addPriorityTile(priorityGrid, 'Sinossi episodi', 'AnimeClick → IT → EN', 'Prima AnimeClick; l’AI traduce soltanto l’ultima fonte inglese.', 'warn');
        authority.body.appendChild(priorityGrid);
        panel.appendChild(authority.card);

        var providers = makeCard(
            'Connettività',
            'Salute dei provider di fallback',
            'I test usano i valori attualmente inseriti nel modulo. Le API key non vengono mai incluse nei risultati mostrati.'
        );
        ['tmdb', 'ai', 'tvdb'].forEach(function (provider) {
            var names = { tmdb: 'TMDB', ai: 'Traduzione AI', tvdb: 'TheTVDB' };
            var roles = {
                tmdb: 'Italiano nativo e fonte inglese',
                ai: 'Ultimo fallback EN→IT',
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
        panel.appendChild(makeCallout(
            'Le correzioni a mano vanno bloccate',
            'I campi elencati qui vengono riscritti a ogni aggiornamento dei metadati: se correggi a mano un titolo o '
            + 'una trama, usa il lucchetto di Jellyfin su quel campo, altrimenti al refresh successivo torna il valore AnimeClick.',
            'warn'
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
            'Consente ad AnimeClick di sostituire anche titolo originale, studio, rating e data. '
            + 'Attenzione: le date AnimeClick sono spesso solo l\'anno (diventano 1° gennaio) e il voto '
            + 'ha tre decimali, quindi su questi due campi TheTVDB e TMDB sono più precisi. '
            + 'Lascia disattivato per un merge conservativo.'
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
            'Funziona anche con il solo AnimeClick. Le API esterne e l’AI servono esclusivamente ad aumentare la copertura.'
        ));
        var chain = el('div', 'ac-chain');
        appendChainStep(chain, 1, 'AnimeClick', 'Sinossi italiana presente nella pagina dell’episodio.', 'prima scelta');
        appendChainStep(chain, 2, 'Italiano di riserva', 'TheTVDB ita, poi TMDB it-IT.', 'senza AI');
        appendChainStep(chain, 3, 'Inglese + AI', 'TMDB en-US, poi TheTVDB eng; l’AI traduce il testo EN→IT.', 'ultima scelta');
        onboarding.body.appendChild(chain);
        panel.appendChild(onboarding.card);

        var sources = makeCard('Sorgenti opzionali', 'Aumenta la copertura', 'Non servono chiavi per usare le sinossi AnimeClick. TheTVDB e TMDB cercano le puntate mancanti; l’AI traduce soltanto una sinossi inglese trovata da uno di questi servizi.');
        var sourceGrid = el('div', 'ac-grid-2');
        var tvdbBlock = el('div', 'ac-credential-block');
        var tvdbHead = el('div', 'ac-row ac-credential-head');
        tvdbHead.appendChild(el('strong', null, 'TheTVDB'));
        tvdbHead.appendChild(el('span', 'ac-badge neutral', 'prima fonte esterna'));
        tvdbBlock.appendChild(tvdbHead);
        tvdbBlock.appendChild(makeCheck('acEnableTvdbSynopsis', 'Usa fonte italiana TVDB', 'Salta la traduzione AI quando esiste una overview ita.'));
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
        cloudHead.appendChild(el('strong', null, 'TMDB + traduzione AI'));
        cloudHead.appendChild(el('span', 'ac-badge success', 'cloud'));
        cloudBlock.appendChild(cloudHead);
        cloudBlock.appendChild(makeSecretField(
            'acTmdbApiKey',
            'API key TMDB',
            'Creala nelle <a href="https://developer.themoviedb.org/docs/getting-started" target="_blank" rel="noopener noreferrer">impostazioni API TMDB</a>.'
        ));
        cloudBlock.appendChild(makeSelect(
            'acAiProvider',
            'Servizio AI',
            'Serve solo per tradurre una sinossi che esiste unicamente in inglese. '
            + 'L’elenco si popola dal server.',
            []
        ));
        var providerNote = el('div', 'ac-field-desc');
        providerNote.id = 'acAiProviderNote';
        cloudBlock.appendChild(providerNote);
        cloudBlock.appendChild(makeSecretField(
            'acAiApiKey',
            'API key del servizio AI',
            'Lasciala vuota per un servizio in casa: non autentica nulla e la chiave non viene mai '
            + 'inviata in chiaro.'
        ));
        cloudBlock.appendChild(makeField(
            'acAiModel',
            'Modello',
            'text',
            'Nessun valore predefinito di proposito: i fornitori ritirano e rinominano i modelli, '
            + 'quindi conviene chiederglielo con «Elenca modelli» invece di indovinare.',
            { spellcheck: 'false', autocomplete: 'off', list: 'acAiModelList', placeholder: 'scegli o incolla il nome del modello' }
        ));
        var modelList = document.createElement('datalist');
        modelList.id = 'acAiModelList';
        cloudBlock.appendChild(modelList);
        cloudBlock.appendChild(makeField(
            'acAiEndpoint',
            'Endpoint',
            'url',
            'Precompilato dal servizio scelto. HTTP in chiaro è accettato solo verso la tua rete '
            + '(<code>http://ollama:11434/api/chat</code>, <code>http://nas.local:11434/api/chat</code>).',
            { placeholder: 'https://…' }
        ));
        var aiActions = el('div', 'ac-row');
        var modelListButton = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Elenca modelli');
        modelListButton.type = 'button';
        modelListButton.id = 'acBtnAiModels';
        aiActions.appendChild(modelListButton);
        cloudBlock.appendChild(aiActions);
        var cloudTests = el('div', 'ac-row');
        var tmdbTest = el('button', 'ac-btn ac-btn-sm', 'Verifica TMDB');
        tmdbTest.type = 'button';
        tmdbTest.setAttribute('data-ac-test', 'tmdb');
        var aiTest = el('button', 'ac-btn ac-btn-sm', 'Verifica AI');
        aiTest.type = 'button';
        aiTest.setAttribute('data-ac-test', 'ai');
        cloudTests.appendChild(tmdbTest);
        cloudTests.appendChild(aiTest);
        cloudBlock.appendChild(cloudTests);
        var tmdbResult = el('div', 'ac-state');
        tmdbResult.id = 'acInlineResult_tmdb';
        var aiResult = el('div', 'ac-state');
        aiResult.id = 'acInlineResult_ai';
        cloudBlock.appendChild(tmdbResult);
        cloudBlock.appendChild(aiResult);
        sourceGrid.appendChild(cloudBlock);
        sources.body.appendChild(sourceGrid);

        var providerAttribution = el('p', 'ac-field-desc');
        providerAttribution.innerHTML = 'Metadata provided by TheTVDB. Considera di <a href="https://thetvdb.com/subscribe" target="_blank" rel="noopener noreferrer">supportare TheTVDB</a>. Dati TMDB usati secondo i relativi termini API.';
        sources.body.appendChild(providerAttribution);
        panel.appendChild(sources.card);

        var advanced = makeDetails(
            'Parametri avanzati e cache',
            'Modificali solo se il servizio scelto si comporta in modo diverso dal previsto.'
        );
        var advancedGrid = el('div', 'ac-grid-2');
        advancedGrid.appendChild(makeField(
            'acEpisodeTranslationTimeoutSec',
            'Timeout richiesta',
            'number',
            'Secondi concessi a una singola chiamata. È un tetto, non un’attesa: una risposta rapida '
            + 'non ci arriva vicino. Troppo basso taglia le traduzioni lente a metà.',
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

    /* ===== library audit ===== */

    /* Tone per cause, so the colour says how much the user can do about it: red is a real
       failure, amber is one click away, grey is a gap in the source. */
    var AUDIT_TONE = {
        Ok: 'success',
        PendingRefresh: 'warn',
        TitleNotPublished: 'neutral',
        CardHasNoTitles: 'neutral',
        CatalogNotCached: 'neutral',
        NotIdentified: 'danger',
        NumberingCollision: 'danger',
        NotMatched: 'danger',
        CardNotResolved: 'danger',
        RowVanished: 'danger',
        Locked: 'warn'
    };

    var AUDIT_SHORT = {
        Ok: 'Completa',
        PendingRefresh: 'Basta un ricontrollo',
        TitleNotPublished: 'Titolo non pubblicato',
        CardHasNoTitles: 'Scheda senza titoli',
        CatalogNotCached: 'Da analizzare',
        NotIdentified: 'Non identificata',
        NumberingCollision: 'Numerazione ripetuta',
        NotMatched: 'Nessun abbinamento',
        CardNotResolved: 'Scheda di stagione da indicare',
        RowVanished: 'Riga scomparsa',
        Locked: 'Titolo bloccato'
    };

    function auditTone(reason) {
        return AUDIT_TONE[reason] || 'neutral';
    }

    function auditShort(reason) {
        return AUDIT_SHORT[reason] || reason || 'Sconosciuto';
    }

    function buildLibreriaPanel() {
        var panel = page.querySelector('#acPanelLibreria');
        clear(panel);

        var audit = makeCard(
            'Diagnosi',
            'Quali titoli vanno sistemati',
            'Legge soltanto le schede già in cache, quindi l’analisi non produce richieste ad AnimeClick. '
            + 'Per ogni serie distingue titoli assenti, segnaposto, bloccati e titoli ormai diversi dalla riga '
            + 'autorevole AnimeClick.'
        );
        var auditActions = el('div', 'ac-row');
        var auditButton = el('button', 'ac-btn ac-btn-primary', 'Analizza la libreria');
        auditButton.type = 'button';
        auditButton.id = 'acBtnAudit';
        auditActions.appendChild(auditButton);
        var auditState = el('span', 'ac-state');
        auditState.id = 'acAuditState';
        auditActions.appendChild(auditState);
        audit.body.appendChild(auditActions);

        var summary = el('div', 'ac-priority-grid');
        summary.id = 'acAuditSummary';
        summary.style.display = 'none';
        audit.body.appendChild(summary);

        var totals = el('div', 'ac-row');
        totals.id = 'acAuditTotals';
        audit.body.appendChild(totals);

        var list = el('div', 'ac-library-list');
        list.id = 'acAuditList';
        audit.body.appendChild(list);
        panel.appendChild(audit.card);

        var quality = makeCard(
            'Sinossi e trame',
            'Qualità metadati',
            'Scansiona soltanto i metadati già presenti in Jellyfin e distingue italiano, inglese, '
            + 'testo mancante e casi incerti. Non contatta AnimeClick, TMDB, TVDB o il servizio AI. '
            + 'La riparazione automatica considera solo inglese e mancante, rispetta i lock e usa refresh non distruttivi.'
        );
        var qualityActions = el('div', 'ac-row');
        var qualityAuditButton = el('button', 'ac-btn ac-btn-primary', 'Analizza la qualità');
        qualityAuditButton.type = 'button';
        qualityAuditButton.id = 'acBtnQualityAudit';
        qualityActions.appendChild(qualityAuditButton);
        var qualityRepairButton = el('button', 'ac-btn ac-btn-ghost', 'Ripara primo lotto');
        qualityRepairButton.type = 'button';
        qualityRepairButton.id = 'acBtnQualityRepair';
        qualityRepairButton.disabled = true;
        qualityActions.appendChild(qualityRepairButton);
        var qualityState = el('span', 'ac-state');
        qualityState.id = 'acQualityState';
        qualityActions.appendChild(qualityState);
        quality.body.appendChild(qualityActions);

        var qualitySummary = el('div', 'ac-priority-grid');
        qualitySummary.id = 'acQualitySummary';
        qualitySummary.style.display = 'none';
        quality.body.appendChild(qualitySummary);
        var qualityList = el('div', 'ac-library-list');
        qualityList.id = 'acQualityList';
        quality.body.appendChild(qualityList);
        panel.appendChild(quality.card);

        qualityAuditButton.addEventListener('click', function () {
            var button = this;
            var state = page.querySelector('#acQualityState');
            var repair = page.querySelector('#acBtnQualityRepair');
            setBusy(button, true, 'Analizza la qualità', 'Analisi…');
            repair.disabled = true;
            repair._acQualityReport = null;
            state.className = 'ac-state';
            state.textContent = 'Lettura locale di serie, film ed episodi identificati…';
            request('GET', 'Plugins/AnimeClick/LibraryQualityAudit').then(function (report) {
                renderQualityAudit(report);
                var repairable = valueOf(report, 'repairableCount') || 0;
                state.className = 'ac-state success';
                state.textContent = (valueOf(report, 'itemCount') || 0) + ' elementi analizzati · '
                    + repairable + ' riparabili in sicurezza';
            }).catch(function (error) {
                state.className = 'ac-state error';
                state.textContent = truncate(error.message, 240);
                toast('Analisi qualità fallita', 'error');
            }).finally(function () {
                setBusy(button, false, 'Analizza la qualità', 'Analisi…');
            });
        });

        qualityRepairButton.addEventListener('click', function () {
            var button = this;
            var report = button._acQualityReport;
            var itemIds = qualityRepairIds(report);
            if (!itemIds.length) {
                toast('Non ci sono metadati inglesi o mancanti riparabili in sicurezza.', 'error');
                return;
            }

            var maximum = valueOf(report, 'maximumRepairItems') || itemIds.length;
            confirmModal(
                'Ripara il primo lotto',
                'Accodare il refresh non distruttivo di ' + itemIds.length + ' elementi? '
                + 'Non verranno sostituite immagini o rimossi metadati; i campi bloccati e i testi incerti restano invariati. '
                + 'Il limite per richiesta è ' + maximum + '.'
            ).then(function (confirmed) {
                if (!confirmed) return;

                var idleLabel = button.textContent;
                var state = page.querySelector('#acQualityState');
                var queuedAny = false;
                setBusy(button, true, idleLabel, 'Accodamento…');
                state.className = 'ac-state';
                state.textContent = 'Validazione e accodamento del lotto…';
                request('POST', 'Plugins/AnimeClick/LibraryQualityRepair', { itemIds: itemIds })
                    .then(function (result) {
                        var queued = valueOf(result, 'queuedCount') || 0;
                        var skipped = valueOf(result, 'skippedCount') || 0;
                        queuedAny = queued > 0;
                        state.className = 'ac-state ' + (queuedAny ? 'success' : 'error');
                        state.textContent = queued + ' refresh accodati'
                            + (skipped ? ' · ' + skipped + ' saltati dopo la verifica' : '')
                            + '. Attendi il completamento, poi analizza di nuovo per il lotto successivo.';
                        toast(
                            queuedAny ? 'Lotto di riparazione accodato' : 'Nessun elemento è risultato ancora riparabile',
                            queuedAny ? 'success' : 'error'
                        );
                    })
                    .catch(function (error) {
                        state.className = 'ac-state error';
                        state.textContent = truncate(error.message, 240);
                    })
                    .finally(function () {
                        if (queuedAny) {
                            button.disabled = true;
                            button.textContent = 'Lotto accodato';
                        } else {
                            setBusy(button, false, idleLabel, 'Accodamento…');
                        }
                    });
            });
        });

        var recheck = makeCard(
            'Manutenzione',
            'Ricontrollo dei titoli',
            'Rilegge la scheda per gli episodi già abbinati che hanno un titolo segnaposto, derivato dal file '
            + 'oppure ormai diverso da quello pubblicato su AnimeClick. È la stessa attività pianificata che gira '
            + 'ogni sette giorni: serve alle serie in corso, dove titolo e slug possono arrivare dopo la prima riga.'
        );
        var recheckActions = el('div', 'ac-row');
        var recheckButton = el('button', 'ac-btn ac-btn-primary', 'Esegui ora il ricontrollo');
        recheckButton.type = 'button';
        recheckButton.id = 'acBtnRunTitles';
        recheckActions.appendChild(recheckButton);
        var recheckState = el('span', 'ac-state');
        recheckState.id = 'acRunTitlesState';
        recheckActions.appendChild(recheckState);
        recheck.body.appendChild(recheckActions);
        panel.appendChild(recheck.card);

        panel.appendChild(makeCallout(
            'Una stagione con titoli da sistemare',
            'AnimeClick pubblica quasi ogni franchise come una scheda per cour, e la catena dei sequel non sempre è '
            + 'dimostrabile. Quando l’analisi segnala «Nessun abbinamento», apri la stagione in Jellyfin e scrivi l’ID '
            + 'AnimeClick di quel cour nel campo AnimeClick della stagione: l’ID di stagione ha la precedenza su '
            + 'quello della serie per il riconoscimento degli episodi, mentre le sinossi continuano a seguire la serie.',
            'warn'
        ));
    }

    function auditSeasonChips(item) {
        var seasons = asArray(valueOf(item, 'seasons'));
        if (!seasons.length) return null;
        var row = el('div', 'ac-row');
        seasons.forEach(function (season) {
            var number = valueOf(season, 'seasonNumber');
            var label = (number == null ? 'Speciali' : 'S' + number)
                + ' · ' + valueOf(season, 'missingTitleCount');
            var chip = el('span', 'ac-badge ' + auditTone(valueOf(season, 'reason')), label);
            var hint = valueOf(season, 'reasonLabel') || '';
            var card = valueOf(season, 'animeClickId');
            var rows = valueOf(season, 'cardRowCount');
            if (card) {
                hint += '\nScheda usata: ' + card
                    + (valueOf(season, 'cardIsResolved') ? ' (risolta per questa stagione)' : ' (scheda della serie)')
                    + (rows != null ? ' · ' + rows + ' righe' : '');
            }
            chip.title = hint;
            row.appendChild(chip);
        });
        return row;
    }

    function renderAuditSeries(item) {
        var card = el('div', 'ac-library-card');
        var heading = el('div', 'ac-library-heading');
        var year = valueOf(item, 'year');
        heading.appendChild(el('strong', null, valueOf(item, 'name') + (year ? ' (' + year + ')' : '')));
        var reason = valueOf(item, 'reason');
        heading.appendChild(el('span', 'ac-badge ' + auditTone(reason), auditShort(reason)));
        card.appendChild(heading);

        var missing = valueOf(item, 'missingTitleCount') || 0;
        var total = valueOf(item, 'episodeCount') || 0;
        var counts = missing
            ? missing + ' titoli episodio da sistemare su ' + total
            : total + ' episodi, tutti con titolo';
        var animeClickId = valueOf(item, 'animeClickId');
        if (animeClickId) counts += ' · scheda ' + animeClickId;
        var rows = valueOf(item, 'cardRowCount');
        if (rows) counts += ' · ' + rows + ' righe lette dalle schede';
        card.appendChild(el('div', 'ac-field-desc', counts));
        card.appendChild(el('div', 'ac-note', valueOf(item, 'reasonLabel') || ''));

        var chips = auditSeasonChips(item);
        if (chips) card.appendChild(chips);

        var actions = el('div', 'ac-row');
        if (animeClickId) {
            var analyze = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Analizza');
            analyze.type = 'button';
            analyze.title = 'Rilegge la scheda da AnimeClick e ricalcola la causa per questa serie.';
            analyze.addEventListener('click', function () {
                setBusy(analyze, true, 'Analizza', 'Lettura…');
                request('POST', 'Plugins/AnimeClick/LibraryAuditSeries', { itemId: valueOf(item, 'id') })
                    .then(function (fresh) {
                        card.parentNode.replaceChild(renderAuditSeries(fresh), card);
                    })
                    .catch(function (error) {
                        setBusy(analyze, false, 'Analizza', 'Lettura…');
                        toast(truncate(error.message, 240), 'error');
                    });
            });
            actions.appendChild(analyze);

            var purge = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Svuota cache');
            purge.type = 'button';
            purge.title = 'Invalida le schede memorizzate per questa serie, così il prossimo refresh le rilegge.';
            purge.addEventListener('click', function () {
                setBusy(purge, true, 'Svuota cache', 'Svuotamento…');
                request('POST', 'Plugins/AnimeClick/ClearCache', { animeClickId: animeClickId })
                    .then(function (response) {
                        toast('Cache svuotata · ' + (valueOf(response, 'removed') || 0) + ' elementi', 'success');
                    })
                    .catch(function (error) {
                        toast(truncate(error.message, 240), 'error');
                    })
                    .finally(function () {
                        setBusy(purge, false, 'Svuota cache', 'Svuotamento…');
                    });
            });
            actions.appendChild(purge);
        } else {
            var identify = el('button', 'ac-btn ac-btn-sm ac-btn-ghost', 'Identifica in Strumenti');
            identify.type = 'button';
            identify.addEventListener('click', function () {
                val('acItemId').value = valueOf(item, 'id');
                val('acAnimeClickId').value = '';
                activateTab(page.querySelector('#acTabStrumenti'), true);
                toast('ID elemento compilato: cerca l’ID AnimeClick e conferma.', 'success');
            });
            actions.appendChild(identify);
        }

        card.appendChild(actions);
        return card;
    }

    function renderAudit(report) {
        var summary = page.querySelector('#acAuditSummary');
        var totals = page.querySelector('#acAuditTotals');
        var list = page.querySelector('#acAuditList');
        clear(summary);
        clear(totals);
        clear(list);

        var series = asArray(valueOf(report, 'series'));
        var missing = valueOf(report, 'missingTitleCount') || 0;
        var episodes = valueOf(report, 'episodeCount') || 0;
        var complete = series.filter(function (item) {
            return !(valueOf(item, 'missingTitleCount') || 0);
        });

        summary.style.display = '';
        addPriorityTile(
            summary,
            'Serie',
            String(series.length),
            complete.length + ' complete, ' + (series.length - complete.length) + ' con episodi da sistemare',
            series.length === complete.length ? 'good' : 'neutral'
        );
        addPriorityTile(
            summary,
            'Titoli episodio da sistemare',
            String(missing),
            episodes ? 'su ' + episodes + ' episodi analizzati' : 'nessun episodio analizzato',
            missing ? 'warn' : 'good'
        );
        var actionable = 0;
        asArray(valueOf(report, 'totals')).forEach(function (entry) {
            var reason = valueOf(entry, 'reason');
            if (reason === 'PendingRefresh' || reason === 'RowVanished') {
                actionable += valueOf(entry, 'episodeCount') || 0;
            }
        });
        addPriorityTile(
            summary,
            'Recuperabili subito',
            String(actionable),
            actionable ? 'il titolo esiste già su AnimeClick: usa «Esegui ora il ricontrollo»' : 'niente da recuperare con un ricontrollo',
            actionable ? 'warn' : 'good'
        );

        asArray(valueOf(report, 'totals')).forEach(function (entry) {
            var reason = valueOf(entry, 'reason');
            if (reason === 'Ok') return;
            var badge = el(
                'span',
                'ac-badge ' + auditTone(reason),
                auditShort(reason) + ' · ' + valueOf(entry, 'seriesCount') + ' serie'
            );
            badge.title = valueOf(entry, 'episodeCount') + ' episodi';
            totals.appendChild(badge);
        });

        if (!valueOf(report, 'episodeTitlesEnabled')) {
            list.appendChild(makeCallout(
                'Titoli episodio disattivati',
                'Nella scheda Metadati l’opzione dei titoli episodio è spenta: finché resta così nessun titolo viene scritto.',
                'warn'
            ));
        }

        if (!series.length) {
            list.appendChild(el('div', 'ac-empty', 'Nessuna serie usa AnimeClick come provider di metadati.'));
            return;
        }

        var problems = series.filter(function (item) {
            return (valueOf(item, 'missingTitleCount') || 0) > 0;
        });
        problems.forEach(function (item) {
            list.appendChild(renderAuditSeries(item));
        });

        if (!problems.length) {
            list.appendChild(el('div', 'ac-empty', 'Tutte le serie hanno i titoli AnimeClick aggiornati.'));
        }

        if (complete.length) {
            var done = makeDetails('Serie complete (' + complete.length + ')', null);
            var doneList = el('div', 'ac-library-list');
            complete.forEach(function (item) {
                var row = el('div', 'ac-library-type');
                var year = valueOf(item, 'year');
                row.appendChild(el('span', 'ac-library-type-name', valueOf(item, 'name') + (year ? ' (' + year + ')' : '')));
                row.appendChild(el('span', 'ac-badge success', (valueOf(item, 'episodeCount') || 0) + ' episodi'));
                doneList.appendChild(row);
            });
            done.body.appendChild(doneList);
            list.appendChild(done.details);
        }
    }

    var QUALITY_TONE = {
        Italian: 'success',
        English: 'warn',
        Missing: 'danger',
        Unknown: 'neutral'
    };

    var QUALITY_LABEL = {
        Italian: 'Italiano',
        English: 'Inglese probabile',
        Missing: 'Mancante',
        Unknown: 'Lingua incerta'
    };

    function qualityTone(status) {
        return QUALITY_TONE[status] || 'neutral';
    }

    function qualityLabel(status) {
        return QUALITY_LABEL[status] || status || 'Sconosciuto';
    }

    function qualityRepairIds(report) {
        if (!report) return [];
        var maximum = valueOf(report, 'maximumRepairItems') || 100;
        var ids = [];
        asArray(valueOf(report, 'series')).forEach(function (group) {
            asArray(valueOf(group, 'items')).forEach(function (item) {
                var id = valueOf(item, 'id');
                if (valueOf(item, 'canRepair') && id && ids.length < maximum) ids.push(id);
            });
        });
        return ids;
    }

    function qualityItemHeading(item) {
        var type = valueOf(item, 'itemType');
        var name = valueOf(item, 'name') || 'Senza nome';
        if (type !== 'Episode') return (type === 'Movie' ? 'Film · ' : 'Serie · ') + name;

        var season = valueOf(item, 'seasonNumber');
        var episode = valueOf(item, 'episodeNumber');
        var coordinate = (season == null ? 'S?' : 'S' + season)
            + (episode == null ? 'E?' : 'E' + episode);
        return coordinate + ' · ' + name;
    }

    function renderQualityItem(item) {
        var row = el('div', 'ac-library-type');
        var copy = el('div', 'ac-stack ac-grow');
        copy.appendChild(el('span', 'ac-library-type-name', qualityItemHeading(item)));
        var preview = valueOf(item, 'preview');
        if (preview) copy.appendChild(el('span', 'ac-field-desc', preview));
        row.appendChild(copy);

        var badges = el('div', 'ac-row');
        var status = valueOf(item, 'status');
        var statusBadge = el('span', 'ac-badge ' + qualityTone(status), qualityLabel(status));
        var confidence = Number(valueOf(item, 'confidence'));
        if ((status === 'English' || status === 'Unknown') && isFinite(confidence) && confidence > 0) {
            statusBadge.title = 'Confidenza classificatore: ' + Math.round(confidence * 100) + '%';
        }
        badges.appendChild(statusBadge);
        if (valueOf(item, 'locked')) {
            badges.appendChild(el('span', 'ac-badge warn', 'Bloccato'));
        } else if (!valueOf(item, 'canRepair') && (status === 'English' || status === 'Missing')) {
            var disabled = el('span', 'ac-badge neutral', 'Funzione disattivata');
            disabled.title = 'Abilita la trama o le sinossi episodio nella configurazione prima di riparare.';
            badges.appendChild(disabled);
        }
        row.appendChild(badges);
        return row;
    }

    function renderQualityGroup(group) {
        var card = el('div', 'ac-library-card');
        var heading = el('div', 'ac-library-heading');
        var year = valueOf(group, 'year');
        heading.appendChild(el(
            'strong',
            null,
            (valueOf(group, 'name') || 'Senza nome') + (year ? ' (' + year + ')' : '')
        ));
        var badges = el('div', 'ac-row');
        var english = valueOf(group, 'englishCount') || 0;
        var missing = valueOf(group, 'missingCount') || 0;
        var unknown = valueOf(group, 'unknownCount') || 0;
        var locked = valueOf(group, 'lockedCount') || 0;
        if (english) badges.appendChild(el('span', 'ac-badge warn', english + ' EN'));
        if (missing) badges.appendChild(el('span', 'ac-badge danger', missing + ' mancanti'));
        if (unknown) badges.appendChild(el('span', 'ac-badge neutral', unknown + ' incerti'));
        if (locked) badges.appendChild(el('span', 'ac-badge warn', locked + ' bloccati'));
        heading.appendChild(badges);
        card.appendChild(heading);

        var items = asArray(valueOf(group, 'items'));
        card.appendChild(el(
            'div',
            'ac-field-desc',
            items.length + ' elementi da verificare su ' + (valueOf(group, 'itemCount') || items.length)
        ));
        items.forEach(function (item) {
            card.appendChild(renderQualityItem(item));
        });
        return card;
    }

    function renderQualityAudit(report) {
        var summary = page.querySelector('#acQualitySummary');
        var list = page.querySelector('#acQualityList');
        var repairButton = page.querySelector('#acBtnQualityRepair');
        clear(summary);
        clear(list);
        summary.style.display = '';

        var itemCount = valueOf(report, 'itemCount') || 0;
        var italian = valueOf(report, 'italianCount') || 0;
        var english = valueOf(report, 'englishCount') || 0;
        var missing = valueOf(report, 'missingCount') || 0;
        var unknown = valueOf(report, 'unknownCount') || 0;
        var locked = valueOf(report, 'lockedCount') || 0;
        var repairable = valueOf(report, 'repairableCount') || 0;
        addPriorityTile(summary, 'Italiano', String(italian), 'su ' + itemCount + ' elementi analizzati', 'good');
        addPriorityTile(summary, 'Inglese probabile', String(english), 'candidato alla riparazione automatica', english ? 'warn' : 'good');
        addPriorityTile(summary, 'Mancante', String(missing), 'campo vuoto da completare', missing ? 'warn' : 'good');
        addPriorityTile(summary, 'Lingua incerta', String(unknown), 'mai modificata automaticamente', 'neutral');
        addPriorityTile(summary, 'Bloccati', String(locked), 'tra i casi da verificare, protetti dai lock Jellyfin', locked ? 'warn' : 'good');

        var candidates = qualityRepairIds(report);
        repairButton._acQualityReport = report;
        repairButton.disabled = candidates.length === 0;
        repairButton.textContent = candidates.length
            ? 'Ripara primo lotto (' + candidates.length + ')'
            : 'Niente da riparare';
        repairButton.title = repairable > candidates.length
            ? repairable + ' elementi riparabili; il server ne accetta al massimo '
                + (valueOf(report, 'maximumRepairItems') || candidates.length) + ' per lotto.'
            : repairable + ' elementi riparabili.';

        if (!itemCount) {
            list.appendChild(el('div', 'ac-empty', 'Nessuna serie o film identificato con AnimeClick.'));
            return;
        }

        var groups = asArray(valueOf(report, 'series'));
        if (!groups.length) {
            list.appendChild(el('div', 'ac-empty', 'Tutti i metadati analizzati risultano in italiano.'));
            return;
        }

        if (!repairable) {
            list.appendChild(makeCallout(
                'Nessuna riparazione automatica sicura',
                'I casi rimasti sono incerti, bloccati oppure protetti da una funzione disattivata. '
                + 'L’audit li mostra, ma non li accoda.',
                'warn'
            ));
        }
        groups.forEach(function (group) {
            list.appendChild(renderQualityGroup(group));
        });
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

    /* Mirrors the server rule: TLS for anything public, plain HTTP only towards the user's own
       network, which is how a service running in the house is reached. */
    function isPrivateAiHost(host) {
        if (host === 'localhost' || host === '::1' || host === '[::1]') return true;
        var octets = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
        if (octets) {
            var a = parseInt(octets[1], 10);
            var b = parseInt(octets[2], 10);
            return a === 10 || a === 127 || (a === 169 && b === 254)
                || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
        }
        return host.indexOf('.') === -1
            || /\.(local|lan|internal)$/i.test(host)
            || /\.home\.arpa$/i.test(host);
    }

    function normalizeAiEndpoint(value) {
        try {
            var endpoint = new URL(String(value || '').trim());
            var schemeAllowed = endpoint.protocol === 'https:'
                || (endpoint.protocol === 'http:' && isPrivateAiHost(endpoint.hostname));
            if (!schemeAllowed || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
                return null;
            }
            return endpoint.href;
        } catch (error) {
            return null;
        }
    }

    function aiEndpointIsLocal() {
        try {
            return new URL(val('acAiEndpoint').value.trim()).protocol === 'http:';
        } catch (error) {
            return false;
        }
    }

    function aiEndpointChanged() {
        var current = normalizeAiEndpoint(val('acAiEndpoint').value);
        var stored = normalizeAiEndpoint(savedConfig && savedConfig.AiEndpoint);
        return current == null || stored == null || current !== stored;
    }

    function aiCredentialAvailable() {
        return !!val('acAiApiKey').value.trim()
            || (!aiEndpointChanged() && !!(savedConfig && savedConfig.AiApiKey));
    }

    /* The selectable services come from the server, so this list never drifts from the one the
       translator actually knows how to speak to. */
    function loadAiProviders() {
        return request('GET', 'Plugins/AnimeClick/AiProviders').then(function (list) {
            aiProviders = asArray(list);
            var select = val('acAiProvider');
            if (!select) return;
            var chosen = (savedConfig && savedConfig.AiProvider) || '';
            clear(select);
            aiProviders.forEach(function (provider) {
                var option = el('option', null, valueOf(provider, 'displayName'));
                option.value = valueOf(provider, 'id');
                select.appendChild(option);
            });
            if (chosen) select.value = chosen;
            updateAiProviderHint();
        }).catch(function () {
            // Without the list the saved profile still works; only the menu is unavailable.
        });
    }

    function currentAiProvider() {
        var id = val('acAiProvider') ? val('acAiProvider').value : '';
        for (var i = 0; i < aiProviders.length; i++) {
            if (valueOf(aiProviders[i], 'id') === id) return aiProviders[i];
        }
        return null;
    }

    function updateAiProviderHint() {
        var note = page.querySelector('#acAiProviderNote');
        var provider = currentAiProvider();
        if (!note) return;
        if (!provider) {
            note.innerHTML = '';
            return;
        }

        var text = esc(valueOf(provider, 'note') || '');
        var credentialUrl = valueOf(provider, 'credentialUrl');
        if (credentialUrl) {
            text += ' <a href="' + esc(credentialUrl) + '" target="_blank" rel="noopener noreferrer">'
                + (valueOf(provider, 'requiresApiKey') ? 'Crea la chiave' : 'Documentazione') + '</a>.';
        }
        note.innerHTML = text;

        var keyInput = val('acAiApiKey');
        if (keyInput) {
            keyInput.placeholder = valueOf(provider, 'requiresApiKey')
                ? 'Inserisci la chiave del servizio'
                : 'Non serve per un servizio in casa';
        }
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
        var aiKeyInput = val('acAiApiKey');
        aiKeyInput.value = '';
        aiKeyInput.placeholder = config.AiApiKey
            ? 'Chiave salvata — lascia vuoto per mantenerla'
            : 'Inserisci la chiave del servizio AI';
        setValue('acAiEndpoint', config.AiEndpoint, '');
        setValue('acAiModel', config.AiModel, '');
        if (val('acAiProvider') && config.AiProvider) val('acAiProvider').value = config.AiProvider;
        setValue('acEpisodeTranslationTimeoutSec', config.EpisodeTranslationTimeoutSec, 90);
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

        config.AiProvider = val('acAiProvider') ? val('acAiProvider').value : '';
        config.AiModel = val('acAiModel').value.trim();
        var enteredAiKey = val('acAiApiKey').value.trim();
        var enteredAiEndpoint = val('acAiEndpoint').value.trim();
        if (enteredAiEndpoint) {
            var normalizedAiEndpoint = normalizeAiEndpoint(enteredAiEndpoint);
            if (normalizedAiEndpoint == null) {
                throw new Error('L’endpoint AI deve essere HTTPS (o HTTP verso un indirizzo della tua '
                    + 'rete) e non può includere credenziali, query o frammenti.');
            }

            var freshAiEndpoint = normalizeAiEndpoint(config.AiEndpoint);
            if (!enteredAiKey && freshAiEndpoint != null && normalizedAiEndpoint !== freshAiEndpoint) {
                // The configuration may have changed in another admin tab after this
                // page loaded. Never pair its newly persisted key with our stale URL.
                throw new Error('Il profilo AI è cambiato sul server: ricarica la pagina o reinserisci la chiave.');
            }

            config.AiEndpoint = normalizedAiEndpoint;
        } else {
            config.AiEndpoint = '';
        }

        if (enteredAiKey) {
            config.AiApiKey = enteredAiKey;
        }

        config.EpisodeTranslationTimeoutSec = parseInt(val('acEpisodeTranslationTimeoutSec').value, 10) || 90;
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
        var enteredEndpoint = val('acAiEndpoint').value.trim();
        if (enteredEndpoint && normalizeAiEndpoint(enteredEndpoint) == null) {
            return 'L’endpoint AI deve essere HTTPS (o HTTP verso un indirizzo della tua rete) '
                + 'e non può includere credenziali, query o frammenti.';
        }

        // A service in the house authenticates nothing, so the credential rules do not apply to it.
        if (aiEndpointIsLocal()) {
            return null;
        }

        var provider = currentAiProvider();
        if (provider && valueOf(provider, 'requiresApiKey')
            && aiEndpointChanged() && !val('acAiApiKey').value.trim()) {
            return 'Reinserisci la chiave del servizio AI dopo aver cambiato endpoint.';
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
            var count = [config.TmdbApiKey, config.AiApiKey, config.TvdbApiKey].filter(Boolean).length;
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
            ai: aiCredentialAvailable(),
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
            provider: val('acAiProvider') ? val('acAiProvider').value : '',
            model: val('acAiModel').value.trim(),
            timeoutSec: parseInt(val('acEpisodeTranslationTimeoutSec').value, 10) || 90
        };
        var enteredApiKey = val('acAiApiKey').value.trim();
        var visibleEndpoint = val('acAiEndpoint').value.trim();
        if (visibleEndpoint && (aiEndpointChanged() || enteredApiKey)) {
            payload.endpoint = normalizeAiEndpoint(visibleEndpoint) || visibleEndpoint;
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
        var providerName = currentAiProvider() ? valueOf(currentAiProvider(), 'displayName') : 'Servizio AI';
        return providerName + ' raggiungibile' + (status ? ' · HTTP ' + status : '') +
            ' · modello ' + (valueOf(result, 'model') || val('acAiModel').value.trim());
    }

    function runProviderTest(provider, button) {
        var endpoints = {
            tmdb: 'Plugins/AnimeClick/TestTmdb',
            ai: 'Plugins/AnimeClick/TestAi',
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
            host.appendChild(makeCallout('Nessuna traduzione', valueOf(result, 'errorMessage') || 'Esegui prima «Verifica AI».', 'warn'));
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
            if (valueOf(result, 'usedAi')) meta.appendChild(el('span', 'ac-badge warn', valueOf(result, 'model') || 'AI'));
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

        page.querySelector('#acAiProvider').addEventListener('change', function () {
            // Choosing a service fills in its own endpoint and clears the model, because a model
            // name from one vendor means nothing to another.
            var provider = currentAiProvider();
            if (provider) {
                val('acAiEndpoint').value = valueOf(provider, 'chatEndpoint') || '';
                val('acAiModel').value = '';
                clear(page.querySelector('#acAiModelList'));
            }
            updateAiProviderHint();
            markDirty();
        });

        page.querySelector('#acBtnAiModels').addEventListener('click', function () {
            var button = this;
            var inline = page.querySelector('#acInlineResult_ai');
            setBusy(button, true, 'Elenca modelli', 'Lettura…');
            inline.className = 'ac-state';
            inline.textContent = 'Richiesta dell’elenco dei modelli…';
            request('POST', 'Plugins/AnimeClick/AiModels', providerPayload('ai')).then(function (result) {
                var models = asArray(valueOf(result, 'models'));
                var list = page.querySelector('#acAiModelList');
                clear(list);
                models.forEach(function (model) {
                    var option = document.createElement('option');
                    option.value = model;
                    list.appendChild(option);
                });
                if (models.length) {
                    inline.className = 'ac-state success';
                    inline.textContent = models.length + ' modelli disponibili: scrivi nel campo Modello per filtrarli.';
                } else {
                    inline.className = 'ac-state error';
                    inline.textContent = truncate(valueOf(result, 'errorMessage') || 'Nessun modello elencato.', 240);
                }
            }).catch(function (error) {
                inline.className = 'ac-state error';
                inline.textContent = truncate(error.message, 240);
            }).finally(function () {
                setBusy(button, false, 'Elenca modelli', 'Lettura…');
            });
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
            var payload = providerPayload('ai');
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

        page.querySelector('#acBtnAudit').addEventListener('click', function () {
            var button = this;
            var state = page.querySelector('#acAuditState');
            setBusy(button, true, 'Analizza la libreria', 'Analisi…');
            state.className = 'ac-state';
            state.textContent = 'Lettura della libreria e delle schede in cache…';
            request('GET', 'Plugins/AnimeClick/LibraryAudit').then(function (report) {
                renderAudit(report);
                var missing = valueOf(report, 'missingTitleCount') || 0;
                state.className = 'ac-state success';
                state.textContent = (valueOf(report, 'seriesCount') || 0) + ' serie analizzate · '
                    + (missing ? missing + ' titoli episodio da sistemare' : 'nessun titolo da sistemare');
            }).catch(function (error) {
                state.className = 'ac-state error';
                state.textContent = truncate(error.message, 240);
                toast('Analisi fallita', 'error');
            }).finally(function () {
                setBusy(button, false, 'Analizza la libreria', 'Analisi…');
            });
        });

        page.querySelector('#acBtnRunTitles').addEventListener('click', function () {
            var button = this;
            var state = page.querySelector('#acRunTitlesState');
            setBusy(button, true, 'Esegui ora il ricontrollo', 'Avvio…');
            state.className = 'ac-state';
            state.textContent = 'Accodamento…';
            request('POST', 'Plugins/AnimeClick/RunMissingTitlesTask').then(function (response) {
                state.className = 'ac-state success';
                state.textContent = valueOf(response, 'message') || 'Ricontrollo accodato.';
                toast('Ricontrollo dei titoli avviato', 'success');
            }).catch(function (error) {
                state.className = 'ac-state error';
                state.textContent = truncate(error.message, 240);
            }).finally(function () {
                setBusy(button, false, 'Esegui ora il ricontrollo', 'Avvio…');
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
        buildLibreriaPanel();
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
            loadAiProviders();
            loadLibraryHealth();
        }).catch(function (error) {
            toast('Impossibile caricare la configurazione: ' + truncate(error.message, 240), 'error');
        });
    }

    window.AC = window.AC || {};
    window.AC.config = { show: show };
}());
