/* AnimeClick per Jellyfin — impostazioni: configurazione, Preferenze, Fonti, Strumenti, Comunità. */
(function () {
    'use strict';

    var AC = window.AnimeClickUI;
    var h = AC.dom.h, icon = AC.dom.icon, clear = AC.dom.clear, replace = AC.dom.replace;
    var val = AC.util.val, list = AC.util.list, truncate = AC.util.truncate;
    var ui = AC.ui, api = AC.api;

    var page = null;
    var saved = null;
    var loaded = false;
    var dirty = false;
    var saving = false;
    var aiProviders = [];
    var identify = { item: null };

    /* ===== configuration map ===== */

    var BOOLEANS = {
        EnableIntegratedMetadata: true, PreferItalianTitle: true, EnablePlot: true,
        EnableEpisodeTitles: true, EnableEpisodeTitleFallback: true, EnableEpisodeSynopsisTranslation: true,
        EnableGenres: true, EnableTags: true, EnableProductionLocations: true, EnableTrailers: true,
        EnableCast: true, EnableThemeSongs: true, EnableIntegratedImages: true, EnableFanartImages: true,
        EnableAnimeClickImages: true, OverwriteNonItalianFields: false, EnableStudios: true,
        EnableCommunityRating: true, EnableCollections: false, EnableTvdbSynopsis: false,
        EnableAiTranslation: true, EnableCommunityMappings: false, EnableAniListMetadata: false
    };
    var NUMBERS = {
        MinPosterWidth: 400, EpisodeTranslationTimeoutSec: 90, TranslationCacheHours: 87600,
        MaxSearchResults: 10, CacheHours: 48, NegativeCacheHours: 12, RequestDelayMilliseconds: 1000
    };
    var TEXTS = {
        FanartPersonalApiKey: '', FanartProjectApiKey: '', TmdbApiKey: '', TvdbApiKey: '', AiModel: '',
        EpisodeLayoutOverrides: '', UserAgent: '', BaseUrl: 'https://www.animeclick.it', CommunitySharingMode: 'Ask'
    };

    function el(id) {
        return page ? page.querySelector('#' + id) : null;
    }

    function bound() {
        return Array.prototype.slice.call(page.querySelectorAll('[data-key]'));
    }

    function writeControl(control, config) {
        var key = control.dataset.key;
        var value = config[key];
        if (key in BOOLEANS) control.checked = value == null ? BOOLEANS[key] : !!value;
        else if (key in NUMBERS) control.value = value == null || value === '' ? NUMBERS[key] : value;
        else if (key in TEXTS) control.value = value == null || value === '' ? TEXTS[key] : value;
    }

    function readControl(control, config) {
        var key = control.dataset.key;
        if (key in BOOLEANS) config[key] = control.checked;
        else if (key in NUMBERS) {
            var parsed = parseInt(control.value, 10);
            config[key] = isNaN(parsed) ? NUMBERS[key] : parsed;
        } else if (key === 'BaseUrl') config[key] = control.value.trim() || TEXTS.BaseUrl;
        else if (key in TEXTS) config[key] = control.value.trim();
    }

    function loadForm(config) {
        saved = config;
        verified = {};
        bound().forEach(function (control) {
            writeControl(control, config);
            ui.fieldError(control, '');
        });

        var aiKey = el('acAiApiKey');
        aiKey.value = '';
        aiKey.placeholder = config.AiApiKey ? 'Chiave salvata: lascia vuoto per mantenerla' : 'Inserisci la chiave del servizio';
        el('acClearAiKey').checked = false;
        el('acAiEndpoint').value = config.AiEndpoint || '';
        ensureProviderOption(config.AiProvider);
        el('acAiProvider').value = config.AiProvider || '';
        updateProviderHint();

        var token = el('acCommunityGitHubToken');
        token.value = '';
        token.placeholder = config.CommunityGitHubToken ? 'Token salvato: lascia vuoto per mantenerlo' : 'Token personale GitHub';
        el('acClearCommunityToken').checked = false;

        refreshDerived();
        markClean();
        AC.bus.emit('config', config);
    }

    function readForm(config) {
        bound().forEach(function (control) { readControl(control, config); });

        if (el('acClearCommunityToken').checked) config.CommunityGitHubToken = '';
        var token = el('acCommunityGitHubToken').value.trim();
        if (token) config.CommunityGitHubToken = token;

        config.AiProvider = el('acAiProvider').value;
        var enteredKey = el('acAiApiKey').value.trim();
        var enteredEndpoint = el('acAiEndpoint').value.trim();
        if (enteredEndpoint) {
            var normalized = normalizeAiEndpoint(enteredEndpoint);
            if (normalized == null) throw new Error(AI_ENDPOINT_RULE);
            var fresh = normalizeAiEndpoint(config.AiEndpoint);
            if (!enteredKey && config.AiApiKey && !el('acClearAiKey').checked) {
                if (aiEndpointIsLocal()) {
                    config.AiApiKey = '';
                    config.OllamaCloudApiKey = '';
                } else if (fresh != null && normalized !== fresh) {
                    // Another administrator may have saved a new profile after this page loaded: never
                    // pair their freshly saved key with the destination shown here.
                    throw new Error('Il profilo AI è cambiato sul server: ricarica la pagina o reinserisci la chiave.');
                }
            }
            config.AiEndpoint = normalized;
        } else {
            config.AiEndpoint = '';
        }

        if (enteredKey) {
            config.AiApiKey = enteredKey;
        } else if (el('acClearAiKey').checked) {
            config.AiApiKey = '';
            config.OllamaCloudApiKey = '';
        }
        return config;
    }

    /* Mirrors the server rule: TLS for anything public, plain HTTP only towards the user's network. */
    function isPrivateHost(host) {
        if (host === 'localhost' || host === '::1' || host === '[::1]') return true;
        var ipv6 = host.replace(/^\[|\]$/g, '');
        if (ipv6.indexOf(':') !== -1) return /^(?:f[cd][0-9a-f]{2}|fe[89ab][0-9a-f]):/i.test(ipv6);
        var octets = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
        if (octets) {
            var a = parseInt(octets[1], 10), b = parseInt(octets[2], 10);
            return a === 10 || a === 127 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
        }
        return host.indexOf('.') === -1 || /\.(local|lan|internal)$/i.test(host) || /\.home\.arpa$/i.test(host);
    }

    function normalizeAiEndpoint(value) {
        try {
            var url = new URL(String(value || '').trim());
            var allowed = url.protocol === 'https:' || (url.protocol === 'http:' && isPrivateHost(url.hostname));
            if (!allowed || url.username || url.password || url.search || url.hash) return null;
            return url.href;
        } catch (error) {
            return null;
        }
    }

    function aiEndpointChanged() {
        var current = normalizeAiEndpoint(el('acAiEndpoint').value);
        var stored = normalizeAiEndpoint(saved && saved.AiEndpoint);
        return current == null || stored == null || current !== stored;
    }

    function aiEndpointIsLocal() {
        try { return new URL(el('acAiEndpoint').value.trim()).protocol === 'http:'; } catch (error) { return false; }
    }

    var AI_ENDPOINT_RULE = 'L’indirizzo del servizio AI deve essere HTTPS (o HTTP verso la tua rete) e non può contenere credenziali, query o frammenti.';

    /** Opens the section and the panel that hold a field, shows the problem under it and focuses it. */
    function pointAt(input, message) {
        var view = input.closest('[data-view]');
        if (view) AC.app.go(view.dataset.view);
        for (var parent = input.parentElement; parent && parent !== view; parent = parent.parentElement) {
            if (parent.tagName === 'DETAILS') parent.open = true;
        }
        ui.fieldError(input, message);
        input.focus();
        return message;
    }

    /** The first problem that prevents saving, already shown next to its field; null when there is none. */
    function validate() {
        var numbers = bound().filter(function (input) { return input.type === 'number'; });
        numbers.concat([el('acAiEndpoint'), el('acAiApiKey')]).forEach(function (input) { ui.fieldError(input, ''); });

        var invalid = numbers.find(function (input) { return !input.value.trim() || !input.checkValidity(); });
        if (invalid) {
            var label = page.querySelector('label[for="' + invalid.id + '"]');
            pointAt(invalid, 'Inserisci un numero intero fra ' + invalid.min + ' e ' + invalid.max + '.');
            return 'Controlla «' + (label ? label.textContent : invalid.id) + '»: il valore deve essere compreso tra '
                + invalid.min + ' e ' + invalid.max + '.';
        }

        var endpoint = el('acAiEndpoint').value.trim();
        if (endpoint && normalizeAiEndpoint(endpoint) == null) return pointAt(el('acAiEndpoint'), AI_ENDPOINT_RULE);
        if (aiEndpointIsLocal()) return null;
        if (aiEndpointChanged() && saved && saved.AiApiKey && !el('acClearAiKey').checked && !el('acAiApiKey').value.trim()) {
            return pointAt(el('acAiApiKey'), 'Dopo aver cambiato servizio, inserisci la nuova chiave oppure seleziona «Rimuovi la chiave salvata».');
        }
        return null;
    }

    /* ===== saving ===== */

    function markDirty() {
        if (!loaded || saving) return;
        dirty = true;
        var bar = el('acSaveBar');
        if (bar) bar.hidden = false;
        refreshDerived();
    }

    function markClean() {
        dirty = false;
        var bar = el('acSaveBar');
        if (bar) bar.hidden = true;
    }

    /**
     * Writes only the keys this page changed, on top of what the server holds now. A key that another
     * session changed in the meantime blocks the save instead of being silently overwritten.
     */
    function commit(edited) {
        var original = Object.assign({}, saved);
        var changed = Object.keys(edited).filter(function (key) { return edited[key] !== original[key]; });
        saving = true;
        var main = page.querySelector('.ac-main');
        if (main) main.inert = true;
        if (el('acBtnDiscard')) el('acBtnDiscard').disabled = true;
        return api.getConfig().then(function (current) {
            var conflict = changed.some(function (key) { return current[key] !== original[key] && current[key] !== edited[key]; });
            var aiChanged = changed.some(function (key) { return key.indexOf('Ai') === 0; });
            if (aiChanged) {
                conflict = conflict || ['AiEndpoint', 'AiProvider', 'AiApiKey'].some(function (key) { return current[key] !== original[key]; });
            }
            if (conflict) throw new Error('Le stesse impostazioni sono cambiate in un’altra sessione. Ricarica la pagina prima di salvare.');
            changed.forEach(function (key) { current[key] = edited[key]; });
            return api.putConfig(current);
        }).then(function () {
            return api.getConfig();
        }).then(function (config) {
            saving = false;
            loadForm(config);
            return config;
        }).finally(function () {
            saving = false;
            if (main) main.inert = false;
            if (el('acBtnDiscard')) el('acBtnDiscard').disabled = false;
        });
    }

    function save() {
        if (!loaded || saving) return Promise.reject(new Error('Attendi il caricamento delle impostazioni.'));
        var problem = validate();
        if (problem) {
            ui.toast(problem, 'error');
            var error = new Error(problem);
            error.reported = true;
            return Promise.reject(error);
        }
        var edited;
        try { edited = readForm(Object.assign({}, saved)); } catch (error) { return Promise.reject(error); }
        return commit(edited).then(function (config) {
            ui.toast('Impostazioni salvate', 'success');
            return config;
        });
    }

    var patches = Promise.resolve();

    /**
     * Used by the guided setup and the quick actions: same merge and conflict rules, on an explicit set
     * of keys. Patches run one after the other, each on top of what the previous one saved.
     */
    function savePatch(patch) {
        var turn = patches.then(function () {
            if (!loaded) throw new Error('Attendi il caricamento delle impostazioni.');
            // save() runs only on a dirty form, which stays dirty until it is written: refused here.
            if (dirty) throw new Error('Hai modifiche non salvate nelle impostazioni: salvale o annullale prima.');
            return commit(Object.assign({}, saved, patch));
        });
        patches = turn.catch(function () { /* the caller reports it; the next patch still runs */ });
        return turn;
    }

    /* ===== derived summaries ===== */

    function sourceState(config) {
        config = config || saved || {};
        var fanart = !!(config.FanartPersonalApiKey || config.FanartProjectApiKey);
        var ai = !!(config.EnableAiTranslation && config.AiModel && (config.AiEndpoint || config.AiProvider));
        return {
            tmdb: !!config.TmdbApiKey,
            aniList: !!config.EnableAniListMetadata,
            fanart: fanart,
            tvdb: !!(config.EnableTvdbSynopsis && config.TvdbApiKey),
            ai: ai
        };
    }

    function formSourceState() {
        return {
            tmdb: !!el('acTmdbApiKey').value.trim(),
            fanart: !!(el('acFanartPersonalApiKey').value.trim() || el('acFanartProjectApiKey').value.trim()),
            tvdb: el('acEnableTvdbSynopsis').checked && !!el('acTvdbApiKey').value.trim(),
            ai: el('acEnableAiTranslation').checked && !!el('acAiModel').value.trim()
                && (!!el('acAiApiKey').value.trim() || aiEndpointIsLocal()
                    || (!el('acClearAiKey').checked && !aiEndpointChanged() && !!(saved && saved.AiApiKey)))
        };
    }

    var verified = {};

    /*
     * Whether AnimeClick answers the plugin. Since November 2026 the site can answer every page with
     * Cloudflare's anti-bot verification: the server then suspends its requests and says so here.
     */
    var animeClickStatus = null;
    var retryTimeFormat = new Intl.DateTimeFormat('it-IT', { hour: '2-digit', minute: '2-digit' });

    function animeClickBlocked() { return !!(animeClickStatus && val(animeClickStatus, 'blocked')); }

    function loadAnimeClickStatus() {
        return api.request('GET', 'Plugins/AnimeClick/SourceStatus').then(function (status) {
            setAnimeClickStatus(status);
            return status;
        }).catch(function () { return animeClickStatus; });
    }

    function setAnimeClickStatus(status) {
        animeClickStatus = status || null;
        refreshDerived();
        AC.bus.emit('source-status', animeClickStatus);
    }

    /** One sentence for the card and the result line: what happens, and when the plugin tries again. */
    function describeAnimeClickStatus(status) {
        if (!status) return '';
        if (!val(status, 'blocked')) return val(status, 'message') || 'AnimeClick risponde normalmente.';
        var retryAt = val(status, 'retryAtUtc');
        var retry = retryAt && !isNaN(new Date(retryAt)) ? ' Prossimo tentativo automatico alle ' + retryTimeFormat.format(new Date(retryAt)) + '.' : '';
        return (val(status, 'message') || 'AnimeClick sta rifiutando le richieste automatiche.') + retry;
    }

    function renderAnimeClickNotice() {
        var host = el('acAnimeClickNotice');
        if (!host) return;
        if (!animeClickBlocked()) { clear(host); return; }
        var since = AC.util.relTime(val(animeClickStatus, 'blockedSinceUtc'));
        replace(host, ui.callout('AnimeClick sta bloccando le richieste' + (since ? ' (rilevato ' + since + ')' : ''),
            describeAnimeClickStatus(animeClickStatus) + ' Il plugin non aggira la verifica: per farlo tornare a leggere le schede, AnimeClick dovrebbe consentire il suo User-Agent.',
            'warn', 'alert'));
    }

    function refreshDerived() {
        if (!page || !el('acTmdbApiKey')) return;
        var state = formSourceState();
        ['tmdb', 'fanart', 'tvdb', 'ai'].forEach(function (source) {
            var badgeHost = el('acSourceState_' + source);
            if (!badgeHost) return;
            var result = verified[source];
            var node = result === 'ok' ? ui.badge('Connessa', 'ok', 'check')
                : result === 'bad' ? ui.badge('Errore', 'bad', 'alert')
                    : state[source] ? ui.badge('Da verificare', 'warn') : ui.badge('Non configurata');
            replace(badgeHost, node);
        });
        var animeClickState = el('acSourceState_animeclick');
        if (animeClickState) {
            replace(animeClickState, animeClickBlocked() ? ui.badge('Verifica Cloudflare', 'warn', 'alert')
                : animeClickStatus && val(animeClickStatus, 'reachable') ? ui.badge('Connessa', 'ok', 'check')
                    : verified.animeclick === 'bad' ? ui.badge('Errore', 'bad', 'alert')
                        : ui.badge('Da verificare', 'warn'));
        }
        renderAnimeClickNotice();
        var aniListState = el('acSourceState_anilist');
        if (aniListState) {
            replace(aniListState, el('acEnableAniListMetadata').checked ? ui.badge('Attiva', 'ok', 'check') : ui.badge('Spenta'));
        }
        renderChain(state);
    }

    function renderChain(state) {
        var host = el('acSourceChain');
        if (!host) return;
        var steps = [
            ['AnimeClick', 'italiano', true, true],
            ['TheTVDB', 'italiano', state.tvdb],
            ['TMDB', 'italiano', state.tmdb],
            ['TMDB + AI', 'inglese tradotto', state.tmdb && state.ai],
            ['TheTVDB + AI', 'inglese tradotto', state.tvdb && state.ai]
        ];
        replace(host, steps.map(function (step, index) {
            return [
                index ? icon('chevronRight', 'ac-chain-arrow') : null,
                h('span', { class: 'ac-chain-step' + (step[3] ? ' is-first' : step[2] ? ' is-on' : '') },
                    h('span', { class: 'ac-led' + (step[3] && animeClickBlocked() ? ' is-warn' : step[2] ? ' is-ok' : '') }), step[0], h('small', { class: 'ac-muted', text: step[1] }))
            ];
        }));
    }

    /* ===== AI services ===== */

    function loadAiProviders() {
        return api.request('GET', 'Plugins/AnimeClick/AiProviders').then(function (rows) {
            aiProviders = list(rows);
            var selectNode = el('acAiProvider');
            var chosen = selectNode.value || (saved && saved.AiProvider) || '';
            clear(selectNode);
            selectNode.appendChild(h('option', { value: '', text: 'Scegli un servizio…' }));
            aiProviders.forEach(function (provider) {
                selectNode.appendChild(h('option', { value: val(provider, 'id'), text: val(provider, 'displayName') }));
            });
            ensureProviderOption(chosen);
            selectNode.value = chosen;
            updateProviderHint();
        }).catch(function () {
            // Without the list the saved profile keeps working; only the menu is incomplete.
        });
    }

    function currentProvider() {
        var id = el('acAiProvider').value;
        return aiProviders.find(function (provider) { return val(provider, 'id') === id; }) || null;
    }

    function ensureProviderOption(id) {
        var selectNode = el('acAiProvider');
        if (!id || Array.prototype.some.call(selectNode.options, function (option) { return option.value === id; })) return;
        selectNode.appendChild(h('option', { value: id, text: id + ' (profilo salvato)' }));
    }

    function updateProviderHint() {
        var note = el('acAiProviderNote');
        var provider = currentProvider();
        if (!note) return;
        clear(note);
        if (!provider) return;
        note.appendChild(document.createTextNode(val(provider, 'note') || ''));
        var link = val(provider, 'credentialUrl');
        if (link && /^https:\/\//i.test(link)) {
            note.appendChild(document.createTextNode(' '));
            note.appendChild(h('a', { href: link, target: '_blank', rel: 'noopener noreferrer', text: val(provider, 'requiresApiKey') ? 'Crea la chiave' : 'Documentazione' }));
        }
        el('acAiApiKey').placeholder = val(provider, 'requiresApiKey')
            ? (saved && saved.AiApiKey && !aiEndpointChanged() ? 'Chiave salvata: lascia vuoto per mantenerla' : 'Inserisci la chiave del servizio')
            : 'Non serve per un servizio nella tua rete';
    }

    function providerPayload(source) {
        if (source === 'animeclick') return {};
        if (source === 'fanart') return { personalApiKey: el('acFanartPersonalApiKey').value.trim(), projectApiKey: el('acFanartProjectApiKey').value.trim() };
        if (source === 'tmdb') return { apiKey: el('acTmdbApiKey').value.trim() };
        if (source === 'tvdb') return { apiKey: el('acTvdbApiKey').value.trim() };
        var payload = {
            provider: el('acAiProvider').value,
            model: el('acAiModel').value.trim(),
            timeoutSec: parseInt(el('acEpisodeTranslationTimeoutSec').value, 10) || 90
        };
        var key = el('acAiApiKey').value.trim();
        var endpoint = el('acAiEndpoint').value.trim();
        if (endpoint && (aiEndpointChanged() || key)) payload.endpoint = normalizeAiEndpoint(endpoint) || endpoint;
        if (key) payload.apiKey = key;
        return payload;
    }

    var TEST_ROUTES = { animeclick: 'SourceStatus/Check', tmdb: 'TestTmdb', ai: 'TestAi', tvdb: 'TestTvdb', fanart: 'TestFanart' };
    var SOURCE_NAMES = { animeclick: 'AnimeClick', tmdb: 'TMDB', ai: 'Traduzione AI', tvdb: 'TheTVDB', fanart: 'Fanart' };

    /** The AnimeClick check answers with the availability state, not with a success flag. */
    function testSucceeded(source, result) {
        return source === 'animeclick' ? !!val(result, 'reachable') : !!val(result, 'success');
    }

    function describeTest(source, result, success) {
        if (source === 'animeclick') {
            if (success) return 'AnimeClick risponde.';
            return val(result, 'blocked') ? 'AnimeClick risponde ancora con la verifica di Cloudflare.' : truncate(val(result, 'message') || 'AnimeClick non ha risposto.', 300);
        }
        var code = val(result, 'statusCode');
        if (!success) return truncate(val(result, 'errorMessage') || ('Verifica non riuscita' + (code ? ' (HTTP ' + code + ')' : '') + '.'), 300);
        if (source === 'fanart') return 'Fanart risponde: ' + (val(result, 'imageCount') || 0) + ' immagini per il titolo di prova.';
        if (source === 'tmdb') return 'Chiave valida.' + (val(result, 'sampleName') ? ' Risultato di prova: ' + truncate(val(result, 'sampleName'), 80) + '.' : '');
        if (source === 'tvdb') return 'Accesso ed episodi verificati' + (val(result, 'effectiveLanguage') ? ' (lingua ' + val(result, 'effectiveLanguage') + ')' : '') + '.';
        var provider = currentProvider();
        return (provider ? val(provider, 'displayName') : 'Il servizio AI') + ' risponde con il modello ' + (val(result, 'model') || el('acAiModel').value.trim()) + '.';
    }

    function runTest(source, buttonNode, quiet) {
        var out = el('acTestResult_' + source);
        ui.setBusy(buttonNode, true, 'Verifica…');
        ui.status(out, 'Connessione in corso…');
        return api.request('POST', 'Plugins/AnimeClick/' + TEST_ROUTES[source], providerPayload(source)).then(function (result) {
            var success = testSucceeded(source, result);
            if (source === 'animeclick') setAnimeClickStatus(result);
            var message = describeTest(source, result, success);
            // A blocked AnimeClick is a state of the site, told in warn by the card: not an error of the check.
            var blocked = source === 'animeclick' && !!val(result, 'blocked');
            verified[source] = success ? 'ok' : blocked ? null : 'bad';
            ui.status(out, message, success ? 'ok' : blocked ? undefined : 'bad');
            if (!quiet) ui.toast(SOURCE_NAMES[source] + ': ' + message, success ? 'success' : blocked ? 'info' : 'error');
            return success;
        }).catch(function (error) {
            verified[source] = 'bad';
            ui.status(out, truncate(error.message, 300), 'bad');
            if (!quiet) ui.toast(SOURCE_NAMES[source] + ': ' + truncate(error.message, 200), 'error');
            return false;
        }).finally(function () {
            ui.setBusy(buttonNode, false);
            refreshDerived();
        });
    }

    function testButton(source, label) {
        var node = ui.button(label, { id: 'acTest_' + source, icon: 'check', small: true });
        node.addEventListener('click', function () { runTest(source, node); });
        return node;
    }

    /* ===== Preferenze ===== */

    function buildPreferences(view) {
        view.appendChild(ui.sectionHead('セッティング', 'Preferenze', 'Cosa importare nelle tue schede',
            'Le scelte valgono dal prossimo aggiornamento dei metadati. Le opzioni consigliate sono già attive: puoi tornare qui quando vuoi.'));

        view.appendChild(ui.callout('Le correzioni manuali vanno bloccate',
            'I campi qui sotto vengono riscritti a ogni aggiornamento. Se correggi a mano un titolo o una trama, usa il lucchetto di Jellyfin su quel campo: altrimenti al refresh successivo torna il valore di AnimeClick.',
            'warn', 'lock'));

        var essentials = ui.card({ icon: 'cat', title: 'Identità italiana', text: 'Il cuore del plugin: nome e trama in italiano da AnimeClick.' });
        essentials.body.appendChild(ui.switches([
            ui.switchRow('acPreferItalianTitle', 'Titolo italiano', 'Usa il titolo AnimeClick come nome principale.', { key: 'PreferItalianTitle' }),
            ui.switchRow('acEnablePlot', 'Trama italiana', 'Importa la sinossi AnimeClick quando c’è.', { key: 'EnablePlot' }),
            ui.switchRow('acEnableIntegratedMetadata', 'Completa dalle fonti integrate', 'Con una chiave TMDB identifica le opere e completa i campi mancanti: AnimeClick può restare l’unico provider delle librerie anime.', { key: 'EnableIntegratedMetadata' })
        ]));

        var episodes = ui.card({ icon: 'tv', title: 'Episodi', text: 'Titoli e trame di ogni puntata.' });
        episodes.body.appendChild(ui.switches([
            ui.switchRow('acEnableEpisodeTitles', 'Titoli degli episodi', 'Completa i nomi vuoti o generici e converte quelli riconoscibilmente inglesi. Conserva titoli italiani, incerti e bloccati.', { key: 'EnableEpisodeTitles' }),
            ui.switchRow('acEnableEpisodeTitleFallback', 'Cerca i titoli anche nelle altre fonti', 'Dopo AnimeClick prova TheTVDB e TMDB; se c’è solo l’inglese può tradurlo con il servizio AI abilitato.', { key: 'EnableEpisodeTitleFallback' }),
            ui.switchRow('acEnableEpisodeSynopsisTranslation', 'Trame degli episodi', 'AnimeClick per primo, poi le fonti aggiuntive che hai configurato.', { key: 'EnableEpisodeSynopsisTranslation' })
        ]));

        var enrichment = ui.card({ icon: 'sparkles', title: 'Arricchimento', text: 'Quanto dettaglio aggiungere alle schede.' });
        enrichment.body.appendChild(ui.switches([
            ui.switchRow('acEnableGenres', 'Generi', 'Generi in italiano.', { key: 'EnableGenres' }),
            ui.switchRow('acEnableTags', 'Tag e origine', 'Target, tag e opera di origine.', { key: 'EnableTags' }),
            ui.switchRow('acEnableCast', 'Cast e staff', 'Doppiatori e ruoli dello staff.', { key: 'EnableCast' }),
            ui.switchRow('acEnableTrailers', 'Trailer e PV', 'Solo video YouTube etichettati come tali.', { key: 'EnableTrailers' }),
            ui.switchRow('acEnableThemeSongs', 'Sigle', 'Opening ed ending nei tag.', { key: 'EnableThemeSongs' }),
            ui.switchRow('acEnableProductionLocations', 'Nazionalità', 'Come luogo di produzione.', { key: 'EnableProductionLocations' })
        ]));

        var images = ui.card({ icon: 'image', title: 'Immagini', text: 'Ordine: Fanart, TMDB alla risoluzione originale, copertina e banner di AniList, poi la locandina AnimeClick. Le immagini già salvate non vengono toccate.' });
        images.body.appendChild(ui.switches([
            ui.switchRow('acEnableIntegratedImages', 'Immagini TMDB ad alta risoluzione', 'Locandine, sfondi, loghi, stagioni, fotogrammi e ritratti. Serve la chiave TMDB.', { key: 'EnableIntegratedImages' }),
            ui.switchRow('acEnableFanartImages', 'Preferisci le immagini Fanart', 'Serve una chiave Fanart nella sezione Fonti.', { key: 'EnableFanartImages' }),
            ui.switchRow('acEnableAnimeClickImages', 'Locandina AnimeClick come riserva', 'Usata quando le fonti precedenti non hanno un poster.', { key: 'EnableAnimeClickImages' })
        ]));
        images.body.appendChild(ui.field('acMinPosterWidth', 'Larghezza minima della locandina (px)', {
            type: 'number', min: '0', max: '4000', step: '1', key: 'MinPosterWidth', inputmode: 'numeric',
            hint: 'Sotto questa soglia la locandina viene scartata. 0 disattiva il filtro; consigliato 400.'
        }));

        var invasive = ui.card({ icon: 'alert', title: 'Opzioni invasive', text: 'Non servono nel flusso consigliato. Attivale solo se sai perché.', cls: 'ac-card--danger' });
        invasive.body.appendChild(ui.switches([
            ui.switchRow('acOverwriteNonItalianFields', 'Sovrascrivi i campi non italiani', 'AnimeClick sostituisce anche titolo originale, studio, voto e data. Le sue date sono spesso solo l’anno e il voto ha tre decimali: TheTVDB e TMDB qui sono più precisi.', { key: 'OverwriteNonItalianFields' }),
            ui.switchRow('acEnableStudios', 'Studi di animazione AnimeClick', 'Con le fonti integrate o con la sovrascrittura attiva.', { key: 'EnableStudios' }),
            ui.switchRow('acEnableCommunityRating', 'Voto della community AnimeClick', 'Con le fonti integrate o con la sovrascrittura attiva.', { key: 'EnableCommunityRating' }),
            ui.switchRow('acEnableCollections', 'Collezioni automatiche', 'Raggruppa sequel, prequel e spin-off.', { key: 'EnableCollections' })
        ]));

        view.appendChild(h('div', { class: 'ac-cols-2' }, essentials.card, episodes.card, enrichment.card, images.card));
        view.appendChild(invasive.card);
    }

    /* ===== Fonti ===== */

    function sourceCard(options) {
        var c = ui.card({ id: 'acSource_' + options.id });
        var logo = h('span', { class: 'ac-source-logo' },
            options.logo ? h('img', { src: api.asset(options.logo), alt: options.name, width: 92, height: 30, decoding: 'async' }) : icon(options.icon || 'plug'));
        c.card.insertBefore(h('div', { class: 'ac-source-head' },
            h('div', { class: 'ac-source-top' }, logo, h('span', { id: 'acSourceState_' + options.id })),
            h('div', null, h('h2', { class: 'ac-h2', text: options.name }), h('p', { class: 'ac-hint', style: 'margin-top:4px', text: options.role }))), c.body);
        return c;
    }

    function buildSources(view) {
        var verifyAll = ui.button('Verifica tutte', { id: 'acBtnVerifyAll', icon: 'check', variant: 'ghost' });
        verifyAll.addEventListener('click', function () {
            var state = formSourceState();
            // AnimeClick needs no key: it is always one of the sources to check.
            var pending = ['animeclick'].concat(['tmdb', 'fanart', 'tvdb', 'ai'].filter(function (source) { return state[source]; }));
            ui.setBusy(verifyAll, true, 'Verifica…');
            pending.reduce(function (chain, source) {
                return chain.then(function () { return runTest(source, el('acTest_' + source), true); });
            }, Promise.resolve()).then(function () {
                var failed = pending.filter(function (source) { return verified[source] !== 'ok'; });
                ui.toast(failed.length ? 'Da controllare: ' + failed.map(function (s) { return SOURCE_NAMES[s]; }).join(', ') : 'Tutte le fonti rispondono.', failed.length ? 'error' : 'success');
            }).finally(function () { ui.setBusy(verifyAll, false); });
        });

        view.appendChild(ui.sectionHead('ソース', 'Fonti', 'Più fonti, più anime in italiano',
            'AnimeClick funziona da solo, senza chiavi. Le fonti aggiuntive completano quello che manca: prima i testi italiani, poi quelli inglesi tradotti.', verifyAll));

        var chainCard = ui.card({ icon: 'layers', title: 'L’ordine delle fonti per i testi', text: 'Per ogni campo vince la prima fonte che ha un testo valido.' });
        chainCard.body.appendChild(h('div', { id: 'acSourceChain', class: 'ac-chain' }));
        view.appendChild(chainCard.card);

        var animeclick = sourceCard({ id: 'animeclick', name: 'AnimeClick', icon: 'cat', role: 'La fonte principale, senza chiavi: titoli, trame, episodi, cast e relazioni in italiano.' });
        animeclick.body.appendChild(h('div', { id: 'acAnimeClickNotice' }));
        animeclick.body.appendChild(h('div', { class: 'ac-row' }, testButton('animeclick', 'Verifica AnimeClick'), ui.liveStatus('acTestResult_animeclick')));

        var tmdb = sourceCard({ id: 'tmdb', name: 'TMDB', logo: 'AnimeClickLogoTmdb', role: 'Identificazione delle opere, dati mancanti e immagini alla risoluzione originale.' });
        tmdb.body.appendChild(ui.field('acTmdbApiKey', 'Chiave API TMDB', {
            secret: true, key: 'TmdbApiKey', spellcheck: false,
            html: 'La trovi nelle <a href="https://developer.themoviedb.org/docs/getting-started" target="_blank" rel="noopener noreferrer">impostazioni API di TMDB</a>.'
        }));
        tmdb.body.appendChild(h('div', { class: 'ac-row' }, testButton('tmdb', 'Verifica TMDB'), ui.liveStatus('acTestResult_tmdb')));
        tmdb.body.appendChild(ui.hint('This product uses the TMDB API but is not endorsed or certified by TMDB.'));

        var fanart = sourceCard({ id: 'fanart', name: 'Fanart', logo: 'AnimeClickLogoFanart', role: 'Le immagini preferite: locandine, sfondi fino al 4K, loghi e banner.' });
        fanart.body.appendChild(ui.field('acFanartPersonalApiKey', 'Chiave personale Fanart', {
            secret: true, key: 'FanartPersonalApiKey', spellcheck: false,
            html: 'Creala in <a href="https://fanart.tv/get-an-api-key/" target="_blank" rel="noopener noreferrer">Personal API Keys su Fanart</a>: è quella consigliata.'
        }));
        fanart.body.appendChild(ui.field('acFanartProjectApiKey', 'Chiave di progetto Fanart (facoltativa)', {
            secret: true, key: 'FanartProjectApiKey', spellcheck: false, hint: 'Puoi usare la personale, quella di progetto o entrambe.'
        }));
        fanart.body.appendChild(h('div', { class: 'ac-row' }, testButton('fanart', 'Verifica Fanart'), ui.liveStatus('acTestResult_fanart')));
        fanart.body.appendChild(ui.hint('Artwork provided by Fanart.tv.'));

        var tvdb = sourceCard({ id: 'tvdb', name: 'TheTVDB', logo: 'AnimeClickLogoTvdb', role: 'Titoli, trame e generi: consultata prima di TMDB per i testi.' });
        tvdb.body.appendChild(ui.switches([ui.switchRow('acEnableTvdbSynopsis', 'Usa TheTVDB', 'Serve una chiave API del servizio.', { key: 'EnableTvdbSynopsis' })]));
        tvdb.body.appendChild(ui.field('acTvdbApiKey', 'Chiave API TheTVDB', {
            secret: true, key: 'TvdbApiKey', spellcheck: false,
            html: 'Dal <a href="https://thetvdb.com/dashboard" target="_blank" rel="noopener noreferrer">tuo account TheTVDB</a>.'
        }));
        tvdb.body.appendChild(h('div', { class: 'ac-row' }, testButton('tvdb', 'Verifica TheTVDB'), ui.liveStatus('acTestResult_tvdb')));
        tvdb.body.appendChild(ui.hint('Metadata provided by TheTVDB.'));

        var anilist = sourceCard({ id: 'anilist', name: 'AniList', icon: 'users', role: 'Senza chiave: cast con personaggi e doppiatori, studio, date, voto, trailer, copertina e banner; e l’anno di ogni stagione per collegare i sequel.' });
        anilist.body.appendChild(ui.switches([ui.switchRow('acEnableAniListMetadata', 'Usa AniList', 'Completa solo i campi che AnimeClick lascia vuoti, prima di TMDB.', { key: 'EnableAniListMetadata' })]));
        anilist.body.appendChild(ui.hint('Non scrive testi: AniList è solo in inglese, quindi titoli e trame restano ad AnimeClick, TMDB e TheTVDB. Un ID AniList viene usato solo se tipo e anno coincidono con l’opera. Dati forniti da AniList.'));

        var ai = sourceCard({ id: 'ai', name: 'Traduzione AI', icon: 'translate', role: 'Ultima risorsa: traduce in italiano titoli e trame disponibili solo in inglese.' });
        ai.card.classList.add('ac-source-wide');
        ai.body.appendChild(ui.callout(null, 'Si attiva solo quando AnimeClick non ha il testo. Vengono inviati soltanto i testi da tradurre; i servizi online possono avere un costo.', 'info'));
        ai.body.appendChild(ui.switches([ui.switchRow('acEnableAiTranslation', 'Consenti la traduzione dall’inglese', 'Puoi spegnerla quando vuoi senza perdere servizio e chiave salvati. Funziona solo con un modello scelto.', { key: 'EnableAiTranslation' })]));
        var providerSelect = ui.select('acAiProvider', 'Servizio di traduzione', [{ value: '', label: 'Scegli un servizio…' }]);
        var keyField = ui.field('acAiApiKey', 'Chiave del servizio', { secret: true, spellcheck: false, hint: 'Se una chiave è già salvata, lascia vuoto per mantenerla. Un servizio nella tua rete non ne ha bisogno.' });
        ai.body.appendChild(h('div', { class: 'ac-cols-2' },
            h('div', { class: 'ac-stack-s' }, providerSelect, h('p', { id: 'acAiProviderNote', class: 'ac-hint' })),
            h('div', { class: 'ac-stack-s' }, keyField, ui.check('acClearAiKey', 'Rimuovi la chiave salvata', 'Viene rimossa quando salvi.'))));
        var modelsButton = ui.button('Elenca modelli', { id: 'acBtnAiModels', icon: 'search', small: true });
        ai.body.appendChild(ui.field('acAiModel', 'Modello', {
            key: 'AiModel', spellcheck: false, list: 'acAiModelList', placeholder: 'Scegli un modello…',
            hint: 'Premi «Elenca modelli» e scegli un nome dall’elenco.', action: modelsButton
        }));
        ai.body.appendChild(h('datalist', { id: 'acAiModelList' }));
        ai.body.appendChild(h('div', { class: 'ac-row' }, testButton('ai', 'Verifica AI'), ui.liveStatus('acTestResult_ai')));
        ai.body.appendChild(ui.details('Indirizzo del servizio e tempi di attesa', [
            ui.field('acAiEndpoint', 'Indirizzo del servizio', { type: 'url', placeholder: 'https://…', spellcheck: false, hint: 'Si compila da solo quando scegli il servizio. HTTPS per i servizi online, HTTP solo nella tua rete.' }),
            h('div', { class: 'ac-cols-2' },
                ui.field('acEpisodeTranslationTimeoutSec', 'Attesa massima (secondi)', { type: 'number', min: '5', max: '120', step: '1', key: 'EpisodeTranslationTimeoutSec', hint: 'Consigliato 90: è un tetto, non un’attesa.' }),
                ui.field('acTranslationCacheHours', 'Conserva le traduzioni (ore)', { type: 'number', min: '0', max: '876000', step: '1', key: 'TranslationCacheHours', hint: '0 le conserva senza scadenza; se cambia il testo originale vengono rifatte.' }))
        ]));
        var previewButton = ui.button('Genera anteprima', { id: 'acBtnPreviewTranslation', icon: 'translate', small: true });
        var previewOut = h('div', { id: 'acTranslationPreview', class: 'ac-stack-s', 'aria-live': 'polite' });
        ai.body.appendChild(ui.details('Prova una traduzione', [
            ui.hint('Usa il servizio selezionato e può consumare credito.'),
            ui.field('acPreviewSource', 'Testo inglese', { multiline: true, rows: 4, maxLength: 8000, hint: 'Una breve sinossi, fino a 8000 caratteri.' }),
            h('div', { class: 'ac-row' }, previewButton),
            previewOut
        ]));

        view.appendChild(h('div', { class: 'ac-sources' }, animeclick.card, tmdb.card, fanart.card, tvdb.card, anilist.card, ai.card));

        el('acAiProvider').addEventListener('change', function () {
            // A model name from one vendor means nothing to another, and the endpoint follows the service.
            var provider = currentProvider();
            el('acAiApiKey').value = '';
            if (provider) {
                el('acAiEndpoint').value = val(provider, 'chatEndpoint') || '';
                el('acAiModel').value = '';
                clear(el('acAiModelList'));
            }
            updateProviderHint();
            markDirty();
        });

        modelsButton.addEventListener('click', function () {
            var out = el('acTestResult_ai');
            ui.setBusy(modelsButton, true, 'Lettura…');
            ui.status(out, 'Richiesta dell’elenco dei modelli…');
            api.request('POST', 'Plugins/AnimeClick/AiModels', providerPayload('ai')).then(function (result) {
                var models = list(val(result, 'models'));
                replace(el('acAiModelList'), models.map(function (model) { return h('option', { value: model }); }));
                if (models.length) ui.status(out, models.length + ' modelli disponibili: inizia a scrivere nel campo Modello per filtrarli.', 'ok');
                else ui.status(out, truncate(val(result, 'errorMessage') || 'Il servizio non ha elencato modelli.', 240), 'bad');
            }).catch(function (error) {
                ui.status(out, truncate(error.message, 240), 'bad');
            }).finally(function () { ui.setBusy(modelsButton, false); });
        });

        el('acPreviewSource').addEventListener('input', function () { ui.fieldError(this, ''); });
        previewButton.addEventListener('click', function () {
            var source = requireValue(el('acPreviewSource'), 'Incolla una breve sinossi in inglese.');
            if (!source) return;
            var payload = providerPayload('ai');
            payload.sourceText = source;
            ui.setBusy(previewButton, true, 'Traduzione…');
            api.request('POST', 'Plugins/AnimeClick/PreviewTranslation', payload).then(function (result) {
                if (!val(result, 'success')) {
                    replace(previewOut, ui.callout('Nessuna traduzione', val(result, 'errorMessage') || 'Prova prima «Verifica AI».', 'warn'));
                    return;
                }
                replace(previewOut, h('div', { class: 'ac-row' }, ui.badge('EN → IT', 'ok'), ui.badge(val(result, 'model') || 'AI')),
                    h('p', { class: 'ac-preview-text', text: val(result, 'translation') }));
            }).catch(function (error) {
                replace(previewOut, ui.callout('Nessuna traduzione', truncate(error.message, 300), 'warn'));
            }).finally(function () { ui.setBusy(previewButton, false); });
        });
    }

    /* ===== Strumenti: correggi un abbinamento ===== */

    /** The input, select or textarea inside a field built by ui.field / ui.select. */
    function fieldControl(fieldNode) {
        return fieldNode.querySelector('input, textarea, select');
    }

    /**
     * The trimmed value of a field that must not be empty (or shorter than `minimum` characters); when
     * it is, says so next to it, moves the focus there and returns ''.
     */
    function requireValue(input, message, minimum) {
        var value = input.value.trim();
        var ok = value.length >= (minimum || 1);
        ui.fieldError(input, ok ? '' : message);
        if (!ok) input.focus();
        return ok ? value : '';
    }

    function onEnter(input, action) {
        input.addEventListener('keydown', function (event) {
            if (event.key !== 'Enter') return;
            event.preventDefault();
            action();
        });
    }

    function markUnlocked() {
        var host = el('acSelectedItemLockState');
        if (host) replace(host, ui.badge('Sbloccata', 'ok', 'unlock'));
    }

    /** Removes the Jellyfin locks of the title itself: its seasons and episodes keep theirs. */
    function unlockItem(itemId, buttonNode) {
        ui.setBusy(buttonNode, true, 'Sblocco…');
        return api.request('POST', 'Plugins/AnimeClick/UnlockItem', { itemId: itemId }).then(function () {
            markUnlocked();
            ui.toast('Scheda sbloccata in Jellyfin', 'success');
            AC.bus.emit('library-changed');
            return true;
        }).catch(function (error) {
            ui.toast(truncate(error.message, 240), 'error');
            ui.setBusy(buttonNode, false);
            return false;
        });
    }

    function checkItemStatus(targetId) {
        var lockHost = el('acSelectedItemLockState');
        if (!lockHost || !targetId) return;
        clear(lockHost);
        api.request('GET', 'Plugins/AnimeClick/IdentifyStatus?itemId=' + encodeURIComponent(targetId)).then(function (status) {
            if (!el('acItemId') || el('acItemId').value !== targetId) return;
            var currentId = val(status, 'animeClickId');
            if (currentId && el('acFallbackAnimeId') && !el('acFallbackAnimeId').value.trim()) {
                el('acFallbackAnimeId').value = currentId;
            }
            var isLocked = !!val(status, 'isLocked') || list(val(status, 'lockedFields')).length > 0;
            if (!isLocked) return;
            var unlockButton = ui.button('Sblocca ora', { id: 'acBtnUnlockSelected', small: true, variant: 'ghost', icon: 'unlock' });
            unlockButton.addEventListener('click', function () { unlockItem(targetId, unlockButton); });
            replace(lockHost, ui.badge('Bloccata in Jellyfin', 'warn', 'lock'), unlockButton);
        }).catch(function () { /* Diagnostic status is optional. */ });
    }

    function returnToLibrary(returnKey, reload) {
        AC.app.go('library');
        var open = function () { AC.library.openDetail(returnKey); };
        if (reload) AC.library.loadReports(true).then(open); else open();
    }

    var CARD_REQUIRED = 'Scegli un risultato o incolla il link della scheda.';
    var previewRequest = null;

    /**
     * Drops the preview on screen and withdraws the one still on its way: it described a card no
     * longer chosen, and the server should not keep reading AnimeClick for it.
     */
    function discardPreview() {
        if (previewRequest) previewRequest.abort();
        previewRequest = null;
        ui.setBusy(el('acBtnPreviewCard'), false);
        clear(el('acCardPreview'));
    }

    function clearMatch() {
        el('acAnimeClickId').value = '';
        ui.fieldError(el('acAnimeClickId'), '');
        clear(el('acAnimeClickCandidates'));
        discardPreview();
        clear(el('acIdentifyResult'));
    }

    function clearSelection() {
        identify.item = null;
        el('acItemId').value = '';
        clearMatch();
        replace(el('acSelectedItem'), h('p', { class: 'ac-muted', text: 'Nessun titolo selezionato.' }));
        el('acItemSearch').focus();
    }

    function pickItem(item) {
        identify.item = item;
        var initialId = val(item, 'seasonId') || val(item, 'id') || '';
        el('acItemId').value = initialId;
        ui.fieldError(el('acItemSearch'), '');
        var year = val(item, 'productionYear') || val(item, 'year');
        var returnKey = val(item, 'returnToKey');
        replace(el('acSelectedItem'),
            ui.poster({ id: val(item, 'id'), name: val(item, 'name'), hasPrimaryImage: val(item, 'hasPrimaryImage') }, { height: 120 }),
            h('div', { class: 'ac-stack-s ac-grow' },
                h('strong', { text: val(item, 'name') || 'Titolo selezionato' }),
                h('p', { class: 'ac-muted', text: (val(item, 'type') === 'Movie' ? 'Film' : 'Serie') + (year ? ' · ' + year : '') }),
                h('div', { id: 'acSelectedItemLockState', class: 'ac-row' })),
            h('div', { class: 'ac-row' },
                returnKey ? ui.button('Torna alla libreria', {
                    id: 'acBtnReturnLibrary', small: true, variant: 'ghost', icon: 'arrowLeft',
                    onClick: function () { returnToLibrary(returnKey); }
                }) : null,
                ui.button('Cambia', { small: true, variant: 'ghost', onClick: clearSelection })));
        clear(el('acItemCandidates'));
        clearMatch();
        var seasonNumber = val(item, 'seasonNumber');
        el('acAnimeClickSearch').value = (val(item, 'name') || '') + (seasonNumber != null && seasonNumber > 1 ? ' ' + seasonNumber : '');
        ui.fieldError(el('acAnimeClickSearch'), '');
        checkItemStatus(initialId);
        if (val(item, 'type') !== 'Movie') loadSeasons(item);
    }

    /**
     * A season with its own AnimeClick card (a sequel, a final arc) is corrected on the season itself:
     * the series keeps its card and the correction can be shared for that season only.
     */
    function loadSeasons(item) {
        var seriesId = val(item, 'id');
        var desiredSeasonId = val(item, 'seasonId');
        var desiredSeasonNumber = val(item, 'seasonNumber');
        var picker = ui.select('acItemSeason', 'Cosa correggere', [{ value: seriesId, label: 'Tutta la serie' }], {
            hint: 'Scegli una stagione se ha una scheda AnimeClick sua, per esempio un sequel o un arco finale.'
        });
        picker.id = 'acItemSeasonField';
        el('acSelectedItem').appendChild(picker);
        var select = fieldControl(picker);
        select.addEventListener('change', function () {
            el('acItemId').value = select.value;
            checkItemStatus(select.value);
        });
        var sameId = function (left, right) {
            return String(left || '').replace(/-/g, '').toLowerCase() === String(right || '').replace(/-/g, '').toLowerCase();
        };
        api.request('GET', 'Shows/' + encodeURIComponent(seriesId) + '/Seasons?Fields=ProviderIds').then(function (result) {
            // Another title may have been picked while the seasons were loading.
            if (!select.isConnected) return;
            var matched = null;
            list(val(result, 'items')).forEach(function (season) {
                var seasonId = val(season, 'id');
                var number = val(season, 'indexNumber');
                var card = val(val(season, 'providerIds') || {}, 'animeClick');
                select.appendChild(h('option', {
                    value: seasonId,
                    text: (number === 0 ? 'Speciali' : number != null ? 'Stagione ' + number : (val(season, 'name') || 'Stagione'))
                        + (card ? ' · AnimeClick ' + String(card).split('/')[0] : '')
                }));
                if ((desiredSeasonId && sameId(seasonId, desiredSeasonId))
                    || (!matched && desiredSeasonNumber != null && number === desiredSeasonNumber)) {
                    matched = seasonId;
                }
            });
            if (matched) {
                select.value = matched;
                el('acItemId').value = matched;
                checkItemStatus(matched);
            }
        }).catch(function () { /* The series stays selectable on its own. */ });
    }

    function searchLibrary(buttonNode) {
        var query = requireValue(el('acItemSearch'), 'Scrivi almeno due lettere del titolo.', 2);
        if (!query) return;
        var host = el('acItemCandidates');
        ui.setBusy(buttonNode, true, 'Ricerca…');
        clear(host);
        api.request('GET', 'Items?Recursive=true&IncludeItemTypes=Series,Movie&Limit=24&Fields=ProductionYear&SearchTerm=' + encodeURIComponent(query)).then(function (result) {
            var items = list(val(result, 'items'));
            if (!items.length) {
                host.appendChild(h('p', { class: 'ac-muted', text: 'Nessun titolo trovato: prova con un nome più breve.' }));
                return;
            }
            host.appendChild(h('div', { class: 'ac-picks' }, items.map(function (item) {
                var normalized = {
                    id: val(item, 'id'), name: val(item, 'name') || 'Senza titolo', type: val(item, 'type'),
                    productionYear: val(item, 'productionYear'),
                    hasPrimaryImage: !!(val(item, 'imageTags') && val(val(item, 'imageTags'), 'primary'))
                };
                return h('button', { type: 'button', class: 'ac-pick', on: { click: function () { pickItem(normalized); el('acAnimeClickId').focus(); } } },
                    ui.poster(normalized, { height: 240 }),
                    h('strong', { text: normalized.name }),
                    h('small', { text: (normalized.type === 'Movie' ? 'Film' : 'Serie') + (normalized.productionYear ? ' · ' + normalized.productionYear : '') }));
            })));
        }).catch(function (error) {
            host.appendChild(h('p', { class: 'ac-status is-bad', text: truncate(error.message, 240) }));
        }).finally(function () { ui.setBusy(buttonNode, false); });
    }

    function searchAnimeClick(buttonNode) {
        var query = requireValue(el('acAnimeClickSearch'), 'Scrivi almeno due lettere del titolo da cercare su AnimeClick.', 2);
        if (!query) return;
        var host = el('acAnimeClickCandidates');
        ui.setBusy(buttonNode, true, 'Ricerca…');
        clear(host);
        api.request('GET', 'Plugins/AnimeClick/TestLookup?name=' + encodeURIComponent(query)).then(function (rows) {
            var results = list(rows).filter(function (row) { return val(row, 'animeClickId'); });
            if (!results.length) {
                host.appendChild(h('p', { class: 'ac-muted', text: 'AnimeClick non ha restituito risultati: incolla il link della scheda qui sotto.' }));
                return;
            }
            host.appendChild(h('div', { class: 'ac-picks' }, results.slice(0, 12).map(function (row) {
                var id = val(row, 'animeClickId');
                var pick = h('button', { type: 'button', class: 'ac-pick', 'aria-pressed': 'false' },
                    ui.poster({ name: val(row, 'name') || id }, { src: val(row, 'imageUrl') }),
                    h('strong', { text: val(row, 'name') || id }),
                    h('small', { text: (val(row, 'year') ? val(row, 'year') + ' · ' : '') + 'ID ' + id }));
                pick.addEventListener('click', function () {
                    host.querySelectorAll('.ac-pick').forEach(function (other) { other.setAttribute('aria-pressed', 'false'); });
                    pick.setAttribute('aria-pressed', 'true');
                    el('acAnimeClickId').value = id;
                    ui.fieldError(el('acAnimeClickId'), '');
                    previewCard();
                });
                return pick;
            })));
        }).catch(function (error) {
            if (error.blocked) loadAnimeClickStatus();
            host.appendChild(h('p', { class: 'ac-status is-bad', text: truncate(error.message, 300) }));
        }).finally(function () { ui.setBusy(buttonNode, false); });
    }

    /**
     * Shows the chosen card before it is bound: its title, year and episodes, and the library titles
     * that already use it. The check is read-only; «Identifica e aggiorna» is what saves.
     */
    function previewCard() {
        var host = el('acCardPreview');
        var buttonNode = el('acBtnPreviewCard');
        var animeClickId = requireValue(el('acAnimeClickId'), CARD_REQUIRED);
        if (!animeClickId) return;
        discardPreview();
        var mine = previewRequest = new AbortController();
        ui.setBusy(buttonNode, true, 'Lettura…');
        replace(host, ui.skeleton(132));
        api.request('GET', 'Plugins/AnimeClick/PreviewCard?animeClickId=' + encodeURIComponent(animeClickId)
            + '&itemId=' + encodeURIComponent(el('acItemId').value.trim()), undefined, undefined, mine.signal).then(function (preview) {
            if (previewRequest === mine) renderCardPreview(preview);
        }).catch(function (error) {
            if (error.blocked) loadAnimeClickStatus();
            if (previewRequest === mine) replace(host, ui.callout(error.blocked ? 'AnimeClick sta bloccando le richieste' : 'Non riesco a mostrare questa scheda', truncate(error.message, 300), 'warn'));
        }).finally(function () {
            if (previewRequest !== mine) return;
            previewRequest = null;
            ui.setBusy(buttonNode, false);
        });
    }

    var USE_KINDS = { Movie: 'film', Season: 'stagione', Series: 'serie' };

    function renderCardPreview(preview) {
        var host = el('acCardPreview');
        var title = val(preview, 'title') || val(preview, 'animeClickId');
        var original = val(preview, 'originalTitle');
        var episodes = val(preview, 'episodeCount');
        var facts = [
            val(preview, 'year'),
            val(preview, 'category'),
            episodes ? AC.util.plural(episodes, 'episodio', 'episodi') : null,
            val(preview, 'status')
        ].filter(Boolean);
        var url = val(preview, 'url');
        var uses = list(val(preview, 'usedBy'));
        replace(host, h('div', { class: 'ac-card-preview', id: 'acCardPreviewBody' },
            ui.poster({ name: title }, { src: val(preview, 'imageUrl') }),
            h('div', { class: 'ac-stack-s' },
                h('div', null,
                    h('strong', { class: 'ac-card-preview-title', text: title }),
                    original && original !== title ? h('p', { class: 'ac-muted', text: original }) : null),
                facts.length ? h('p', { class: 'ac-card-preview-facts', text: facts.join(' · ') }) : null,
                list(val(preview, 'genres')).length ? h('div', { class: 'ac-row' }, list(val(preview, 'genres')).map(function (genre) { return ui.badge(genre); })) : null,
                val(preview, 'overview') ? h('p', { class: 'ac-hint', text: val(preview, 'overview') }) : null,
                url && /^https?:\/\//i.test(url)
                    ? h('div', { class: 'ac-row' }, h('a', { class: 'ac-btn ac-btn--sm ac-btn--ghost', href: url, target: '_blank', rel: 'noopener noreferrer' }, icon('external'), 'Apri su AnimeClick'))
                    : null)),
            uses.length ? ui.callout('Scheda già usata nella libreria',
                'È abbinata a ' + uses.map(function (use) {
                    return '«' + (val(use, 'name') || 'Senza titolo') + '» (' + (USE_KINDS[val(use, 'type')] || 'serie') + ')';
                }).join(', ') + '. Va bene per due edizioni della stessa opera; altrimenti stai per creare un doppione.', 'warn') : null);
    }

    function refreshNow(itemId, buttonNode) {
        ui.setBusy(buttonNode, true, 'Accodamento…');
        api.request('POST', 'Plugins/AnimeClick/RefreshItem', {
            itemId: itemId, replaceAllMetadata: false, replaceAllImages: el('acReplaceAllImages').checked
        }).then(function () {
            ui.toast('Aggiornamento dei metadati avviato', 'success');
            buttonNode.remove();
        }).catch(function (error) {
            ui.toast(truncate(error.message, 240), 'error');
            ui.setBusy(buttonNode, false);
        });
    }

    function renderIdentified(response, itemId, animeClickId) {
        var out = el('acIdentifyResult');
        var success = !!val(response, 'success');
        var unlocked = val(response, 'unlockedCount') || 0;
        replace(out, ui.callout(success ? 'Abbinamento salvato' : 'Operazione incompleta',
            success
                ? (unlocked ? 'Scheda sbloccata, ID AnimeClick salvato e aggiornamento avviato.' : 'L’ID AnimeClick è salvato e l’aggiornamento è partito.')
                : truncate(val(response, 'error') || 'Errore sconosciuto.', 300),
            success ? 'ok' : 'warn'));

        var actions = h('div', { id: 'acIdentifyQuickActions', class: 'ac-row' });
        if (!success) {
            var retry = ui.button('Aggiorna metadati ora', { id: 'acBtnRetryRefresh', small: true, variant: 'primary', icon: 'refresh' });
            retry.addEventListener('click', function () { refreshNow(itemId, retry); });
            actions.appendChild(retry);
        }
        var returnKey = identify.item && val(identify.item, 'returnToKey');
        if (returnKey) {
            actions.appendChild(ui.button('Torna alla scheda in Libreria', {
                id: 'acBtnBackToLibrary', small: true, variant: 'ghost', icon: 'arrowLeft',
                onClick: function () { returnToLibrary(returnKey, true); }
            }));
        }
        if (actions.childNodes.length) out.appendChild(actions);
        if (unlocked) markUnlocked();

        var savedCard = val(response, 'animeClickId') || animeClickId;
        if (savedCard) el('acFallbackAnimeId').value = savedCard;
        var community = val(response, 'communityMessage');
        var proposal = val(response, 'communityProposal');
        if (success && proposal) renderShareOffer(out, itemId, proposal);
        else if (community && community !== 'Disattivata') out.appendChild(h('p', { class: 'ac-hint', text: community }));
        if (success && community && community !== 'Disattivata') AC.bus.emit('community-changed');
        ui.toast(success ? 'Abbinamento salvato' : 'Identificazione incompleta', success ? 'success' : 'error');
        if (success) AC.bus.emit('library-changed');
    }

    /** After a 409: the title is locked in Jellyfin, and the page can remove the lock itself. */
    function renderLockedChoice(itemId) {
        var unlockAndIdentify = ui.button('Sblocca e identifica ora', { id: 'acBtnUnlockAndIdentify', variant: 'primary', small: true, icon: 'unlock' });
        unlockAndIdentify.addEventListener('click', function () { runIdentify(true, unlockAndIdentify); });
        var unlockOnly = ui.button('Solo sblocca', { id: 'acBtnUnlockOnly', variant: 'ghost', small: true, icon: 'unlock' });
        unlockOnly.addEventListener('click', function () {
            unlockItem(itemId, unlockOnly).then(function (done) {
                if (!done) return;
                replace(el('acIdentifyResult'), ui.callout('Scheda sbloccata', 'Il blocco in Jellyfin è stato rimosso. Ora puoi premere «Identifica e aggiorna».', 'ok'));
            });
        });
        el('acIdentifyResult').appendChild(h('div', { id: 'acIdentifyQuickActions', class: 'ac-row' }, unlockAndIdentify, unlockOnly));
    }

    function runIdentify(unlock, triggerButton) {
        var itemId = el('acItemId').value.trim();
        if (!itemId) {
            // The choice lives in a hidden field: the problem is shown under the search that makes it.
            ui.fieldError(el('acItemSearch'), 'Scegli prima il titolo da correggere.');
            el('acItemSearch').focus();
            return;
        }
        var animeClickId = requireValue(el('acAnimeClickId'), CARD_REQUIRED);
        if (!animeClickId) return;
        var out = el('acIdentifyResult');
        var buttonNode = triggerButton || el('acBtnIdentify');
        ui.setBusy(buttonNode, true, unlock ? 'Sblocco e aggiornamento…' : 'Aggiornamento…');
        replace(out, h('p', { class: 'ac-status', text: unlock ? 'Sblocco della scheda e verifica in corso…' : 'Verifica della scheda in corso…' }));
        var payload = { itemId: itemId, animeClickId: animeClickId, replaceAllMetadata: false, replaceAllImages: el('acReplaceAllImages').checked };
        if (unlock) payload.unlock = true;
        api.request('POST', 'Plugins/AnimeClick/IdentifyAndRefresh', payload).then(function (response) {
            renderIdentified(response, itemId, animeClickId);
        }).catch(function (error) {
            var message = truncate(error.message, 300);
            if (error.blocked) loadAnimeClickStatus();
            replace(out, ui.callout('Identificazione non riuscita', message, 'bad'));
            if (error.status === 409 && /bloccata/i.test(message)) renderLockedChoice(itemId);
            ui.toast('Identificazione non riuscita', 'error');
        }).finally(function () { ui.setBusy(buttonNode, false); });
    }

    function buildFixCard() {
        var fix = ui.card({ icon: 'link', title: 'Correggi un abbinamento', text: 'Scegli il titolo nella tua libreria e indica la scheda giusta su AnimeClick. L’aggiornamento continua in background.' });
        var findButton = ui.button('Cerca', { id: 'acBtnFindItem', icon: 'search' });
        var lookupButton = ui.button('Cerca su AnimeClick', { id: 'acBtnLookup', icon: 'search' });
        var previewButton = ui.button('Anteprima', { id: 'acBtnPreviewCard', icon: 'eye', title: 'Mostra la scheda prima di salvarla' });
        var identifyButton = ui.button('Identifica e aggiorna', { id: 'acBtnIdentify', variant: 'primary', icon: 'check' });
        var itemSearch = ui.field('acItemSearch', 'Titolo nella libreria', { type: 'search', placeholder: 'Es. Frieren…', maxLength: 200, action: findButton });
        var lookupSearch = ui.field('acAnimeClickSearch', 'Cerca su AnimeClick', { type: 'search', placeholder: 'Titolo su AnimeClick…', maxLength: 200, action: lookupButton });
        var idField = ui.field('acAnimeClickId', 'Link o ID AnimeClick', {
            placeholder: 'https://www.animeclick.it/anime/…', maxLength: 512, spellcheck: false, action: previewButton,
            hint: 'Scegli un risultato oppure incolla l’indirizzo della scheda da animeclick.it.'
        });

        fix.body.appendChild(h('div', { class: 'ac-stack' },
            h('h3', { class: 'ac-step-title' }, h('span', { class: 'ac-step-num', 'aria-hidden': 'true', text: '1' }), 'Quale titolo vuoi correggere?'),
            itemSearch,
            h('input', { type: 'hidden', id: 'acItemId' }),
            h('div', { id: 'acItemCandidates', 'aria-live': 'polite' }),
            h('div', { id: 'acSelectedItem', class: 'ac-selected' }, h('p', { class: 'ac-muted', text: 'Nessun titolo selezionato.' }))));
        fix.body.appendChild(h('div', { class: 'ac-stack' },
            h('h3', { class: 'ac-step-title' }, h('span', { class: 'ac-step-num', 'aria-hidden': 'true', text: '2' }), 'Qual è la scheda giusta?'),
            lookupSearch,
            h('div', { id: 'acAnimeClickCandidates', 'aria-live': 'polite' }),
            idField,
            h('div', { id: 'acCardPreview', class: 'ac-stack-s', 'aria-live': 'polite' }),
            ui.check('acReplaceAllImages', 'Sostituisci tutte le immagini', 'Chiede a Jellyfin di rifare le immagini. Lascia spento per tenere le tue locandine.')));
        fix.body.appendChild(h('div', { class: 'ac-card-foot' }, identifyButton));
        fix.body.appendChild(h('div', { id: 'acIdentifyResult', 'aria-live': 'polite' }));

        findButton.addEventListener('click', function () { searchLibrary(findButton); });
        lookupButton.addEventListener('click', function () { searchAnimeClick(lookupButton); });
        previewButton.addEventListener('click', previewCard);
        identifyButton.addEventListener('click', function () { runIdentify(false, identifyButton); });
        onEnter(fieldControl(itemSearch), function () { searchLibrary(findButton); });
        onEnter(fieldControl(lookupSearch), function () { searchAnimeClick(lookupButton); });
        onEnter(fieldControl(idField), function () { runIdentify(false, identifyButton); });
        fieldControl(idField).addEventListener('input', function () {
            // A preview describes the card that was in the field: once the field changes it is stale.
            discardPreview();
            ui.fieldError(fieldControl(idField), '');
        });
        [itemSearch, lookupSearch].forEach(function (fieldNode) {
            fieldControl(fieldNode).addEventListener('input', function () { ui.fieldError(this, ''); });
        });
        return fix.card;
    }

    /* ===== Strumenti: da dove arriva questa trama? ===== */

    function renderProbe(result) {
        var out = el('acFallbackResult');
        clear(out);
        var episodeTitle = val(result, 'episodeTitle');
        if (episodeTitle) {
            out.appendChild(h('div', { class: 'ac-row' }, ui.badge('Titolo AnimeClick', 'info', 'title'), h('strong', { text: episodeTitle })));
        }
        if (!val(result, 'success')) {
            out.appendChild(ui.callout('Nessuna fonte disponibile', val(result, 'errorMessage') || 'La catena non ha prodotto una trama.', 'warn'));
        } else {
            out.appendChild(h('div', { class: 'ac-row' },
                ui.badge(val(result, 'source') || 'Fonte', 'ok'), ui.badge(val(result, 'sourceLanguage') || 'it'),
                val(result, 'usedAi') ? ui.badge(val(result, 'model') || 'AI', 'warn') : null));
            out.appendChild(h('p', { class: 'ac-preview-text', text: val(result, 'overview') }));
        }
        if (!val(result, 'animeClickMatchConclusive')) {
            out.appendChild(ui.hint('Prova indicativa: usa solo serie, stagione e numero. Il vero aggiornamento considera anche titolo, file e struttura della libreria.'));
        }
        var steps = list(val(result, 'chain'));
        if (steps.length) {
            out.appendChild(h('div', { class: 'ac-chain' }, steps.map(function (step, index) {
                var on = !!val(step, 'configured');
                return [index ? icon('chevronRight', 'ac-chain-arrow') : null,
                    h('span', { class: 'ac-chain-step' + (on ? ' is-on' : '') }, h('span', { class: 'ac-led' + (on ? ' is-ok' : '') }),
                        (val(step, 'source') || 'Fonte') + ' · ' + (val(step, 'language') || ''))];
            })));
        }
    }

    function runProbe() {
        var buttonNode = el('acBtnPreviewFallback');
        var id = requireValue(el('acFallbackAnimeId'), 'Indica la scheda AnimeClick della serie.');
        var season = parseInt(requireValue(el('acFallbackSeason'), 'Indica la stagione.'), 10);
        var episode = parseInt(requireValue(el('acFallbackEpisode'), 'Indica l’episodio.'), 10);
        if (!id || isNaN(season) || isNaN(episode)) return;

        // The chain runs with the saved settings, so pending edits are saved first.
        ui.setBusy(buttonNode, true, dirty ? 'Salvataggio e analisi…' : 'Analisi…');
        (dirty ? save() : Promise.resolve()).then(function () {
            return api.request('POST', 'Plugins/AnimeClick/PreviewEpisodeFallback', { animeClickId: id, season: season, episode: episode })
                .then(renderProbe, function (error) {
                    renderProbe({ success: false, errorMessage: truncate(error.message, 300), chain: [] });
                });
        }, function (error) {
            if (!error.reported) ui.toast('Salvataggio non riuscito: ' + truncate(error.message, 240), 'error');
        }).finally(function () { ui.setBusy(buttonNode, false); });
    }

    function buildProbeCard() {
        var probe = ui.card({ icon: 'flask', title: 'Da dove arriva questa trama?', text: 'Esegue la catena delle fonti salvata su un episodio e mostra quale fonte risponde.' });
        var probeButton = ui.button('Esegui la catena', { id: 'acBtnPreviewFallback', icon: 'play', onClick: runProbe });
        var fields = [
            ui.field('acFallbackAnimeId', 'ID AnimeClick', { placeholder: 'Es. 72/naruto…', spellcheck: false }),
            ui.field('acFallbackSeason', 'Stagione', { type: 'number', min: '0', inputmode: 'numeric', hint: '0 per gli speciali.' }),
            ui.field('acFallbackEpisode', 'Episodio', { type: 'number', min: '0', inputmode: 'numeric' })
        ];
        fieldControl(fields[1]).value = '1';
        fieldControl(fields[2]).value = '1';
        fields.forEach(function (fieldNode) { onEnter(fieldControl(fieldNode), runProbe); });
        probe.body.appendChild(h('div', { class: 'ac-cols-3' }, fields));
        probe.body.appendChild(h('div', { class: 'ac-row' }, probeButton, h('span', { id: 'acProbeBackHost', class: 'ac-row' })));
        probe.body.appendChild(h('div', { id: 'acFallbackResult', class: 'ac-stack-s', 'aria-live': 'polite' }));
        return probe.card;
    }

    /* ===== Strumenti: manutenzione ===== */

    /* The server decides which source a cache entry belongs to; the page only names the sources. */
    var CACHE_GROUPS = {
        animeclick: 'AnimeClick', translation: 'Traduzioni AI', tmdb: 'TMDB', tvdb: 'TheTVDB', anilist: 'AniList',
        fanart: 'Fanart', community: 'Comunità', ledger: 'Registro dei tentativi'
    };
    var cacheStats = { at: 0, loading: null };

    function renderCacheStats(stats) {
        var host = el('acCacheStats');
        if (!host) return;
        var entries = val(stats, 'entries') || 0;
        if (!entries) {
            replace(host, h('p', { class: 'ac-muted', text: 'La cache è vuota: i metadati verranno letti al prossimo aggiornamento.' }));
            return;
        }
        var oldest = AC.util.relTime(val(stats, 'oldestEntryUtc'));
        replace(host,
            h('p', { id: 'acCacheSummary' }, h('strong', { text: AC.util.plural(entries, 'elemento', 'elementi') }), ' · ' + AC.util.bytes(val(stats, 'bytes'))
                + (oldest ? ' · il più vecchio: ' + oldest : '')),
            h('ul', { class: 'ac-cache-groups' }, list(val(stats, 'groups')).map(function (group) {
                return h('li', null, h('span', { text: CACHE_GROUPS[val(group, 'id')] || val(group, 'id') }),
                    h('span', { class: 'ac-muted', text: AC.util.num(val(group, 'entries')) + ' · ' + AC.util.bytes(val(group, 'bytes')) }));
            })));
    }

    /**
     * Counting the cache means reading the attributes of every file in it: the numbers are asked
     * again only when a minute has passed or the cache has just been emptied (`force`).
     */
    function loadCacheStats(force) {
        var host = el('acCacheStats');
        if (!host) return Promise.resolve();
        if (cacheStats.loading) return cacheStats.loading;
        if (!force && Date.now() - cacheStats.at < 60000) return Promise.resolve();
        cacheStats.loading = api.request('GET', 'Plugins/AnimeClick/CacheStats').then(function (stats) {
            cacheStats.at = Date.now();
            renderCacheStats(stats);
        }).catch(function (error) {
            replace(host, h('p', { class: 'ac-status is-bad', text: 'Stato della cache non disponibile: ' + truncate(error.message, 200) }));
        }).finally(function () { cacheStats.loading = null; });
        return cacheStats.loading;
    }

    function clearAllCache(buttonNode) {
        ui.confirm({
            title: 'Svuotare tutta la cache?',
            message: ['Metadati, abbinamenti e traduzioni memorizzati verranno riletti al prossimo aggiornamento.',
                'Le traduzioni AI già fatte andranno rifatte: con un servizio a pagamento hanno un costo.'],
            confirmLabel: 'Svuota', tone: 'danger'
        }).then(function (yes) {
            if (!yes) return;
            var out = el('acCacheResult');
            ui.setBusy(buttonNode, true, 'Svuotamento…');
            api.request('POST', 'Plugins/AnimeClick/ClearCache', {}).then(function (response) {
                ui.status(out, 'Cache svuotata: ' + AC.util.plural(val(response, 'removed') || 0, 'elemento', 'elementi') + '.', 'ok');
                ui.toast('Cache svuotata', 'success');
                return loadCacheStats(true);
            }).catch(function (error) { ui.status(out, truncate(error.message, 240), 'bad'); })
                .finally(function () { ui.setBusy(buttonNode, false); });
        });
    }

    /** Hands a JSON document to the browser as a file; nothing is sent anywhere. */
    function downloadJson(fileName, content) {
        var url = URL.createObjectURL(new Blob([JSON.stringify(content, null, 2)], { type: 'application/json' }));
        var link = h('a', { href: url, download: fileName });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }

    /**
     * Copies text to the clipboard. The asynchronous clipboard exists only on HTTPS and localhost,
     * and a Jellyfin reached by its LAN address is neither: there the selection of a temporary
     * field is copied instead.
     */
    function copyText(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text);
        return new Promise(function (resolve, reject) {
            var holder = h('textarea', { class: 'ac-offscreen', 'aria-hidden': 'true', tabIndex: -1 });
            holder.value = text;
            document.body.appendChild(holder);
            holder.select();
            var copied = false;
            try { copied = document.execCommand('copy'); } catch (error) { copied = false; }
            holder.remove();
            if (copied) resolve(); else reject(new Error('il browser non permette la copia: scarica il rapporto.'));
        });
    }

    function diagnosticReport(buttonNode, deliver) {
        var out = el('acReportResult');
        ui.setBusy(buttonNode, true, 'Preparazione…');
        api.request('GET', 'Plugins/AnimeClick/DiagnosticReport').then(deliver).then(function (message) {
            ui.status(out, message, 'ok');
        }).catch(function (error) {
            ui.status(out, 'Rapporto non riuscito: ' + truncate(error.message, 200), 'bad');
        }).finally(function () { ui.setBusy(buttonNode, false); });
    }

    function buildMaintenanceCard() {
        var maintenance = ui.card({ icon: 'database', title: 'Cache e manutenzione', text: 'Quello che il plugin ha già letto resta in cache: svuotala solo per forzare una nuova lettura.' });
        var clearButton = ui.button('Svuota tutta la cache', { id: 'acBtnClearCache', variant: 'danger', icon: 'trash' });
        clearButton.addEventListener('click', function () { clearAllCache(clearButton); });
        var setupButton = ui.button('Riapri il setup guidato', { id: 'acBtnReopenSetup', icon: 'sparkles', variant: 'ghost', onClick: function () { AC.setup.open('full'); } });
        var download = ui.button('Scarica il rapporto', { id: 'acBtnReportDownload', icon: 'download', small: true });
        download.addEventListener('click', function () {
            diagnosticReport(download, function (report) {
                downloadJson('animeclick-diagnostica.json', report);
                return 'Rapporto scaricato: allegalo alla segnalazione.';
            });
        });
        var copy = ui.button('Copia', { id: 'acBtnReportCopy', icon: 'copy', small: true, variant: 'ghost' });
        copy.addEventListener('click', function () {
            diagnosticReport(copy, function (report) {
                return copyText(JSON.stringify(report, null, 2)).then(function () {
                    copy.focus();
                    return 'Rapporto copiato: incollalo nella segnalazione.';
                });
            });
        });

        maintenance.body.appendChild(h('div', { id: 'acCacheStats', class: 'ac-stack-s', 'aria-live': 'polite' }, ui.skeleton(64)));
        maintenance.body.appendChild(h('div', { class: 'ac-row' }, clearButton, setupButton));
        maintenance.body.appendChild(ui.liveStatus('acCacheResult'));
        maintenance.body.appendChild(h('div', { class: 'ac-stack-s ac-subsection' },
            h('h3', { class: 'ac-h3', text: 'Rapporto diagnostico' }),
            h('p', { class: 'ac-hint', text: 'Per segnalare un problema: versioni, impostazioni senza chiavi, stato di librerie e cache. Non contiene titoli, percorsi, indirizzi o token.' }),
            h('div', { class: 'ac-row' }, download, copy),
            ui.liveStatus('acReportResult')));

        AC.bus.on('view', function (name) { if (name === 'tools') loadCacheStats(false); });
        return maintenance.card;
    }

    function buildAdvancedCard() {
        var advanced = ui.card({ icon: 'sliders', title: 'Parametri avanzati', text: 'Da toccare solo per diagnosi o se AnimeClick cambia comportamento.' });
        advanced.body.appendChild(ui.details('Ricerca, rete e compatibilità', [
            h('div', { class: 'ac-cols-2' },
                ui.field('acMaxSearchResults', 'Risultati di ricerca', { type: 'number', min: '1', max: '50', key: 'MaxSearchResults', inputmode: 'numeric', hint: 'Da 1 a 50.' }),
                ui.field('acCacheHours', 'Durata della cache (ore)', { type: 'number', min: '1', max: '8760', key: 'CacheHours', inputmode: 'numeric', hint: 'Da 1 a 8760.' }),
                ui.field('acNegativeCacheHours', 'Attesa dopo una ricerca vuota (ore)', { type: 'number', min: '0', max: '8760', key: 'NegativeCacheHours', inputmode: 'numeric', hint: '0 non ricorda le ricerche vuote.' }),
                ui.field('acRequestDelayMilliseconds', 'Pausa fra le richieste (ms)', { type: 'number', min: '500', max: '60000', key: 'RequestDelayMilliseconds', inputmode: 'numeric', hint: 'Almeno 500, per rispetto di AnimeClick.' })),
            ui.field('acBaseUrl', 'Indirizzo di AnimeClick', { type: 'url', key: 'BaseUrl', spellcheck: false, hint: 'Cambialo solo se il sito cambia dominio.' }),
            ui.field('acUserAgent', 'User-Agent', { key: 'UserAgent', spellcheck: false, hint: 'Come il plugin si presenta al sito.' })
        ]));
        advanced.body.appendChild(ui.details('Layout degli episodi', [
            ui.callout('Solo per serie con numerazione eccezionale', 'Una riga per serie: ID=flat, ID=explicit oppure ID=13,24 con i confini cumulativi delle stagioni. Righe vuote, commenti con # e righe non valide vengono ignorati. Un override sbagliato può impedire un abbinamento che il riconoscimento automatico avrebbe trovato.', 'warn'),
            ui.field('acEpisodeLayoutOverrides', 'Override del layout', { multiline: true, rows: 6, key: 'EpisodeLayoutOverrides', spellcheck: false, placeholder: '# Solo eccezioni confermate\n72=flat\n456=13,24', hint: 'In 13,24 la stagione 1 termina all’episodio globale 13 e la 2 al 24.' })
        ]));
        return advanced.card;
    }

    function buildTools(view) {
        view.appendChild(ui.sectionHead('ツール', 'Strumenti', 'Sistema, verifica, regola',
            'Correggi un abbinamento sbagliato, controlla da dove arriva una trama e regola i parametri avanzati.'));
        view.appendChild(buildFixCard());
        view.appendChild(h('div', { class: 'ac-cols-2' }, buildProbeCard(), buildMaintenanceCard()));
        view.appendChild(buildAdvancedCard());
    }

    /** Opens Strumenti with a title already chosen, e.g. from the library detail panel. */
    function startIdentify(item) {
        AC.app.go('tools');
        pickItem(item);
        var target = el('acAnimeClickSearch');
        AC.dom.reveal(target, 'center');
        target.focus({ preventScroll: true });
        if (target.value.trim().length >= 2) searchAnimeClick(el('acBtnLookup'));
    }

    /** Opens Strumenti → Da dove arriva questa trama? pre-filled for an episode and runs the probe. */
    function startProbe(options) {
        options = options || {};
        AC.app.go('tools');
        if (options.animeClickId) el('acFallbackAnimeId').value = options.animeClickId;
        el('acFallbackSeason').value = String(options.season != null ? options.season : 1);
        el('acFallbackEpisode').value = String(options.episode != null ? options.episode : 1);
        replace(el('acProbeBackHost'), options.returnToKey ? ui.button('Torna alla libreria', {
            id: 'acBtnProbeReturnLibrary', small: true, variant: 'ghost', icon: 'arrowLeft',
            onClick: function () { returnToLibrary(options.returnToKey); }
        }) : null);
        var buttonNode = el('acBtnPreviewFallback');
        AC.dom.reveal(buttonNode, 'center');
        buttonNode.focus({ preventScroll: true });
        runProbe();
    }

    /* ===== Comunità ===== */

    var SHARING_CHOICES = [
        { value: 'Ask', label: 'Chiedimi ogni volta' },
        { value: 'Always', label: 'Condividi sempre' },
        { value: 'Never', label: 'Non condividere' }
    ];

    var PROVIDER_NAMES = { Tmdb: 'TMDB', Tvdb: 'TheTVDB', AniList: 'AniList' };

    function describeIds(ids) {
        return Object.keys(ids || {}).sort().map(function (key) { return (PROVIDER_NAMES[key] || key) + ' ' + ids[key]; }).join(', ');
    }

    /** One line a person can check: what the proposal links to what. */
    function describeMapping(mapping) {
        var kind = val(mapping, 'kind');
        var card = 'scheda AnimeClick ' + val(mapping, 'animeClickId');
        if (kind === 'Season') {
            var number = val(mapping, 'seasonNumber');
            return (number === 0 ? 'Speciali' : 'Stagione ' + number) + ' (' + AC.util.plural(val(mapping, 'episodeCount'), 'episodio', 'episodi')
                + ') della serie ' + describeIds(val(mapping, 'series')) + ' → ' + card;
        }
        return (kind === 'Movie' ? 'Film ' : 'Serie ') + describeIds(val(mapping, 'providerIds')) + ' → ' + card;
    }

    var PROPOSAL_STATES = {
        Queued: ['In partenza', 'warn', 'clock'],
        Sent: ['In revisione', '', 'eye'],
        Approved: ['Approvata', 'ok', 'check'],
        NotAccepted: ['Non accettata', 'bad', 'x'],
        Failed: ['Non inviata', 'bad', 'alert']
    };

    function proposalBadge(state) {
        var meta = PROPOSAL_STATES[state] || PROPOSAL_STATES.Queued;
        return ui.badge(meta[0], meta[1], meta[2]);
    }

    /**
     * Shown under a successful correction when the choice is «Chiedimi ogni volta». Nothing leaves the
     * server until «Condividi» is pressed; «Non chiedere più» switches the choice to «Non condividere».
     */
    function renderShareOffer(host, itemId, proposal) {
        var out = ui.liveStatus('acShareOfferResult');
        var share = ui.button('Condividi', { id: 'acShareOffer', variant: 'primary', icon: 'share', small: true });
        var later = ui.button('Non ora', { id: 'acShareLater', variant: 'ghost', small: true });
        var never = ui.button('Non chiedere più', { id: 'acShareNever', variant: 'ghost', small: true });
        var box = h('div', { id: 'acShareBox', class: 'ac-callout is-info ac-share-offer' }, icon('users'),
            h('div', { class: 'ac-stack-s' },
                h('strong', { text: 'Questa correzione può aiutare altri utenti' }),
                h('p', { text: 'Condividi l’abbinamento con la comunità: dopo i controlli e una revisione arriverà a chi ha la stessa opera.' }),
                h('p', { class: 'ac-hint', id: 'acShareSummary', text: describeMapping(proposal) }),
                ui.details('Cosa viene inviato', h('pre', { class: 'ac-code', text: JSON.stringify(proposal, null, 2) })),
                h('div', { class: 'ac-row' }, share, later, never),
                out));
        host.appendChild(box);
        share.addEventListener('click', function () {
            ui.setBusy(share, true, 'Invio…');
            api.request('POST', 'Plugins/AnimeClick/Community/Share', { itemId: itemId }).then(function (result) {
                replace(box, icon('check'), h('div', null, h('strong', { text: 'Grazie!' }), h('p', { text: val(result, 'message') || 'Proposta in coda.' })));
                box.className = 'ac-callout is-ok';
                AC.bus.emit('community-changed');
            }).catch(function (error) {
                ui.status(out, truncate(error.message, 240), 'bad');
                ui.setBusy(share, false);
            });
        });
        later.addEventListener('click', function () { box.remove(); });
        never.addEventListener('click', function () {
            ui.setBusy(never, true, 'Salvataggio…');
            savePatch({ CommunitySharingMode: 'Never' }).then(function () {
                box.remove();
                ui.toast('Non te lo chiederò più. Puoi cambiare idea in Comunità.', 'success');
            }).catch(function (error) {
                ui.status(out, truncate(error.message, 240), 'bad');
                ui.setBusy(never, false);
            });
        });
    }

    function buildCommunity(view) {
        view.appendChild(ui.sectionHead('コミュニティ', 'Comunità', 'Abbinamenti condivisi',
            'Quando correggi un abbinamento puoi aiutare tutti gli altri a riconoscere la stessa opera, senza account e senza condividere la tua libreria.'));

        var how = ui.card({ icon: 'sparkles', title: 'Come funziona' });
        var step = function (number, title, text) {
            return h('li', { class: 'ac-how-step' }, h('span', { class: 'ac-step-num', text: String(number) }),
                h('div', null, h('strong', { text: title }), h('span', { class: 'ac-hint', text: text })));
        };
        how.body.appendChild(h('ol', { class: 'ac-how' },
            step(1, 'Correggi', 'In Strumenti scegli la scheda giusta per una serie, un film o una stagione.'),
            step(2, 'Condividi', 'Il plugin invia solo gli ID pubblici, dopo il tuo consenso.'),
            step(3, 'Revisione', 'Controlli automatici e un clic del curatore del plugin.'),
            step(4, 'Tutti', 'Chi ha la stessa opera riceve l’abbinamento giusto, al massimo entro un giorno.')));

        var choices = ui.card({ icon: 'users', title: 'Le tue scelte', text: 'Due scelte indipendenti: ricevere gli abbinamenti e contribuire.' });
        choices.body.appendChild(ui.switches([
            ui.switchRow('acEnableCommunityMappings', 'Usa gli abbinamenti della comunità', 'Scarica una volta al giorno l’elenco approvato. Vale solo per serie, film e stagioni senza un ID AnimeClick: non cambia mai un abbinamento che hai già.', { key: 'EnableCommunityMappings' })
        ]));
        choices.body.appendChild(ui.select('acCommunitySharingMode', 'Dopo una correzione', SHARING_CHOICES, {
            key: 'CommunitySharingMode', hint: 'Con «Chiedimi ogni volta» non parte nulla finché non premi Condividi.'
        }));
        choices.body.appendChild(ui.callout('Cosa parte, e cosa mai',
            'Tipo, ID AnimeClick e ID TMDB, TheTVDB o AniList; per una stagione anche il suo numero e quanti episodi contiene. Un codice casuale dell’installazione serve solo a limitare gli abusi e non viene pubblicato. Mai titoli, utenti, percorsi, file, indirizzo del server, trame o immagini.', 'info', 'eye'));

        var mine = ui.card({ icon: 'share', title: 'Le tue proposte', text: 'Ogni proposta diventa una segnalazione pubblica nel repository del plugin.' });
        var listHost = h('div', { id: 'acCommunityProposals', class: 'ac-stack-s', 'aria-live': 'polite' });
        var queueOut = ui.liveStatus('acCommunityResult');
        var statusButton = ui.button('Aggiorna', { id: 'acCommunityStatus', icon: 'refresh', variant: 'ghost', small: true });
        var retryButton = ui.button('Riprova invii falliti', { id: 'acCommunityRetry', icon: 'undo', variant: 'ghost', small: true });
        mine.body.appendChild(listHost);
        mine.body.appendChild(h('div', { class: 'ac-row' }, statusButton, retryButton));
        mine.body.appendChild(queueOut);

        function showStatus(report) {
            var pending = val(report, 'pending') || 0, failed = val(report, 'failed') || 0;
            ui.status(queueOut, (pending || failed ? pending + ' in partenza · ' + failed + ' non inviate. ' : '') + (val(report, 'message') || ''),
                failed ? 'bad' : null);
        }

        function loadProposals() {
            replace(listHost, ui.skeleton(48));
            return Promise.all([
                api.request('GET', 'Plugins/AnimeClick/Community/Proposals'),
                api.request('GET', 'Plugins/AnimeClick/Community/Status')
            ]).then(function (results) {
                var rows = list(results[0]);
                if (!rows.length) {
                    replace(listHost, h('p', { class: 'ac-muted', text: 'Nessuna proposta per ora. Quando correggi un abbinamento in Strumenti, qui vedrai la sua revisione.' }));
                } else {
                    replace(listHost, h('ul', { class: 'ac-list' }, rows.map(function (row) {
                        var url = val(row, 'url');
                        return h('li', { class: 'ac-list-item' },
                            h('div', null,
                                h('strong', { text: describeMapping(val(row, 'mapping')) }),
                                url && /^https:\/\/github\.com\/iCosiSenpai\/jellyfin-plugin-animeclick\/issues\/\d+$/.test(url)
                                    ? h('a', { href: url, target: '_blank', rel: 'noopener noreferrer', class: 'ac-hint', text: 'Segnalazione #' + val(row, 'issue') })
                                    : null),
                            proposalBadge(val(row, 'state')));
                    })));
                }
                showStatus(results[1]);
            }).catch(function (error) {
                replace(listHost, h('p', { class: 'ac-status is-bad', text: truncate(error.message, 240) }));
            });
        }

        statusButton.addEventListener('click', function () {
            ui.setBusy(statusButton, true, 'Aggiornamento…');
            loadProposals().finally(function () { ui.setBusy(statusButton, false); });
        });
        retryButton.addEventListener('click', function () {
            ui.setBusy(retryButton, true, 'Riprovo…');
            api.request('POST', 'Plugins/AnimeClick/Community/Retry').then(function () { return loadProposals(); })
                .catch(function (error) { ui.status(queueOut, truncate(error.message, 240), 'bad'); })
                .finally(function () { ui.setBusy(retryButton, false); });
        });
        AC.bus.on('community-changed', function () { if (!view.hidden) loadProposals(); });
        AC.bus.on('view', function (name) { if (name === 'community') loadProposals(); });

        var previewButton = ui.button('Mostra cosa invierebbe il titolo scelto', { id: 'acCommunityPreview', icon: 'eye', variant: 'ghost', small: true });
        var previewOut = h('pre', { id: 'acCommunityPreviewData', class: 'ac-code', hidden: true });
        previewButton.addEventListener('click', function () {
            var id = el('acItemId').value.trim();
            previewOut.hidden = false;
            if (!id) { previewOut.textContent = 'Scegli prima un titolo in Strumenti → Correggi un abbinamento.'; return; }
            ui.setBusy(previewButton, true, 'Caricamento…');
            api.request('GET', 'Plugins/AnimeClick/Community/Preview?itemId=' + encodeURIComponent(id)).then(function (mapping) {
                previewOut.textContent = describeMapping(mapping) + '\n\n' + JSON.stringify(mapping, null, 2);
            }).catch(function (error) { previewOut.textContent = truncate(error.message, 240); })
                .finally(function () { ui.setBusy(previewButton, false); });
        });
        var exportButton = ui.button('Esporta i collegamenti della libreria', { id: 'acCommunityExport', icon: 'download', variant: 'ghost', small: true });
        exportButton.addEventListener('click', function () {
            ui.setBusy(exportButton, true, 'Esportazione…');
            api.request('GET', 'Plugins/AnimeClick/Community/Export').then(function (dataset) {
                downloadJson('animeclick-abbinamenti.json', dataset);
                ui.toast('File scaricato: nulla è stato pubblicato.', 'success');
            }).catch(function (error) { ui.toast('Esportazione non riuscita: ' + truncate(error.message, 200), 'error'); })
                .finally(function () { ui.setBusy(exportButton, false); });
        });

        var advanced = ui.details('Avanzate: invio con il tuo account GitHub, anteprima ed esportazione', [
            ui.field('acCommunityGitHubToken', 'Token GitHub (facoltativo)', {
                secret: true, spellcheck: false,
                html: 'Non serve: senza token le proposte passano dal servizio della comunità. Con un token che possa creare issue in <a href="https://github.com/iCosiSenpai/jellyfin-plugin-animeclick" target="_blank" rel="noopener noreferrer">questo repository</a> le proposte partono a tuo nome. Resta nella configurazione del server e va solo ad api.github.com.'
            }),
            ui.check('acClearCommunityToken', 'Rimuovi il token salvato', 'Viene rimosso quando salvi.'),
            h('div', { class: 'ac-row' }, previewButton, exportButton),
            previewOut,
            ui.hint('L’esportazione contiene solo gli ID pubblici dei titoli già collegati e resta sul tuo dispositivo.')
        ]);

        view.appendChild(how.card);
        view.appendChild(h('div', { class: 'ac-cols-2' }, choices.card, mine.card));
        view.appendChild(advanced);
    }

    /* ===== save bar ===== */

    function buildSaveBar() {
        var discard = ui.button('Annulla', { id: 'acBtnDiscard', variant: 'ghost', icon: 'undo' });
        var store = ui.button('Salva', { id: 'acBtnSave', variant: 'primary', icon: 'save' });
        var bar = h('div', { id: 'acSaveBar', class: 'ac-savebar', role: 'region', 'aria-label': 'Modifiche da salvare', hidden: true },
            h('span', { role: 'status' }, h('span', { class: 'ac-dot' }), 'Hai modifiche non salvate'), discard, store);
        store.addEventListener('click', function () {
            ui.setBusy(store, true, 'Salvataggio…');
            save().catch(function (error) {
                if (!error.reported) ui.toast('Salvataggio non riuscito: ' + truncate(error.message, 240), 'error');
            }).finally(function () { ui.setBusy(store, false); });
        });
        discard.addEventListener('click', function () { if (saved) loadForm(saved); });
        return bar;
    }

    function wire() {
        var sourceFields = {
            acTmdbApiKey: 'tmdb',
            acFanartPersonalApiKey: 'fanart', acFanartProjectApiKey: 'fanart',
            acTvdbApiKey: 'tvdb', acEnableTvdbSynopsis: 'tvdb',
            acAiProvider: 'ai', acAiModel: 'ai', acAiApiKey: 'ai', acAiEndpoint: 'ai', acClearAiKey: 'ai', acEnableAiTranslation: 'ai'
        };
        function onEdit(event) {
            var target = event && event.target;
            var source = target && sourceFields[target.id];
            if (source) delete verified[source];
            // The problem shown under a field belongs to the value that was there.
            if (target && target.getAttribute('aria-invalid')) ui.fieldError(target, '');
            markDirty();
        }
        bound().forEach(function (control) {
            control.addEventListener('input', onEdit);
            control.addEventListener('change', onEdit);
        });
        ['acAiApiKey', 'acAiEndpoint', 'acCommunityGitHubToken'].forEach(function (id) {
            el(id).addEventListener('input', onEdit);
        });
        ['acClearAiKey', 'acClearCommunityToken'].forEach(function (id) {
            el(id).addEventListener('change', onEdit);
        });
        el('acAiEndpoint').addEventListener('input', updateProviderHint);
    }

    AC.settings = {
        attach: function (pageElement) { page = pageElement; },
        buildPreferences: buildPreferences,
        buildSources: buildSources,
        buildTools: buildTools,
        buildCommunity: buildCommunity,
        buildSaveBar: buildSaveBar,
        wire: wire,
        load: function (config) { loaded = true; loadForm(config); loadAiProviders(); loadAnimeClickStatus(); },
        unload: function () { loaded = false; saved = null; dirty = false; verified = {}; animeClickStatus = null; cacheStats.at = 0; },
        savePatch: savePatch,
        saved: function () { return saved; },
        isDirty: function () { return dirty; },
        sourceState: sourceState,
        animeClickStatus: function () { return animeClickStatus; },
        animeClickBlocked: animeClickBlocked,
        pointAt: pointAt,
        startIdentify: startIdentify,
        startProbe: startProbe,
        runTest: function (source, payload) {
            return api.request('POST', 'Plugins/AnimeClick/' + TEST_ROUTES[source], payload).then(function (result) {
                if (source === 'animeclick') setAnimeClickStatus(result);
                return { success: testSucceeded(source, result), message: describeTest(source, result, testSucceeded(source, result)) };
            });
        }
    };
}());
