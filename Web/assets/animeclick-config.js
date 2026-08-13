/* AnimeClick — premium configuration dashboard.
   Cloud-first onboarding, authority visibility and privacy-safe diagnostics. */
(function () {
    'use strict';

    var V = '0.5.4.0';
    var GUID = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';
    var page;
    var savedConfig;
    var aiProviders = [];
    var dirty = false;
    var toastHost;
    var activeConfirm = null;
    var confirmModalSequence = 0;

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
        var shouldReturnFocus = !!busy && document.activeElement === button;
        if (shouldReturnFocus) {
            var focusSelector = '[role="status"], .ac-state, .ac-preview-result';
            var fallback = button.parentElement && button.parentElement.querySelector(focusSelector);
            var card = typeof button.closest === 'function' ? button.closest('.ac-card') : null;
            if (!fallback && card) fallback = card.querySelector(focusSelector);
            button._acReturnFocus = true;
            button._acFocusFallback = fallback || null;
            if (fallback) {
                fallback.tabIndex = -1;
                fallback.focus();
            }
        }
        button.disabled = !!busy;
        button.setAttribute('aria-busy', busy ? 'true' : 'false');
        button.textContent = busy ? busyLabel : idleLabel;
        if (!busy && button._acReturnFocus) {
            var focusFallback = button._acFocusFallback;
            button._acReturnFocus = false;
            button._acFocusFallback = null;
            Promise.resolve().then(function () {
                var focusStayedManaged = focusFallback
                    ? document.activeElement === focusFallback
                    : document.activeElement === document.body;
                if (!focusStayedManaged) return;
                if (document.body.contains(button) && !button.disabled && button.getClientRects().length) {
                    button.focus();
                } else if (focusFallback && document.body.contains(focusFallback)
                    && focusFallback.getClientRects().length) {
                    focusFallback.focus();
                }
            });
        }
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
        item.setAttribute('role', type === 'error' ? 'alert' : 'status');
        item.setAttribute('aria-live', type === 'error' ? 'assertive' : 'polite');
        item.setAttribute('aria-atomic', 'true');
        toastHost.appendChild(item);
        setTimeout(function () {
            item.classList.add('leaving');
            setTimeout(function () {
                if (item.parentNode) item.parentNode.removeChild(item);
            }, 200);
        }, 3400);
    }

    function confirmModal(title, message) {
        if (activeConfirm) {
            activeConfirm.cancel.focus();
            return Promise.resolve(false);
        }

        return new Promise(function (resolve) {
            var trigger = document.activeElement;
            var triggerWasDisabled = !!(trigger && trigger.disabled);
            var pageWasInert = !!(page && page.inert);
            var sequence = ++confirmModalSequence;
            var veil = el('div', 'ac-modal-veil');
            var modal = el('div', 'ac-modal');
            var heading = el('h3', null, title);
            var copy = el('p', null, message);
            var actions = el('div', 'ac-row');
            var cancel = el('button', 'ac-btn ac-btn-ghost', 'Annulla');
            var confirm = el('button', 'ac-btn ac-btn-primary', 'Conferma');
            var headingId = 'acConfirmTitle' + sequence;
            var copyId = 'acConfirmCopy' + sequence;
            var settled = false;

            heading.id = headingId;
            copy.id = copyId;
            modal.setAttribute('role', 'dialog');
            modal.setAttribute('aria-modal', 'true');
            modal.setAttribute('aria-labelledby', headingId);
            modal.setAttribute('aria-describedby', copyId);
            modal.tabIndex = -1;
            cancel.type = 'button';
            confirm.type = 'button';
            actions.appendChild(cancel);
            actions.appendChild(confirm);
            modal.appendChild(heading);
            modal.appendChild(copy);
            modal.appendChild(actions);
            veil.appendChild(modal);

            function finish(value) {
                if (settled) return;
                settled = true;
                veil.removeEventListener('keydown', onKeyDown);
                veil.remove();
                if (page && 'inert' in page) page.inert = pageWasInert;
                if (trigger && trigger.tagName === 'BUTTON' && !triggerWasDisabled) trigger.disabled = false;
                activeConfirm = null;
                if (trigger && typeof trigger.focus === 'function' && document.body.contains(trigger)) trigger.focus();
                resolve(value);
            }

            function onKeyDown(event) {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    finish(false);
                    return;
                }
                if (event.key !== 'Tab') return;
                var first = cancel;
                var last = confirm;
                if (event.shiftKey && document.activeElement === first) {
                    event.preventDefault();
                    last.focus();
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault();
                    first.focus();
                }
            }

            cancel.addEventListener('click', function () { finish(false); });
            confirm.addEventListener('click', function () { finish(true); });
            veil.addEventListener('click', function (event) {
                if (event.target === veil) finish(false);
            });
            veil.addEventListener('keydown', onKeyDown);

            if (trigger && trigger.tagName === 'BUTTON' && !triggerWasDisabled) trigger.disabled = true;
            if (page && 'inert' in page) page.inert = true;
            document.body.appendChild(veil);
            activeConfirm = { cancel: cancel };
            cancel.focus();
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

    var AUDIT_PAGE_SIZE = 16;
    var QUALITY_ITEM_PAGE_SIZE = 20;
    var TITLE_ANALYZE_LIMIT = 10;
    var titleAuditOperationSequence = 0;
    var titleAuditView = {
        report: null,
        query: '',
        filter: 'problems',
        visibleLimit: AUDIT_PAGE_SIZE,
        selected: Object.create(null),
        open: Object.create(null),
        busy: false,
        operationId: 0
    };
    var qualityAuditView = {
        report: null,
        query: '',
        filter: 'all',
        visibleLimit: AUDIT_PAGE_SIZE,
        selected: Object.create(null),
        open: Object.create(null),
        shownItems: Object.create(null),
        queued: false,
        busy: false
    };

    function makeAuditSelect(id, label, options) {
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
        return wrap;
    }

    function makeLiveState(id, cls) {
        var state = el('span', cls || 'ac-state');
        state.id = id;
        state.setAttribute('role', 'status');
        state.setAttribute('aria-live', 'polite');
        return state;
    }

    function beginTitleAuditOperation() {
        if (titleAuditView.busy) {
            toast('Attendi il completamento dell’operazione titoli già in corso.', 'error');
            return 0;
        }
        var operationId = ++titleAuditOperationSequence;
        titleAuditView.operationId = operationId;
        titleAuditView.busy = true;
        return operationId;
    }

    function finishTitleAuditOperation(operationId) {
        if (titleAuditView.operationId !== operationId) return false;
        titleAuditView.operationId = 0;
        titleAuditView.busy = false;
        updateTitleAuditControls();
        return true;
    }

    function resetTitleAuditView(report) {
        titleAuditView.report = report;
        titleAuditView.query = '';
        titleAuditView.filter = 'problems';
        titleAuditView.visibleLimit = AUDIT_PAGE_SIZE;
        titleAuditView.selected = Object.create(null);
        titleAuditView.open = Object.create(null);
        if (val('acAuditSearch')) val('acAuditSearch').value = '';
        if (val('acAuditFilter')) val('acAuditFilter').value = 'problems';
    }

    function resetQualityAuditView(report) {
        qualityAuditView.report = report;
        qualityAuditView.query = '';
        qualityAuditView.filter = 'all';
        qualityAuditView.visibleLimit = AUDIT_PAGE_SIZE;
        qualityAuditView.selected = Object.create(null);
        qualityAuditView.open = Object.create(null);
        qualityAuditView.shownItems = Object.create(null);
        qualityAuditView.queued = false;
        qualityAuditView.busy = false;
        if (val('acQualitySearch')) val('acQualitySearch').value = '';
        if (val('acQualityFilter')) val('acQualityFilter').value = 'all';
    }

    function normalizedSearch(value) {
        return String(value == null ? '' : value).trim().toLowerCase();
    }

    function auditSeriesReasons(item) {
        var reasons = [valueOf(item, 'reason')];
        asArray(valueOf(item, 'seasons')).forEach(function (season) {
            reasons.push(valueOf(season, 'reason'));
        });
        return reasons.filter(Boolean);
    }

    function auditSeriesMatches(item) {
        var missing = valueOf(item, 'missingTitleCount') || 0;
        var animeClickId = valueOf(item, 'animeClickId');
        var reasons = auditSeriesReasons(item);
        var filter = titleAuditView.filter;
        if (filter === 'problems' && !missing) return false;
        if (filter === 'analyzable' && (!missing || !animeClickId)) return false;
        if (filter === 'unidentified' && animeClickId && reasons.indexOf('NotIdentified') < 0) return false;
        if (filter === 'complete' && missing) return false;

        var query = normalizedSearch(titleAuditView.query);
        if (!query) return true;
        var searchable = [
            valueOf(item, 'name'),
            valueOf(item, 'year'),
            animeClickId,
            reasons.map(auditShort).join(' ')
        ];
        asArray(valueOf(item, 'seasons')).forEach(function (season) {
            searchable.push(valueOf(season, 'animeClickId'));
        });
        return normalizedSearch(searchable.join(' ')).indexOf(query) >= 0;
    }

    function filteredTitleSeries() {
        return asArray(valueOf(titleAuditView.report, 'series')).filter(auditSeriesMatches);
    }

    function visibleUnselectedTitleSeries() {
        return filteredTitleSeries().slice(0, titleAuditView.visibleLimit).filter(function (item) {
            return titleSeriesIsSelectable(item) && !titleAuditView.selected[valueOf(item, 'id')];
        });
    }

    function titleSeriesIsSelectable(item) {
        return !!valueOf(item, 'id')
            && !!valueOf(item, 'animeClickId')
            && (valueOf(item, 'missingTitleCount') || 0) > 0;
    }

    function selectedTitleItems() {
        var selected = titleAuditView.selected;
        return asArray(valueOf(titleAuditView.report, 'series')).filter(function (item) {
            return !!selected[valueOf(item, 'id')] && titleSeriesIsSelectable(item);
        }).slice(0, TITLE_ANALYZE_LIMIT);
    }

    function updateTitleAuditControls() {
        var busy = titleAuditView.busy;
        var automatic = val('acBtnRunTitles');
        if (automatic && automatic.getAttribute('aria-busy') !== 'true') automatic.disabled = busy;
        var mainAudit = val('acBtnAudit');
        if (mainAudit && mainAudit.getAttribute('aria-busy') !== 'true') mainAudit.disabled = busy;
        page.querySelectorAll('.ac-audit-series-action, .ac-audit-entry .ac-checkbox').forEach(function (control) {
            if (control.getAttribute('aria-busy') !== 'true') control.disabled = busy;
        });

        if (!titleAuditView.report) return;
        var filtered = filteredTitleSeries();
        var shown = Math.min(filtered.length, titleAuditView.visibleLimit);
        var selected = selectedTitleItems().length;
        var state = val('acAuditVisibleState');
        if (state) {
            state.textContent = shown + ' serie mostrate su ' + filtered.length
                + (selected ? ' · ' + selected + ' selezionate' : '');
        }
        var analyze = val('acBtnAuditSelected');
        if (analyze && analyze.getAttribute('aria-busy') !== 'true') {
            analyze.disabled = busy || selected === 0;
            analyze.textContent = 'Analizza selezionate (' + selected + '/' + TITLE_ANALYZE_LIMIT + ')';
        }
        var clearSelection = val('acBtnAuditClearSelection');
        if (clearSelection) clearSelection.disabled = busy || selected === 0;
        var selectVisible = val('acBtnAuditSelectVisible');
        if (selectVisible) {
            var available = TITLE_ANALYZE_LIMIT - selected;
            var candidates = visibleUnselectedTitleSeries().length;
            selectVisible.disabled = busy || available <= 0 || candidates === 0;
            selectVisible.title = candidates
                ? Math.min(available, candidates) + ' serie visibili possono essere aggiunte alla selezione.'
                : 'Nessun’altra serie visibile può essere aggiunta alla selezione.';
        }
    }

    function replaceAuditSeries(fresh) {
        var items = asArray(valueOf(titleAuditView.report, 'series'));
        var id = valueOf(fresh, 'id');
        for (var i = 0; i < items.length; i++) {
            if (valueOf(items[i], 'id') === id) {
                items[i] = fresh;
                break;
            }
        }
        delete titleAuditView.selected[id];
    }

    function analyzeTitleSeries(items, button, askConfirmation) {
        items = (items || []).filter(function (item) {
            return askConfirmation
                ? titleSeriesIsSelectable(item)
                : !!valueOf(item, 'id') && !!valueOf(item, 'animeClickId');
        }).slice(0, TITLE_ANALYZE_LIMIT);
        if (!items.length) {
            toast('Seleziona almeno una serie identificata da analizzare.', 'error');
            return;
        }
        var prompt = askConfirmation
            ? confirmModal(
                'Analizza ' + items.length + ' serie',
                'Le schede verranno lette una alla volta per non sovraccaricare AnimeClick. '
                + 'Questa diagnosi non modifica i metadati della libreria.'
            )
            : Promise.resolve(true);
        prompt.then(function (confirmed) {
            if (!confirmed) return;
            var operationId = beginTitleAuditOperation();
            if (!operationId) return;
            var idleLabel = button.textContent;
            var state = val('acAuditState');
            var index = 0;
            var errors = [];
            setBusy(button, true, idleLabel, 'Analisi 0/' + items.length + '…');
            updateTitleAuditControls();

            function next() {
                if (index >= items.length) return Promise.resolve();
                var item = items[index];
                var current = index + 1;
                button.textContent = 'Analisi ' + current + '/' + items.length + '…';
                state.className = 'ac-state';
                state.textContent = 'Lettura di ' + (valueOf(item, 'name') || ('serie ' + current)) + '…';
                return request('POST', 'Plugins/AnimeClick/LibraryAuditSeries', { itemId: valueOf(item, 'id') })
                    .then(replaceAuditSeries)
                    .catch(function (error) {
                        errors.push((valueOf(item, 'name') || 'Serie') + ': ' + truncate(error.message, 140));
                    })
                    .then(function () { index += 1; return next(); });
            }

            next().then(function () {
                renderAudit();
                state.className = 'ac-state ' + (errors.length ? 'error' : 'success');
                state.textContent = (items.length - errors.length) + ' serie analizzate'
                    + (errors.length ? ' · ' + errors.length + ' non riuscite · ' + errors[0] : '') + '.';
                toast(
                    errors.length ? 'Analisi bulk completata con errori: ' + errors[0] : 'Analisi bulk completata',
                    errors.length ? 'error' : 'success'
                );
            }).finally(function () {
                if (titleAuditView.operationId !== operationId) return;
                setBusy(button, false, idleLabel, 'Analisi…');
                finishTitleAuditOperation(operationId);
            });
        });
    }

    function runAutomaticTitleRefresh(event, button) {
        event.preventDefault();
        event.stopImmediatePropagation();
        confirmModal(
            'Ricontrollo automatico dei titoli',
            'Accodare fino a 200 episodi con titolo mancante, segnaposto o non più aggiornato? '
            + 'Il lavoro prosegue in background e usa una finestra rotante nelle librerie più grandi. '
            + 'Se l’attività è già in corso, Jellyfin la riavvia dalla coda.'
        ).then(function (confirmed) {
            if (!confirmed) return;
            var operationId = beginTitleAuditOperation();
            if (!operationId) return;
            var state = val('acRunTitlesState');
            var idleLabel = 'Ricontrollo automatico (max 200)';
            setBusy(button, true, idleLabel, 'Accodamento…');
            updateTitleAuditControls();
            state.className = 'ac-state';
            state.textContent = 'Accodamento dell’attività globale…';
            request('POST', 'Plugins/AnimeClick/RunMissingTitlesTask').then(function (response) {
                state.className = 'ac-state success';
                state.textContent = (valueOf(response, 'message') || 'Ricontrollo accodato.')
                    + ' Il completamento dei refresh continua in background.';
                toast('Ricontrollo automatico avviato', 'success');
            }).catch(function (error) {
                state.className = 'ac-state error';
                state.textContent = truncate(error.message, 240);
            }).finally(function () {
                if (titleAuditView.operationId !== operationId) return;
                setBusy(button, false, idleLabel, 'Accodamento…');
                finishTitleAuditOperation(operationId);
            });
        });
    }

    function qualityItemMatchesStatus(item) {
        var filter = qualityAuditView.filter;
        var status = valueOf(item, 'status');
        if (filter === 'repairable') return !!valueOf(item, 'canRepair');
        if (filter === 'locked') return !!valueOf(item, 'locked');
        if (filter === 'waiting-translation' || filter === 'no-source') {
            return valueOf(item, 'repairState') === filter;
        }
        if (filter !== 'all') return status === filter;
        return true;
    }

    function qualityRepairStateLabel(state) {
        if (state === 'waiting-translation') return 'traduzione in corso';
        if (state === 'no-source') return 'senza fonte disponibile';
        if (state === 'blocked') return 'valore cambiato';
        if (state === 'error') return 'errore';
        if (state === 'applied') return 'riparato';
        return '';
    }

    function qualityItemSearchText(item) {
        var itemType = valueOf(item, 'itemType');
        var localizedType = itemType === 'Episode' ? 'episodio'
            : (itemType === 'Movie' ? 'film' : 'serie');
        return normalizedSearch([
            qualityItemHeading(item),
            valueOf(item, 'seriesName'),
            itemType,
            localizedType,
            qualityLabel(valueOf(item, 'status')),
            qualityRepairStateLabel(valueOf(item, 'repairState')),
            valueOf(item, 'locked') ? 'bloccato' : ''
        ].join(' '));
    }

    function filteredQualityGroups() {
        var query = normalizedSearch(qualityAuditView.query);
        return asArray(valueOf(qualityAuditView.report, 'series')).map(function (group) {
            var groupMatches = !query || normalizedSearch([
                valueOf(group, 'name'),
                valueOf(group, 'year')
            ].join(' ')).indexOf(query) >= 0;
            var items = asArray(valueOf(group, 'items')).filter(function (item) {
                return qualityItemMatchesStatus(item)
                    && (groupMatches || qualityItemSearchText(item).indexOf(query) >= 0);
            });
            return { group: group, items: items };
        }).filter(function (entry) { return entry.items.length > 0; });
    }

    function qualityGroupKey(group) {
        return valueOf(group, 'id') || ((valueOf(group, 'name') || 'group') + '-' + (valueOf(group, 'year') || ''));
    }

    function visibleQualityRepairItems() {
        var items = [];
        filteredQualityGroups().slice(0, qualityAuditView.visibleLimit).forEach(function (entry) {
            var key = qualityGroupKey(entry.group);
            if (!qualityAuditView.open[key]) return;
            var shown = qualityAuditView.shownItems[key] || QUALITY_ITEM_PAGE_SIZE;
            entry.items.slice(0, shown).forEach(function (item) {
                if (valueOf(item, 'canRepair')) items.push(item);
            });
        });
        return items;
    }

    function visibleUnselectedQualityRepairItems() {
        return visibleQualityRepairItems().filter(function (item) {
            return !qualityAuditView.selected[valueOf(item, 'id')];
        });
    }

    function selectedQualityIds() {
        return Object.keys(qualityAuditView.selected).filter(function (id) {
            return !!qualityAuditView.selected[id];
        });
    }

    function updateQualityAuditControls() {
        if (!qualityAuditView.report) return;
        var groups = filteredQualityGroups();
        var visible = groups.slice(0, qualityAuditView.visibleLimit);
        var visibleItems = visible.reduce(function (count, entry) { return count + entry.items.length; }, 0);
        var visibleRepairable = visibleUnselectedQualityRepairItems().length;
        var selected = selectedQualityIds().length;
        var maximum = valueOf(qualityAuditView.report, 'maximumRepairItems') || 100;
        var available = maximum - selected;
        var state = val('acQualityVisibleState');
        if (state) {
            state.textContent = visible.length + ' gruppi mostrati su ' + groups.length
                + ' · ' + visibleItems + ' elementi corrispondenti'
                + (selected ? ' · ' + selected + ' selezionati' : '');
        }
        var busy = qualityAuditView.busy;
        var selectedButton = val('acBtnQualityRepairSelected');
        if (selectedButton && selectedButton.getAttribute('aria-busy') !== 'true') {
            selectedButton.disabled = busy || qualityAuditView.queued || selected === 0;
            selectedButton.textContent = 'Ripara selezionati (' + selected + '/' + maximum + ')';
        }
        var clearSelection = val('acBtnQualityClearSelection');
        if (clearSelection) clearSelection.disabled = busy || qualityAuditView.queued || selected === 0;
        var selectVisible = val('acBtnQualitySelectVisible');
        if (selectVisible) {
            selectVisible.disabled = busy || qualityAuditView.queued || available <= 0 || visibleRepairable === 0;
            selectVisible.title = visibleRepairable
                ? Math.min(available, visibleRepairable) + ' elementi aperti e visibili possono essere aggiunti.'
                : 'Apri un gruppo oppure modifica la selezione per aggiungere altri elementi.';
        }
        var automatic = val('acBtnQualityRepair');
        if (automatic && automatic.getAttribute('aria-busy') !== 'true') {
            automatic.disabled = busy || qualityAuditView.queued || qualityRepairIds(qualityAuditView.report).length === 0;
        }
        var retry = val('acBtnQualityRetryNoSource');
        if (retry && retry.getAttribute('aria-busy') !== 'true') {
            retry.disabled = busy
                || qualityAuditView.queued
                || qualitySuppressedIds(qualityAuditView.report).length === 0;
        }
        var mainAudit = val('acBtnQualityAudit');
        if (mainAudit && mainAudit.getAttribute('aria-busy') !== 'true') mainAudit.disabled = busy;
        page.querySelectorAll('.ac-quality-item .ac-checkbox').forEach(function (checkbox) {
            checkbox.disabled = busy || qualityAuditView.queued;
        });
    }

    function qualityRepairIsBlocked() {
        if (!qualityAuditView.busy && !qualityAuditView.queued) return false;
        toast(
            qualityAuditView.busy
                ? 'Attendi il completamento dell’operazione qualità già in corso.'
                : 'Un lotto è già stato accodato. Attendi il completamento e analizza di nuovo.',
            'error'
        );
        return true;
    }

    function queueQualityRepair(itemIds, button, automatic, force) {
        if (qualityRepairIsBlocked()) return;
        if (!itemIds.length) {
            toast(
                force
                    ? 'Nessun elemento è stato escluso per mancanza di fonti.'
                    : 'Non ci sono metadati riparabili nella selezione.',
                'error'
            );
            return;
        }
        var maximum = valueOf(qualityAuditView.report, 'maximumRepairItems') || 100;
        confirmModal(
            force
                ? 'Riprova gli elementi senza fonte'
                : (automatic ? 'Ripara il prossimo lotto automatico' : 'Ripara gli elementi selezionati'),
            'Accodare il refresh non distruttivo di ' + itemIds.length + ' elementi? '
            + 'Il server ricontrolla lingua, lock e configurazione prima di accodarli. '
            + (force
                ? 'Questi elementi erano stati esclusi perché nessuna fonte aveva la sinossi: '
                    + 'la ricerca viene rifatta comunque. '
                : '')
            + 'Il limite per richiesta è ' + maximum + '.'
        ).then(function (confirmed) {
            if (!confirmed || qualityRepairIsBlocked()) return;
            var idleLabel = button.textContent;
            var state = val('acQualityState');
            var queuedAny = false;
            qualityAuditView.busy = true;
            setBusy(button, true, idleLabel, 'Accodamento…');
            updateQualityAuditControls();
            state.className = 'ac-state';
            state.textContent = 'Validazione e accodamento del lotto…';
            request('POST', 'Plugins/AnimeClick/LibraryQualityRepair', {
                itemIds: itemIds,
                force: !!force
            })
                .then(function (result) {
                    var considered = valueOf(result, 'consideredCount') || 0;
                    var queued = valueOf(result, 'queuedCount') || 0;
                    var skipped = valueOf(result, 'skippedCount') || 0;
                    var suppressed = valueOf(result, 'suppressedCount') || 0;
                    var truncated = !!valueOf(result, 'truncated');
                    queuedAny = queued > 0;
                    qualityAuditView.queued = queuedAny;
                    if (queuedAny) qualityAuditView.selected = Object.create(null);
                    state.className = 'ac-state ' + (queuedAny ? 'success' : 'error');
                    state.textContent = queued + ' refresh accodati su ' + considered + ' verificati'
                        + (skipped ? ' · ' + skipped + ' saltati' : '')
                        + (suppressed ? ' · ' + suppressed + ' già senza fonte' : '')
                        + (truncated ? ' · richiesta limitata a ' + maximum : '')
                        + '. Attendi il completamento, poi analizza di nuovo: '
                        + 'ogni esito viene registrato e mostrato sulla riga.';
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
                    qualityAuditView.busy = false;
                    setBusy(button, false, idleLabel, 'Accodamento…');
                    if (queuedAny) renderQualityAudit();
                    else updateQualityAuditControls();
                });
        });
    }

    function buildLibreriaPanel() {
        var panel = page.querySelector('#acPanelLibreria');
        clear(panel);

        var audit = makeCard(
            'Diagnosi',
            'Quali titoli vanno sistemati',
            'Legge soltanto le schede già in cache, quindi l’analisi non produce richieste ad AnimeClick. '
            + 'Il risultato resta compatto: cerca o filtra le serie, poi apri soltanto quelle che vuoi approfondire.'
        );
        var auditActions = el('div', 'ac-row ac-audit-primary-actions');
        var auditButton = el('button', 'ac-btn ac-btn-primary', 'Analizza la libreria');
        auditButton.type = 'button';
        auditButton.id = 'acBtnAudit';
        auditActions.appendChild(auditButton);
        auditActions.appendChild(makeLiveState('acAuditState'));
        audit.body.appendChild(auditActions);

        var auditAutomation = el('div', 'ac-audit-automation');
        var automaticTitles = el('button', 'ac-btn ac-btn-ghost', 'Ricontrollo automatico (max 200)');
        automaticTitles.type = 'button';
        automaticTitles.id = 'acBtnRunTitles';
        automaticTitles.title = 'Accoda l’attività globale settimanale per un massimo di 200 episodi per esecuzione.';
        auditAutomation.appendChild(automaticTitles);
        auditAutomation.appendChild(makeLiveState('acRunTitlesState'));
        audit.body.appendChild(auditAutomation);

        var summary = el('div', 'ac-priority-grid ac-audit-summary');
        summary.id = 'acAuditSummary';
        summary.style.display = 'none';
        audit.body.appendChild(summary);

        var totals = el('div', 'ac-row ac-audit-totals');
        totals.id = 'acAuditTotals';
        audit.body.appendChild(totals);

        var auditControls = el('div', 'ac-audit-controls');
        auditControls.id = 'acAuditControls';
        auditControls.style.display = 'none';
        var auditToolbar = el('div', 'ac-audit-toolbar');
        auditToolbar.appendChild(makeField(
            'acAuditSearch',
            'Cerca serie',
            'search',
            'Nome, anno, ID AnimeClick o causa.',
            { placeholder: 'Es. Grand Blue, 2026, non identificata', autocomplete: 'off' },
            false
        ));
        auditToolbar.appendChild(makeAuditSelect('acAuditFilter', 'Mostra', [
            { value: 'problems', label: 'Solo serie da sistemare' },
            { value: 'analyzable', label: 'Analizzabili in bulk' },
            { value: 'unidentified', label: 'Non identificate' },
            { value: 'complete', label: 'Serie complete' },
            { value: 'all', label: 'Tutte le serie' }
        ]));
        auditControls.appendChild(auditToolbar);

        var auditBulk = el('div', 'ac-audit-bulkbar');
        var selectAuditVisible = el('button', 'ac-btn ac-btn-ghost', 'Seleziona analizzabili visibili');
        selectAuditVisible.type = 'button';
        selectAuditVisible.id = 'acBtnAuditSelectVisible';
        auditBulk.appendChild(selectAuditVisible);
        var clearAuditSelection = el('button', 'ac-btn ac-btn-ghost', 'Azzera selezione');
        clearAuditSelection.type = 'button';
        clearAuditSelection.id = 'acBtnAuditClearSelection';
        clearAuditSelection.disabled = true;
        auditBulk.appendChild(clearAuditSelection);
        var analyzeSelected = el('button', 'ac-btn ac-btn-primary', 'Analizza selezionate (0/' + TITLE_ANALYZE_LIMIT + ')');
        analyzeSelected.type = 'button';
        analyzeSelected.id = 'acBtnAuditSelected';
        analyzeSelected.disabled = true;
        auditBulk.appendChild(analyzeSelected);
        auditControls.appendChild(auditBulk);
        auditControls.appendChild(makeLiveState('acAuditVisibleState', 'ac-state ac-audit-result-state'));
        audit.body.appendChild(auditControls);

        var list = el('div', 'ac-library-list ac-audit-list');
        list.id = 'acAuditList';
        audit.body.appendChild(list);
        panel.appendChild(audit.card);

        val('acAuditSearch').addEventListener('input', function () {
            titleAuditView.query = this.value;
            titleAuditView.visibleLimit = AUDIT_PAGE_SIZE;
            renderAudit();
        });
        val('acAuditFilter').addEventListener('change', function () {
            titleAuditView.filter = this.value;
            titleAuditView.visibleLimit = AUDIT_PAGE_SIZE;
            renderAudit();
        });
        selectAuditVisible.addEventListener('click', function () {
            var returnFocus = document.activeElement === selectAuditVisible;
            var candidates = visibleUnselectedTitleSeries();
            var available = TITLE_ANALYZE_LIMIT - selectedTitleItems().length;
            var added = 0;
            candidates.forEach(function (item) {
                var id = valueOf(item, 'id');
                if (available > 0) {
                    titleAuditView.selected[id] = true;
                    available -= 1;
                    added += 1;
                }
            });
            if (added < candidates.length) {
                toast('Selezione fermata al limite di ' + TITLE_ANALYZE_LIMIT + ' serie per analisi bulk.', 'success');
            }
            renderAudit();
            if (returnFocus && selectAuditVisible.disabled) {
                if (!analyzeSelected.disabled) analyzeSelected.focus();
                else val('acAuditSearch').focus();
            }
        });
        clearAuditSelection.addEventListener('click', function () {
            var returnFocus = document.activeElement === clearAuditSelection;
            titleAuditView.selected = Object.create(null);
            renderAudit();
            if (returnFocus) {
                if (!selectAuditVisible.disabled) selectAuditVisible.focus();
                else val('acAuditSearch').focus();
            }
        });
        analyzeSelected.addEventListener('click', function () {
            analyzeTitleSeries(selectedTitleItems(), this, true);
        });
        automaticTitles.addEventListener('click', function (event) {
            runAutomaticTitleRefresh(event, this);
        }, true);

        var quality = makeCard(
            'Sinossi e trame',
            'Qualità metadati',
            'Scansiona soltanto i metadati già presenti in Jellyfin. Filtri e sezioni comprimibili evitano '
            + 'elenchi infiniti; puoi scegliere gli elementi da riparare oppure lasciare al plugin il prossimo lotto sicuro.'
        );
        var qualityActions = el('div', 'ac-row ac-audit-primary-actions');
        var qualityAuditButton = el('button', 'ac-btn ac-btn-primary', 'Analizza la qualità');
        qualityAuditButton.type = 'button';
        qualityAuditButton.id = 'acBtnQualityAudit';
        qualityActions.appendChild(qualityAuditButton);
        qualityActions.appendChild(makeLiveState('acQualityState'));
        quality.body.appendChild(qualityActions);

        var qualitySummary = el('div', 'ac-priority-grid ac-audit-summary');
        qualitySummary.id = 'acQualitySummary';
        qualitySummary.style.display = 'none';
        quality.body.appendChild(qualitySummary);

        var qualityControls = el('div', 'ac-audit-controls');
        qualityControls.id = 'acQualityControls';
        qualityControls.style.display = 'none';
        var qualityToolbar = el('div', 'ac-audit-toolbar');
        qualityToolbar.appendChild(makeField(
            'acQualitySearch',
            'Cerca metadato',
            'search',
            'Serie, film, episodio o stato.',
            { placeholder: 'Es. 3D Kanojo, S1E5, inglese', autocomplete: 'off' },
            false
        ));
        qualityToolbar.appendChild(makeAuditSelect('acQualityFilter', 'Mostra', [
            { value: 'all', label: 'Tutte le anomalie' },
            { value: 'repairable', label: 'Solo riparabili' },
            { value: 'English', label: 'Inglese probabile' },
            { value: 'Missing', label: 'Sinossi mancante' },
            { value: 'Unknown', label: 'Lingua incerta' },
            { value: 'waiting-translation', label: 'Traduzione in corso' },
            { value: 'no-source', label: 'Senza fonte disponibile' },
            { value: 'locked', label: 'Elementi bloccati' }
        ]));
        qualityControls.appendChild(qualityToolbar);

        var qualityBulk = el('div', 'ac-audit-bulkbar');
        var selectQualityVisible = el('button', 'ac-btn ac-btn-ghost', 'Seleziona riparabili visibili');
        selectQualityVisible.type = 'button';
        selectQualityVisible.id = 'acBtnQualitySelectVisible';
        qualityBulk.appendChild(selectQualityVisible);
        var clearQualitySelection = el('button', 'ac-btn ac-btn-ghost', 'Azzera selezione');
        clearQualitySelection.type = 'button';
        clearQualitySelection.id = 'acBtnQualityClearSelection';
        clearQualitySelection.disabled = true;
        qualityBulk.appendChild(clearQualitySelection);
        var repairSelected = el('button', 'ac-btn ac-btn-primary', 'Ripara selezionati (0/100)');
        repairSelected.type = 'button';
        repairSelected.id = 'acBtnQualityRepairSelected';
        repairSelected.disabled = true;
        qualityBulk.appendChild(repairSelected);
        var qualityRepairButton = el('button', 'ac-btn ac-btn-ghost', 'Ripara prossimo lotto automatico');
        qualityRepairButton.type = 'button';
        qualityRepairButton.id = 'acBtnQualityRepair';
        qualityRepairButton.disabled = true;
        qualityBulk.appendChild(qualityRepairButton);
        var qualityRetryButton = el('button', 'ac-btn ac-btn-ghost', 'Riprova senza fonte');
        qualityRetryButton.type = 'button';
        qualityRetryButton.id = 'acBtnQualityRetryNoSource';
        qualityRetryButton.disabled = true;
        qualityBulk.appendChild(qualityRetryButton);
        qualityControls.appendChild(qualityBulk);
        qualityControls.appendChild(makeLiveState('acQualityVisibleState', 'ac-state ac-audit-result-state'));
        quality.body.appendChild(qualityControls);

        var qualityList = el('div', 'ac-library-list ac-audit-list');
        qualityList.id = 'acQualityList';
        quality.body.appendChild(qualityList);
        panel.appendChild(quality.card);

        val('acQualitySearch').addEventListener('input', function () {
            qualityAuditView.query = this.value;
            qualityAuditView.visibleLimit = AUDIT_PAGE_SIZE;
            renderQualityAudit();
        });
        val('acQualityFilter').addEventListener('change', function () {
            qualityAuditView.filter = this.value;
            qualityAuditView.visibleLimit = AUDIT_PAGE_SIZE;
            renderQualityAudit();
        });
        selectQualityVisible.addEventListener('click', function () {
            var returnFocus = document.activeElement === selectQualityVisible;
            var maximum = valueOf(qualityAuditView.report, 'maximumRepairItems') || 100;
            var available = maximum - selectedQualityIds().length;
            var candidates = visibleUnselectedQualityRepairItems();
            var added = 0;
            candidates.forEach(function (item) {
                var id = valueOf(item, 'id');
                if (available > 0 && id) {
                    qualityAuditView.selected[id] = true;
                    available -= 1;
                    added += 1;
                }
            });
            if (added < candidates.length) {
                toast('Selezione fermata al limite di ' + maximum + ' elementi per richiesta.', 'success');
            }
            renderQualityAudit();
            if (returnFocus && selectQualityVisible.disabled) {
                if (!repairSelected.disabled) repairSelected.focus();
                else val('acQualitySearch').focus();
            }
        });
        clearQualitySelection.addEventListener('click', function () {
            var returnFocus = document.activeElement === clearQualitySelection;
            qualityAuditView.selected = Object.create(null);
            renderQualityAudit();
            if (returnFocus) {
                if (!selectQualityVisible.disabled) selectQualityVisible.focus();
                else val('acQualitySearch').focus();
            }
        });
        repairSelected.addEventListener('click', function () {
            queueQualityRepair(selectedQualityIds(), this, false);
        });
        qualityRepairButton.addEventListener('click', function () {
            queueQualityRepair(qualityRepairIds(qualityAuditView.report), this, true);
        });
        qualityRetryButton.addEventListener('click', function () {
            queueQualityRepair(qualitySuppressedIds(qualityAuditView.report), this, true, true);
        });

        qualityAuditButton.addEventListener('click', function () {
            var button = this;
            var state = val('acQualityState');
            setBusy(button, true, 'Analizza la qualità', 'Analisi…');
            qualityAuditView.report = null;
            qualityAuditView.busy = true;
            qualityControls.style.display = 'none';
            clear(qualitySummary);
            qualitySummary.style.display = 'none';
            clear(qualityList);
            state.className = 'ac-state';
            state.textContent = 'Lettura locale di serie, film ed episodi identificati…';
            request('GET', 'Plugins/AnimeClick/LibraryQualityAudit').then(function (report) {
                renderQualityAudit(report);
                var repairable = valueOf(report, 'repairableCount') || 0;
                var waiting = valueOf(report, 'waitingTranslationCount') || 0;
                var withoutSource = valueOf(report, 'noSourceCount') || 0;
                state.className = 'ac-state success';
                state.textContent = (valueOf(report, 'itemCount') || 0) + ' elementi analizzati · '
                    + repairable + ' riparabili in sicurezza'
                    + (waiting ? ' · ' + waiting + ' in traduzione' : '')
                    + (withoutSource ? ' · ' + withoutSource + ' senza fonte disponibile' : '');
            }).catch(function (error) {
                state.className = 'ac-state error';
                state.textContent = truncate(error.message, 240);
                toast('Analisi qualità fallita', 'error');
            }).finally(function () {
                qualityAuditView.busy = false;
                setBusy(button, false, 'Analizza la qualità', 'Analisi…');
                if (qualityAuditView.report) updateQualityAuditControls();
            });
        });

        panel.appendChild(makeCallout(
            'Bulk sicuro, senza modifiche alla cieca',
            '«Analizza selezionate» legge al massimo 10 serie una alla volta e non modifica la libreria. '
            + '«Ricontrollo automatico» accoda fino a 200 episodi. Le riparazioni delle sinossi sono limitate a 100 '
            + 'elementi e il server ricontrolla sempre lingua, lock e configurazione prima del refresh.',
            'good'
        ));

        panel.appendChild(makeCallout(
            'Una stagione con titoli da sistemare',
            'Quando l’analisi segnala «Nessun abbinamento», apri la stagione in Jellyfin e scrivi l’ID AnimeClick '
            + 'di quel cour nel campo AnimeClick della stagione. L’ID di stagione ha la precedenza su quello della serie.',
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
        var id = valueOf(item, 'id');
        var selectable = titleSeriesIsSelectable(item);
        var wrapper = el('div', 'ac-audit-entry' + (selectable ? ' is-selectable' : ''));

        if (selectable) {
            var selector = el('label', 'ac-audit-selector');
            var checkbox = el('input', 'ac-checkbox');
            checkbox.type = 'checkbox';
            checkbox.checked = !!titleAuditView.selected[id];
            checkbox.disabled = titleAuditView.busy;
            checkbox.setAttribute('aria-label', 'Seleziona ' + (valueOf(item, 'name') || 'serie') + ' per l’analisi bulk');
            checkbox.addEventListener('change', function () {
                if (checkbox.checked && selectedTitleItems().length >= TITLE_ANALYZE_LIMIT) {
                    checkbox.checked = false;
                    toast('Puoi analizzare al massimo ' + TITLE_ANALYZE_LIMIT + ' serie per volta.', 'error');
                    return;
                }
                if (checkbox.checked) titleAuditView.selected[id] = true;
                else delete titleAuditView.selected[id];
                updateTitleAuditControls();
            });
            selector.appendChild(checkbox);
            selector.appendChild(el('span', 'ac-visually-hidden', 'Seleziona per analisi bulk'));
            wrapper.appendChild(selector);
        }

        var details = el('details', 'ac-details ac-audit-group');
        details.open = !!titleAuditView.open[id];
        var summary = el('summary', 'ac-audit-group-summary');
        var heading = el('div', 'ac-audit-summary-line');
        var title = el('div', 'ac-audit-summary-copy');
        var year = valueOf(item, 'year');
        title.appendChild(el('strong', null, (valueOf(item, 'name') || 'Senza nome') + (year ? ' (' + year + ')' : '')));
        var missing = valueOf(item, 'missingTitleCount') || 0;
        var total = valueOf(item, 'episodeCount') || 0;
        title.appendChild(el('span', 'ac-field-desc', missing
            ? missing + ' titoli da sistemare su ' + total
            : total + ' episodi completi'));
        heading.appendChild(title);
        var reason = valueOf(item, 'reason');
        heading.appendChild(el('span', 'ac-badge ' + auditTone(reason), auditShort(reason)));
        summary.appendChild(heading);
        details.appendChild(summary);

        var body = el('div', 'ac-details-body ac-audit-group-body');
        var animeClickId = valueOf(item, 'animeClickId');
        var counts = animeClickId ? 'Scheda ' + animeClickId : 'Nessuna scheda AnimeClick associata';
        var rows = valueOf(item, 'cardRowCount');
        if (rows) counts += ' · ' + rows + ' righe lette';
        body.appendChild(el('div', 'ac-field-desc', counts));
        body.appendChild(el('div', 'ac-note', valueOf(item, 'reasonLabel') || ''));

        var chips = auditSeasonChips(item);
        if (chips) body.appendChild(chips);

        var actions = el('div', 'ac-row ac-audit-item-actions');
        if (animeClickId) {
            var analyze = el('button', 'ac-btn ac-btn-sm ac-btn-ghost ac-audit-series-action', 'Analizza questa serie');
            analyze.type = 'button';
            analyze.title = 'Rilegge la scheda da AnimeClick e ricalcola la causa per questa serie.';
            analyze.addEventListener('click', function () {
                analyzeTitleSeries([item], analyze, false);
            });
            actions.appendChild(analyze);

            var purge = el('button', 'ac-btn ac-btn-sm ac-btn-ghost ac-audit-series-action', 'Svuota cache');
            purge.type = 'button';
            purge.title = 'Invalida le schede memorizzate per questa serie, così il prossimo refresh le rilegge.';
            purge.addEventListener('click', function () {
                var operationId = beginTitleAuditOperation();
                if (!operationId) return;
                setBusy(purge, true, 'Svuota cache', 'Svuotamento…');
                updateTitleAuditControls();
                request('POST', 'Plugins/AnimeClick/ClearCache', { animeClickId: animeClickId })
                    .then(function (response) {
                        toast('Cache svuotata · ' + (valueOf(response, 'removed') || 0) + ' elementi', 'success');
                    })
                    .catch(function (error) {
                        toast(truncate(error.message, 240), 'error');
                    })
                    .finally(function () {
                        if (titleAuditView.operationId !== operationId) return;
                        setBusy(purge, false, 'Svuota cache', 'Svuotamento…');
                        finishTitleAuditOperation(operationId);
                    });
            });
            actions.appendChild(purge);
        } else {
            var identify = el('button', 'ac-btn ac-btn-sm ac-btn-ghost ac-audit-series-action', 'Identifica in Strumenti');
            identify.type = 'button';
            identify.addEventListener('click', function () {
                val('acItemId').value = id;
                val('acAnimeClickId').value = '';
                activateTab(page.querySelector('#acTabStrumenti'), true);
                toast('ID elemento compilato: cerca l’ID AnimeClick e conferma.', 'success');
            });
            actions.appendChild(identify);
        }
        body.appendChild(actions);
        details.appendChild(body);
        details.addEventListener('toggle', function () {
            titleAuditView.open[id] = details.open;
        });
        wrapper.appendChild(details);
        return wrapper;
    }

    function renderAudit(report) {
        if (report && report !== titleAuditView.report) resetTitleAuditView(report);
        report = titleAuditView.report;
        if (!report) return;

        var summary = val('acAuditSummary');
        var totals = val('acAuditTotals');
        var controls = val('acAuditControls');
        var list = val('acAuditList');
        clear(summary);
        clear(totals);
        clear(list);

        var series = asArray(valueOf(report, 'series'));
        var episodes = valueOf(report, 'episodeCount') || 0;
        var missing = series.reduce(function (count, item) {
            return count + (valueOf(item, 'missingTitleCount') || 0);
        }, 0);
        var complete = series.filter(function (item) {
            return !(valueOf(item, 'missingTitleCount') || 0);
        });

        summary.style.display = '';
        summary.setAttribute('aria-label', 'Riepilogo analisi titoli');
        addPriorityTile(
            summary,
            'Serie',
            String(series.length),
            complete.length + ' complete · ' + (series.length - complete.length) + ' da verificare',
            series.length === complete.length ? 'good' : 'neutral'
        );
        addPriorityTile(
            summary,
            'Titoli da sistemare',
            String(missing),
            episodes ? 'su ' + episodes + ' episodi analizzati' : 'nessun episodio analizzato',
            missing ? 'warn' : 'good'
        );
        addPriorityTile(
            summary,
            'Analisi bulk',
            'Max ' + TITLE_ANALYZE_LIMIT,
            'serie identificate lette in sequenza per ogni operazione',
            'neutral'
        );

        var reasonTotals = Object.create(null);
        series.forEach(function (item) {
            var reason = valueOf(item, 'reason') || 'Unknown';
            if (reason === 'Ok') return;
            if (!reasonTotals[reason]) reasonTotals[reason] = { series: 0, episodes: 0 };
            reasonTotals[reason].series += 1;
            reasonTotals[reason].episodes += valueOf(item, 'missingTitleCount') || 0;
        });
        Object.keys(reasonTotals).forEach(function (reason) {
            var entry = reasonTotals[reason];
            var badge = el('span', 'ac-badge ' + auditTone(reason), auditShort(reason) + ' · ' + entry.series + ' serie');
            badge.title = entry.episodes + ' episodi';
            totals.appendChild(badge);
        });

        controls.style.display = '';
        if (!valueOf(report, 'episodeTitlesEnabled')) {
            list.appendChild(makeCallout(
                'Titoli episodio disattivati',
                'Nella scheda Metadati l’opzione dei titoli episodio è spenta: finché resta così nessun titolo viene scritto.',
                'warn'
            ));
        }

        if (!series.length) {
            list.appendChild(el('div', 'ac-empty', 'Nessuna serie usa AnimeClick come provider di metadati.'));
            updateTitleAuditControls();
            return;
        }

        var filtered = filteredTitleSeries();
        var visible = filtered.slice(0, titleAuditView.visibleLimit);
        if (!visible.length) {
            list.appendChild(el('div', 'ac-empty', titleAuditView.query
                ? 'Nessuna serie corrisponde alla ricerca e al filtro scelti.'
                : 'Nessuna serie rientra nel filtro scelto.'));
        } else {
            var fragment = document.createDocumentFragment();
            visible.forEach(function (item) {
                fragment.appendChild(renderAuditSeries(item));
            });
            list.appendChild(fragment);
        }

        if (visible.length < filtered.length) {
            var loadMore = el('button', 'ac-btn ac-btn-ghost ac-load-more', 'Mostra altre '
                + Math.min(AUDIT_PAGE_SIZE, filtered.length - visible.length) + ' serie');
            loadMore.type = 'button';
            loadMore.addEventListener('click', function () {
                var previous = visible.length;
                titleAuditView.visibleLimit += AUDIT_PAGE_SIZE;
                renderAudit();
                var summaries = list.querySelectorAll('.ac-audit-entry .ac-audit-group-summary');
                if (summaries[previous]) summaries[previous].focus();
            });
            list.appendChild(loadMore);
        }
        updateTitleAuditControls();
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

    // One item per group per pass, not the first N in report order. Taking them in order let a
    // single long series with no available source fill the whole batch: every automatic run spent
    // its 100 slots on the same hopeless episodes, so nothing else in the library ever improved.
    function collectQualityIds(report, predicate) {
        if (!report) return [];
        var maximum = valueOf(report, 'maximumRepairItems') || 100;
        var buckets = asArray(valueOf(report, 'series')).map(function (group) {
            return asArray(valueOf(group, 'items')).filter(function (item) {
                return predicate(item) && valueOf(item, 'id');
            });
        }).filter(function (items) { return items.length > 0; });

        var ids = [];
        var depth = 0;
        while (ids.length < maximum) {
            var progressed = false;
            for (var index = 0; index < buckets.length && ids.length < maximum; index++) {
                if (depth < buckets[index].length) {
                    ids.push(valueOf(buckets[index][depth], 'id'));
                    progressed = true;
                }
            }
            if (!progressed) break;
            depth += 1;
        }
        return ids;
    }

    function qualityRepairIds(report) {
        return collectQualityIds(report, function (item) { return !!valueOf(item, 'canRepair'); });
    }

    // Items a previous attempt proved to have no source. Offered separately, behind an explicit
    // retry, because sources do get filled in and a cached negative should not be permanent.
    function qualitySuppressedIds(report) {
        return collectQualityIds(report, function (item) {
            return !!valueOf(item, 'suppressed')
                && valueOf(item, 'repairState') === 'no-source';
        });
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
        var id = valueOf(item, 'id');
        var selectable = !!valueOf(item, 'canRepair') && !!id;
        var row = el('div', 'ac-library-type ac-quality-item' + (selectable ? ' is-selectable' : ''));
        if (selectable) {
            var selector = el('label', 'ac-quality-selector');
            var checkbox = el('input', 'ac-checkbox');
            checkbox.type = 'checkbox';
            checkbox.checked = !!qualityAuditView.selected[id];
            checkbox.disabled = qualityAuditView.busy || qualityAuditView.queued;
            checkbox.setAttribute('aria-label', 'Seleziona ' + qualityItemHeading(item) + ' per la riparazione');
            checkbox.addEventListener('change', function () {
                var maximum = valueOf(qualityAuditView.report, 'maximumRepairItems') || 100;
                if (checkbox.checked && selectedQualityIds().length >= maximum) {
                    checkbox.checked = false;
                    toast('Puoi riparare al massimo ' + maximum + ' elementi per richiesta.', 'error');
                    return;
                }
                if (checkbox.checked) qualityAuditView.selected[id] = true;
                else delete qualityAuditView.selected[id];
                updateQualityAuditControls();
            });
            selector.appendChild(checkbox);
            row.appendChild(selector);
        }

        var copy = el('div', 'ac-stack ac-grow ac-quality-copy');
        copy.appendChild(el('span', 'ac-library-type-name', qualityItemHeading(item)));
        var preview = valueOf(item, 'preview');
        if (preview) copy.appendChild(el('span', 'ac-field-desc', preview));
        row.appendChild(copy);

        var badges = el('div', 'ac-row ac-quality-badges');
        var status = valueOf(item, 'status');
        var statusBadge = el('span', 'ac-badge ' + qualityTone(status), qualityLabel(status));
        var confidence = Number(valueOf(item, 'confidence'));
        if ((status === 'English' || status === 'Unknown') && isFinite(confidence) && confidence > 0) {
            statusBadge.title = 'Confidenza classificatore: ' + Math.round(confidence * 100) + '%';
        }
        badges.appendChild(statusBadge);
        if (valueOf(item, 'locked')) {
            badges.appendChild(el('span', 'ac-badge warn', 'Bloccato'));
        } else if (!valueOf(item, 'languageRepairable') && (status === 'English' || status === 'Missing')) {
            var disabled = el('span', 'ac-badge neutral', 'Funzione disattivata');
            disabled.title = 'Abilita la trama o le sinossi episodio nella configurazione prima di riparare.';
            badges.appendChild(disabled);
        }

        // Why an item is still here after a repair. Without this the row looked identical before and
        // after a batch, and the only honest reading was "il pulsante non fa niente".
        var repairState = valueOf(item, 'repairState');
        var attempts = valueOf(item, 'attemptCount') || 0;
        var attemptSuffix = attempts ? ' Tentativi: ' + attempts + '.' : '';
        if (repairState === 'waiting-translation') {
            var waiting = el('span', 'ac-badge neutral', 'Traduzione in corso');
            waiting.title = 'La traduzione AI è già accodata: si applica da sola quando il modello risponde.'
                + attemptSuffix;
            badges.appendChild(waiting);
        } else if (repairState === 'no-source') {
            var noSource = el('span', 'ac-badge neutral', 'Nessuna fonte');
            noSource.title = 'AnimeClick, TheTVDB e TMDB non hanno questa sinossi, quindi non c’è niente da scrivere. '
                + 'Usa «Riprova senza fonte» per ricontrollare.' + attemptSuffix;
            badges.appendChild(noSource);
        } else if (repairState === 'blocked') {
            var blocked = el('span', 'ac-badge warn', 'Valore cambiato');
            blocked.title = 'Il testo è cambiato mentre la riparazione era in corso, quindi non è stato sovrascritto.'
                + attemptSuffix;
            badges.appendChild(blocked);
        } else if (repairState === 'error') {
            var failed = el('span', 'ac-badge danger', 'Errore');
            failed.title = 'L’ultimo tentativo è terminato con un errore; i dettagli sono nel log di Jellyfin.'
                + attemptSuffix;
            badges.appendChild(failed);
        }
        row.appendChild(badges);
        return row;
    }

    function renderQualityGroup(group, items) {
        items = items || asArray(valueOf(group, 'items'));
        var key = qualityGroupKey(group);
        var details = el('details', 'ac-details ac-audit-group ac-quality-group');
        details.open = !!qualityAuditView.open[key];
        var summary = el('summary', 'ac-audit-group-summary');
        var heading = el('div', 'ac-audit-summary-line');
        var title = el('div', 'ac-audit-summary-copy');
        var year = valueOf(group, 'year');
        title.appendChild(el('strong', null, (valueOf(group, 'name') || 'Senza nome') + (year ? ' (' + year + ')' : '')));
        title.appendChild(el('span', 'ac-field-desc', items.length + ' elementi corrispondenti su '
            + (valueOf(group, 'itemCount') || items.length)));
        heading.appendChild(title);

        var badges = el('div', 'ac-row ac-quality-badges');
        var english = items.filter(function (item) { return valueOf(item, 'status') === 'English'; }).length;
        var missing = items.filter(function (item) { return valueOf(item, 'status') === 'Missing'; }).length;
        var unknown = items.filter(function (item) { return valueOf(item, 'status') === 'Unknown'; }).length;
        var locked = items.filter(function (item) { return !!valueOf(item, 'locked'); }).length;
        var repairable = items.filter(function (item) { return !!valueOf(item, 'canRepair'); }).length;
        if (english) badges.appendChild(el('span', 'ac-badge warn', english + ' EN'));
        if (missing) badges.appendChild(el('span', 'ac-badge danger', missing + ' mancanti'));
        if (unknown) badges.appendChild(el('span', 'ac-badge neutral', unknown + ' incerti'));
        if (locked) badges.appendChild(el('span', 'ac-badge warn', locked + ' bloccati'));
        if (repairable) badges.appendChild(el('span', 'ac-badge success', repairable + ' riparabili'));
        heading.appendChild(badges);
        summary.appendChild(heading);
        details.appendChild(summary);

        var body = el('div', 'ac-details-body ac-audit-group-body');
        details.appendChild(body);
        var shown = qualityAuditView.shownItems[key] || QUALITY_ITEM_PAGE_SIZE;
        qualityAuditView.shownItems[key] = shown;
        var loaded = false;

        function renderItems() {
            loaded = true;
            clear(body);
            var fragment = document.createDocumentFragment();
            items.slice(0, shown).forEach(function (item) {
                fragment.appendChild(renderQualityItem(item));
            });
            body.appendChild(fragment);
            if (shown < items.length) {
                var loadMore = el('button', 'ac-btn ac-btn-ghost ac-load-more', 'Mostra altri '
                    + Math.min(QUALITY_ITEM_PAGE_SIZE, items.length - shown) + ' elementi');
                loadMore.type = 'button';
                loadMore.addEventListener('click', function () {
                    var previous = shown;
                    shown += QUALITY_ITEM_PAGE_SIZE;
                    qualityAuditView.shownItems[key] = shown;
                    renderItems();
                    var rows = body.querySelectorAll('.ac-quality-item');
                    var firstNew = rows[previous];
                    if (firstNew) {
                        var focusTarget = firstNew.querySelector('input:not([disabled]), button:not([disabled])');
                        if (!focusTarget) {
                            firstNew.tabIndex = -1;
                            focusTarget = firstNew;
                        }
                        focusTarget.focus();
                    }
                    updateQualityAuditControls();
                });
                body.appendChild(loadMore);
            }
        }

        details.addEventListener('toggle', function () {
            qualityAuditView.open[key] = details.open;
            if (details.open && !loaded) renderItems();
            updateQualityAuditControls();
        });
        if (details.open) renderItems();
        return details;
    }

    function renderQualityAudit(report) {
        if (report && report !== qualityAuditView.report) resetQualityAuditView(report);
        report = qualityAuditView.report;
        if (!report) return;

        var summary = val('acQualitySummary');
        var controls = val('acQualityControls');
        var list = val('acQualityList');
        var repairButton = val('acBtnQualityRepair');
        clear(summary);
        clear(list);
        summary.style.display = '';
        summary.setAttribute('aria-label', 'Riepilogo qualità metadati');
        controls.style.display = '';

        var itemCount = valueOf(report, 'itemCount') || 0;
        var italian = valueOf(report, 'italianCount') || 0;
        var english = valueOf(report, 'englishCount') || 0;
        var missing = valueOf(report, 'missingCount') || 0;
        var unknown = valueOf(report, 'unknownCount') || 0;
        var locked = valueOf(report, 'lockedCount') || 0;
        var repairable = valueOf(report, 'repairableCount') || 0;
        var waitingTranslation = valueOf(report, 'waitingTranslationCount') || 0;
        var noSource = valueOf(report, 'noSourceCount') || 0;
        addPriorityTile(summary, 'Italiano', String(italian), 'su ' + itemCount + ' elementi analizzati', 'good');
        addPriorityTile(summary, 'Inglese', String(english), 'candidato alla riparazione automatica', english ? 'warn' : 'good');
        addPriorityTile(summary, 'Mancante', String(missing), 'campo vuoto da completare', missing ? 'warn' : 'good');
        addPriorityTile(summary, 'Incerto', String(unknown), 'mai modificato automaticamente', 'neutral');
        addPriorityTile(summary, 'Bloccato', String(locked), 'protetto dai lock Jellyfin', locked ? 'warn' : 'good');
        addPriorityTile(
            summary,
            'Traduzione in corso',
            String(waitingTranslation),
            'si applicano da sole quando il modello risponde',
            'neutral'
        );
        addPriorityTile(
            summary,
            'Senza fonte',
            String(noSource),
            'nessuna sinossi su AnimeClick, TheTVDB o TMDB',
            'neutral'
        );

        var candidates = qualityRepairIds(report);
        var suppressedCandidates = qualitySuppressedIds(report);
        var retryButton = val('acBtnQualityRetryNoSource');
        if (retryButton && retryButton.getAttribute('aria-busy') !== 'true') {
            retryButton.disabled = qualityAuditView.busy
                || qualityAuditView.queued
                || suppressedCandidates.length === 0;
            retryButton.textContent = suppressedCandidates.length
                ? 'Riprova senza fonte (' + suppressedCandidates.length + ')'
                : 'Riprova senza fonte';
            retryButton.title = suppressedCandidates.length
                ? 'Ricontrolla gli elementi per cui nessuna fonte aveva la sinossi. Utile dopo aver aggiunto '
                    + 'una chiave TMDB/TheTVDB o configurato la traduzione AI.'
                : 'Nessun elemento è stato escluso per mancanza di fonti.';
        }

        if (repairButton.getAttribute('aria-busy') !== 'true') {
            repairButton.disabled = qualityAuditView.busy || qualityAuditView.queued || candidates.length === 0;
            repairButton.textContent = candidates.length
                ? 'Ripara lotto automatico (' + candidates.length + ')'
                : 'Niente da riparare';
            repairButton.title = repairable > candidates.length
                ? repairable + ' elementi riparabili; il server ne accetta al massimo '
                    + (valueOf(report, 'maximumRepairItems') || candidates.length) + ' per lotto, '
                    + 'distribuiti fra serie diverse.'
                : repairable + ' elementi riparabili.';
        }

        if (!itemCount) {
            list.appendChild(el('div', 'ac-empty', 'Nessuna serie o film identificato con AnimeClick.'));
            updateQualityAuditControls();
            return;
        }

        var allGroups = asArray(valueOf(report, 'series'));
        if (!allGroups.length) {
            list.appendChild(el('div', 'ac-empty', 'Tutti i metadati analizzati risultano in italiano.'));
            updateQualityAuditControls();
            return;
        }

        if (!repairable && qualityAuditView.filter === 'all' && !qualityAuditView.query) {
            list.appendChild(makeCallout(
                'Nessuna riparazione automatica sicura',
                noSource || waitingTranslation
                    ? 'Restano ' + noSource + ' elementi per cui nessuna fonte ha la sinossi e '
                        + waitingTranslation + ' in attesa della traduzione AI. I primi tornano '
                        + 'disponibili da soli dopo qualche giorno, oppure subito con «Riprova senza fonte»; '
                        + 'i secondi si applicano appena il modello risponde.'
                    : 'I casi rimasti sono incerti, bloccati oppure protetti da una funzione disattivata. '
                        + 'L’audit li mostra, ma non li accoda.',
                'warn'
            ));
        }

        var filtered = filteredQualityGroups();
        var visible = filtered.slice(0, qualityAuditView.visibleLimit);
        if (!visible.length) {
            list.appendChild(el('div', 'ac-empty', qualityAuditView.query
                ? 'Nessun metadato corrisponde alla ricerca e al filtro scelti.'
                : 'Nessun metadato rientra nel filtro scelto.'));
        } else {
            var fragment = document.createDocumentFragment();
            visible.forEach(function (entry) {
                fragment.appendChild(renderQualityGroup(entry.group, entry.items));
            });
            list.appendChild(fragment);
        }

        if (visible.length < filtered.length) {
            var loadMore = el('button', 'ac-btn ac-btn-ghost ac-load-more', 'Mostra altri '
                + Math.min(AUDIT_PAGE_SIZE, filtered.length - visible.length) + ' gruppi');
            loadMore.type = 'button';
            loadMore.addEventListener('click', function () {
                var previous = visible.length;
                qualityAuditView.visibleLimit += AUDIT_PAGE_SIZE;
                renderQualityAudit();
                var summaries = list.querySelectorAll('.ac-quality-group > .ac-audit-group-summary');
                if (summaries[previous]) summaries[previous].focus();
            });
            list.appendChild(loadMore);
        }
        updateQualityAuditControls();
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
            var operationId = beginTitleAuditOperation();
            if (!operationId) return;
            var button = this;
            var state = page.querySelector('#acAuditState');
            titleAuditView.report = null;
            val('acAuditControls').style.display = 'none';
            val('acAuditSummary').style.display = 'none';
            clear(val('acAuditSummary'));
            clear(val('acAuditTotals'));
            clear(val('acAuditList'));
            setBusy(button, true, 'Analizza la libreria', 'Analisi…');
            updateTitleAuditControls();
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
                if (titleAuditView.operationId !== operationId) return;
                setBusy(button, false, 'Analizza la libreria', 'Analisi…');
                finishTitleAuditOperation(operationId);
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
