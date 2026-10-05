/* AnimeClick per Jellyfin — fondamenta: DOM, icone, API, feedback e componenti condivisi. */
(function () {
    'use strict';

    var AC = window.AnimeClickUI = window.AnimeClickUI || {};
    AC.version = '1.6.0.0';
    AC.pluginId = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';

    /* ===== DOM ===== */

    var PROPERTIES = {
        id: 1, type: 1, value: 1, checked: 1, disabled: 1, hidden: 1, htmlFor: 1, tabIndex: 1, title: 1,
        src: 1, alt: 1, href: 1, target: 1, rel: 1, download: 1, name: 1, placeholder: 1, min: 1, max: 1,
        step: 1, rows: 1, maxLength: 1, autocomplete: 1, spellcheck: 1, open: 1, loading: 1, decoding: 1
    };

    function append(node, children) {
        children.forEach(function (child) {
            if (child == null || child === false) return;
            if (Array.isArray(child)) { append(node, child); return; }
            node.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
        });
        return node;
    }

    /** h('div', { class: 'x', on: { click: fn } }, child, 'testo', [altri]) */
    function h(tag, props) {
        var node = document.createElement(tag);
        Object.keys(props || {}).forEach(function (key) {
            var value = props[key];
            if (value == null || value === false) return;
            if (key === 'class') node.className = value;
            else if (key === 'text') node.textContent = value;
            else if (key === 'on') Object.keys(value).forEach(function (name) { node.addEventListener(name, value[name]); });
            else if (key === 'data') Object.keys(value).forEach(function (name) { node.dataset[name] = value[name]; });
            else if (key === 'style') node.style.cssText = value;
            else if (PROPERTIES[key]) node[key] = value;
            else node.setAttribute(key, value === true ? '' : value);
        });
        return append(node, Array.prototype.slice.call(arguments, 2));
    }

    function clear(node) {
        while (node && node.firstChild) node.removeChild(node.firstChild);
        return node;
    }

    function replace(node) {
        clear(node);
        return append(node, Array.prototype.slice.call(arguments, 1));
    }

    /* Trusted, static markup only: every path below is a constant of this file. */
    var ICONS = {
        home: '<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M10 21v-6h4v6"/>',
        library: '<rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="9" rx="1.5"/><rect x="3" y="15" width="7" height="6" rx="1.5"/><rect x="14" y="15" width="7" height="6" rx="1.5"/>',
        sliders: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
        plug: '<path d="M9 7V3M15 7V3"/><path d="M6 7h12v4a6 6 0 0 1-12 0z"/><path d="M12 17v4"/>',
        wrench: '<path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/>',
        users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
        refresh: '<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>',
        sparkles: '<path d="M12 3l1.9 5.6L19.5 10l-5.6 1.9L12 17.5l-1.9-5.6L4.5 10l5.6-1.4z"/><path d="M19 3v4M17 5h4M5 17v4M3 19h4"/>',
        check: '<path d="M20 6 9 17l-5-5"/>',
        x: '<path d="M18 6 6 18M6 6l12 12"/>',
        alert: '<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>',
        info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
        external: '<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
        search: '<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>',
        play: '<path d="M7 4.5v15l12.5-7.5z"/>',
        stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
        chevronRight: '<path d="m9 18 6-6-6-6"/>',
        chevronLeft: '<path d="m15 18-6-6 6-6"/>',
        arrowRight: '<path d="M5 12h14M13 5l7 7-7 7"/>',
        key: '<circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6M15.5 7.5l3 3L22 7l-3-3"/>',
        image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.1-3.1a2 2 0 0 0-2.8 0L6 21"/>',
        translate: '<path d="m5 8 6 6M4 14l6-6 2-3M2 5h12M7 2h1M22 22l-5-10-5 10M14 18h6"/>',
        clock: '<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>',
        tv: '<rect x="2" y="7" width="20" height="15" rx="2"/><path d="m17 2-5 5-5-5"/>',
        film: '<rect x="2" y="2" width="20" height="20" rx="2.2"/><path d="M7 2v20M17 2v20M2 12h20M2 7h5M2 17h5M17 17h5M17 7h5"/>',
        title: '<path d="M4 7V4h16v3M9 20h6M12 4v16"/>',
        book: '<path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/><path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>',
        eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
        eyeOff: '<path d="M9.9 4.2A10 10 0 0 1 12 4c6.4 0 10 8 10 8a17 17 0 0 1-2.2 3.2M6.6 6.6A17 17 0 0 0 2 12s3.6 8 10 8a9.7 9.7 0 0 0 5.4-1.6M2 2l20 20"/><path d="M14.1 14.1a3 3 0 1 1-4.2-4.2"/>',
        trash: '<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/>',
        download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>',
        share: '<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="m8.6 13.5 6.8 4M15.4 6.5l-6.8 4"/>',
        link: '<path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7"/><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7"/>',
        folder: '<path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13c0 1.1.9 2 2 2z"/>',
        heart: '<path d="M19 14c1.5-1.5 3-3.2 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.8 0-3 .5-4.5 2-1.5-1.5-2.7-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4 3 5.5l7 7z"/>',
        cat: '<path d="M5 3.5 8.5 8h7L19 3.5V13a7 7 0 0 1-14 0z"/><path d="M9.5 12.5h.01M14.5 12.5h.01M10.5 16 12 17l1.5-1"/>',
        checklist: '<path d="m3 17 2 2 4-4M3 7l2 2 4-4M13 6h8M13 12h8M13 18h8"/>',
        activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
        database: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
        globe: '<circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15 15 0 0 1 0 20M12 2a15 15 0 0 0 0 20"/>',
        lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
        undo: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/>',
        flask: '<path d="M9 3h6M10 3v6L4.5 19a1.5 1.5 0 0 0 1.3 2h12.4a1.5 1.5 0 0 0 1.3-2L14 9V3"/><path d="M7 15h10"/>',
        save: '<path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><path d="M17 21v-8H7v8M7 3v5h8"/>',
        layers: '<path d="m12 2 10 5-10 5L2 7z"/><path d="m2 17 10 5 10-5M2 12l10 5 10-5"/>'
    };

    function icon(name, cls) {
        var span = document.createElement('span');
        span.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">'
            + (ICONS[name] || ICONS.info) + '</svg>';
        var svg = span.firstChild;
        if (cls) svg.setAttribute('class', cls);
        return svg;
    }

    /* ===== data helpers ===== */

    /** Reads a property whatever its casing: Jellyfin answers in PascalCase, the plugin in camelCase. */
    function val(obj, name) {
        if (!obj) return undefined;
        if (obj[name] !== undefined) return obj[name];
        return obj[name.charAt(0).toUpperCase() + name.slice(1)];
    }

    function list(value) {
        return Array.isArray(value) ? value : [];
    }

    function num(value) {
        return Number(value || 0).toLocaleString('it-IT');
    }

    function pct(part, total) {
        return total > 0 ? Math.round(Math.max(0, Math.min(1, part / total)) * 100) : 0;
    }

    function truncate(value, max) {
        var text = String(value == null ? '' : value);
        return text.length > max ? text.slice(0, max - 1) + '…' : text;
    }

    function plural(count, one, many) {
        return num(count) + ' ' + (count === 1 ? one : many);
    }

    function relTime(value) {
        if (!value) return '';
        var date = new Date(value);
        var seconds = (Date.now() - date.getTime()) / 1000;
        if (!isFinite(seconds)) return '';
        if (seconds < 60) return 'adesso';
        var minutes = Math.round(seconds / 60);
        if (minutes < 60) return minutes === 1 ? '1 minuto fa' : minutes + ' minuti fa';
        var hours = Math.round(minutes / 60);
        if (hours < 24) return hours === 1 ? '1 ora fa' : hours + ' ore fa';
        var days = Math.round(hours / 24);
        if (days < 30) return days === 1 ? 'ieri' : days + ' giorni fa';
        return date.toLocaleDateString('it-IT', { day: 'numeric', month: 'short', year: 'numeric' });
    }

    function normalize(value) {
        return String(value == null ? '' : value).trim().toLowerCase()
            .normalize('NFD').replace(/[̀-ͯ]/g, '');
    }

    /* ===== events ===== */

    var listeners = Object.create(null);
    AC.bus = {
        on: function (name, handler) { (listeners[name] = listeners[name] || []).push(handler); },
        off: function (name, handler) { listeners[name] = (listeners[name] || []).filter(function (h) { return h !== handler; }); },
        emit: function (name, payload) { (listeners[name] || []).slice().forEach(function (handler) { handler(payload); }); }
    };

    /* ===== authenticated API ===== */

    function authHeader() {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
                var token = ApiClient.accessToken();
                if (token) return 'MediaBrowser Token="' + token + '"';
            }
        } catch (error) {
            // ApiClient may not be ready yet; same-origin credentials still apply.
        }
        return null;
    }

    function apiUrl(path) {
        try {
            if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function') return ApiClient.getUrl(path);
        } catch (error) {
            // Fall back to a relative server URL.
        }
        return path;
    }

    function request(method, path, body, timeoutMilliseconds) {
        var headers = { Accept: 'application/json' };
        var auth = authHeader();
        if (auth) headers.Authorization = auth;
        var controller = new AbortController();
        var options = { method: method, credentials: 'same-origin', headers: headers, signal: controller.signal };
        var timeout = setTimeout(function () { controller.abort(); }, timeoutMilliseconds || 130000);
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }
        return fetch(apiUrl(path), options).then(function (response) {
            var isJson = (response.headers.get('Content-Type') || '').toLowerCase().indexOf('json') > -1;
            if (!response.ok) {
                return (isJson ? response.json() : response.text()).catch(function () { return null; }).then(function (payload) {
                    var message = payload && typeof payload === 'object' ? (val(payload, 'error') || val(payload, 'message') || val(payload, 'title')) : null;
                    if (!message && typeof payload === 'string') message = payload;
                    var error = new Error(message || ('Il server ha risposto con l’errore ' + response.status + '.'));
                    error.status = response.status;
                    throw error;
                });
            }
            if (response.status === 204 || !isJson) return null;
            return response.json();
        }).catch(function (error) {
            if (error.name === 'AbortError') throw new Error('Il server non ha risposto in tempo. Puoi riprovare.');
            throw error;
        }).finally(function () { clearTimeout(timeout); });
    }

    function wrap(call) {
        return new Promise(function (resolve, reject) {
            try { call().then(resolve, reject); } catch (error) { reject(error); }
        });
    }

    function imageUrl(itemId, type, params) {
        var query = Object.keys(params || {}).map(function (key) {
            return encodeURIComponent(key) + '=' + encodeURIComponent(params[key]);
        }).join('&');
        return apiUrl('Items/' + encodeURIComponent(itemId) + '/Images/' + type + (query ? '?' + query : ''));
    }

    function itemUrl(itemId) {
        var serverId = '';
        try { serverId = typeof ApiClient !== 'undefined' && ApiClient.serverId ? ApiClient.serverId() : ''; } catch (error) { serverId = ''; }
        return '#/details?id=' + encodeURIComponent(itemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
    }

    AC.api = {
        request: request,
        url: apiUrl,
        image: imageUrl,
        itemUrl: itemUrl,
        getConfig: function () { return wrap(function () { return ApiClient.getPluginConfiguration(AC.pluginId); }); },
        putConfig: function (config) { return wrap(function () { return ApiClient.updatePluginConfiguration(AC.pluginId, config); }); },
        asset: function (name) { return 'configurationpage?name=' + name + '&v=' + AC.version; }
    };

    /* ===== feedback ===== */

    var toastHost = null;

    function toast(message, tone) {
        if (!toastHost || !document.body.contains(toastHost)) {
            toastHost = h('div', { class: 'ac-layer ac-toasts' });
            document.body.appendChild(toastHost);
        }
        var bad = tone === 'error';
        var item = h('div', {
            class: 'ac-toast' + (bad ? ' is-bad' : tone === 'success' ? ' is-ok' : ''),
            role: bad ? 'alert' : 'status',
            'aria-live': bad ? 'assertive' : 'polite',
            'aria-atomic': 'true'
        }, icon(bad ? 'alert' : tone === 'success' ? 'check' : 'info'), h('span', { text: message }));
        toastHost.appendChild(item);
        setTimeout(function () {
            item.classList.add('is-leaving');
            setTimeout(function () { item.remove(); }, 220);
        }, bad ? 6000 : 3600);
    }

    var openModal = null;

    /** Resolves true on confirm. Focus is trapped inside and returns to the trigger afterwards. */
    function confirmDialog(options) {
        if (openModal) {
            openModal.focus();
            return Promise.resolve(false);
        }
        return new Promise(function (resolve) {
            var trigger = document.activeElement;
            var id = 'acDialog' + Math.random().toString(36).slice(2, 8);
            var cancel = h('button', { type: 'button', class: 'ac-btn ac-btn--ghost', text: options.cancelLabel || 'Annulla' });
            var confirm = h('button', {
                type: 'button',
                class: 'ac-btn ' + (options.tone === 'danger' ? 'ac-btn--danger' : 'ac-btn--primary'),
                text: options.confirmLabel || 'Conferma'
            });
            var body = h('div', { id: id + 'Body', class: 'ac-stack-s' },
                (Array.isArray(options.message) ? options.message : [options.message]).map(function (line) {
                    return h('p', { text: line });
                }));
            var modal = h('div', {
                class: 'ac-modal', role: 'dialog', 'aria-modal': 'true',
                'aria-labelledby': id + 'Title', 'aria-describedby': id + 'Body', tabIndex: -1
            }, h('h3', { id: id + 'Title', text: options.title }), body, h('div', { class: 'ac-row' }, cancel, confirm));
            var veil = h('div', { class: 'ac-layer ac-veil' }, modal);
            var app = document.querySelector('.ac-app');
            var settled = false;

            function finish(value) {
                if (settled) return;
                settled = true;
                veil.remove();
                if (app) app.inert = false;
                openModal = null;
                if (trigger && typeof trigger.focus === 'function' && document.body.contains(trigger)) trigger.focus();
                resolve(value);
            }

            veil.addEventListener('keydown', function (event) {
                if (event.key === 'Escape') { event.preventDefault(); finish(false); return; }
                if (event.key !== 'Tab') return;
                if (event.shiftKey && document.activeElement === cancel) { event.preventDefault(); confirm.focus(); }
                else if (!event.shiftKey && document.activeElement === confirm) { event.preventDefault(); cancel.focus(); }
            });
            veil.addEventListener('click', function (event) { if (event.target === veil) finish(false); });
            cancel.addEventListener('click', function () { finish(false); });
            confirm.addEventListener('click', function () { finish(true); });
            document.body.appendChild(veil);
            if (app && !document.querySelector('.ac-drawer-veil')) app.inert = true;
            openModal = cancel;
            cancel.focus();
        });
    }

    /** Side panel for details. Returns { body, close }. */
    function drawer(options) {
        var trigger = document.activeElement;
        var close = h('button', { type: 'button', class: 'ac-btn ac-btn--icon ac-drawer-close', 'aria-label': 'Chiudi' }, icon('x'));
        var head = h('div', { class: 'ac-drawer-head' });
        var body = h('div', { class: 'ac-drawer-body' });
        var panel = h('div', {
            class: 'ac-drawer', role: 'dialog', 'aria-modal': 'true', 'aria-label': options.label || 'Dettagli', tabIndex: -1
        }, close, head, body);
        var veil = h('div', { class: 'ac-layer ac-drawer-veil' }, panel);
        var app = document.querySelector('.ac-app');
        var closed = false;

        function dismiss() {
            if (closed) return;
            closed = true;
            veil.remove();
            document.removeEventListener('keydown', onKey, true);
            if (app) app.inert = false;
            if (options.onClose) options.onClose();
            if (trigger && typeof trigger.focus === 'function' && document.body.contains(trigger)) trigger.focus();
        }

        function onKey(event) {
            if (event.key === 'Escape' && !openModal) { event.preventDefault(); dismiss(); }
        }

        close.addEventListener('click', dismiss);
        veil.addEventListener('click', function (event) { if (event.target === veil) dismiss(); });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(veil);
        if (app) app.inert = true;
        panel.focus();
        return { head: head, body: body, close: dismiss, panel: panel, isOpen: function () { return !closed; } };
    }

    /* ===== components ===== */

    /** Disables a button while work is in flight; its icon and label come back afterwards. */
    function setBusy(button, busy, busyLabel) {
        if (!button) return;
        if (busy) {
            if (!button._acIdle) button._acIdle = Array.prototype.slice.call(button.childNodes);
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            if (busyLabel) button.textContent = busyLabel;
        } else {
            button.disabled = false;
            button.setAttribute('aria-busy', 'false');
            if (button._acIdle) {
                clear(button);
                append(button, button._acIdle);
                button._acIdle = null;
            }
        }
    }

    function status(node, text, tone) {
        if (!node) return;
        node.className = 'ac-status' + (tone === 'ok' ? ' is-ok' : tone === 'bad' ? ' is-bad' : '');
        node.textContent = text || '';
    }

    function liveStatus(id) {
        return h('p', { id: id, class: 'ac-status', role: 'status', 'aria-live': 'polite' });
    }

    function button(label, options) {
        options = options || {};
        return h('button', {
            type: 'button',
            id: options.id,
            class: 'ac-btn' + (options.variant ? ' ac-btn--' + options.variant : '') + (options.small ? ' ac-btn--sm' : '') + (options.cls ? ' ' + options.cls : ''),
            title: options.title,
            'aria-label': options.ariaLabel,
            disabled: options.disabled,
            on: options.onClick ? { click: options.onClick } : null
        }, options.icon ? icon(options.icon) : null, label);
    }

    function badge(text, tone, iconName) {
        return h('span', { class: 'ac-badge' + (tone ? ' is-' + tone : '') }, iconName ? icon(iconName) : null, text);
    }

    function callout(title, text, tone, iconName) {
        return h('div', { class: 'ac-callout' + (tone ? ' is-' + tone : '') },
            icon(iconName || (tone === 'warn' || tone === 'bad' ? 'alert' : tone === 'ok' ? 'check' : 'info')),
            h('div', null, title ? h('strong', { text: title }) : null, text ? h('p', { text: text }) : null));
    }

    function sectionHead(jp, kicker, title, lead, actions) {
        return h('header', { class: 'ac-section-head' },
            h('div', null,
                h('p', { class: 'ac-jp' }, kicker, h('i', { 'aria-hidden': 'true', text: jp })),
                h('h1', { class: 'ac-h1', text: title }),
                lead ? h('p', { class: 'ac-lead', text: lead }) : null),
            actions ? h('div', { class: 'ac-row' }, actions) : null);
    }

    function card(options) {
        var body = h('div', { class: 'ac-stack' });
        var head = options.title ? h('div', { class: 'ac-card-head' },
            options.icon ? h('span', { class: 'ac-card-icon' }, icon(options.icon)) : null,
            h('div', null,
                h('h2', { class: 'ac-h2', text: options.title }),
                options.text ? h('p', { class: 'ac-hint', style: 'margin-top:4px', text: options.text }) : null),
            options.aside || null) : null;
        var node = h(options.tag || 'section', { class: 'ac-card' + (options.cls ? ' ' + options.cls : ''), id: options.id }, head, body);
        return { card: node, body: body, head: head };
    }

    /** Static hint text may carry links: only constants of this code reach innerHTML. */
    function hint(text, html) {
        var node = h('span', { class: 'ac-hint' });
        if (html) node.innerHTML = html; else node.textContent = text || '';
        return node;
    }

    function switchRow(id, title, description, options) {
        options = options || {};
        var input = h('input', { type: 'checkbox', class: 'ac-switch', id: id, role: 'switch', data: options.key ? { key: options.key } : null });
        return h('label', { class: 'ac-switch-row', htmlFor: id },
            h('div', null, h('strong', { text: title }), description || options.html ? hint(description, options.html) : null),
            input);
    }

    function switches(rows) {
        return h('div', { class: 'ac-switches' }, rows);
    }

    function field(id, label, options) {
        options = options || {};
        var input = h(options.multiline ? 'textarea' : 'input', {
            id: id,
            class: options.multiline ? 'ac-textarea' : 'ac-input',
            type: options.multiline ? null : (options.type || 'text'),
            placeholder: options.placeholder,
            autocomplete: options.autocomplete || 'off',
            spellcheck: options.spellcheck,
            rows: options.rows,
            maxLength: options.maxLength,
            data: options.key ? { key: options.key } : null
        });
        ['min', 'max', 'step', 'list', 'inputmode'].forEach(function (name) {
            if (options[name] != null) input.setAttribute(name, options[name]);
        });
        if (options.spellcheck === false) input.spellcheck = false;
        var control = input;
        if (options.secret) {
            input.type = 'password';
            input.autocomplete = 'new-password';
            var toggle = h('button', { type: 'button', class: 'ac-btn ac-btn--icon ac-btn--ghost', 'aria-label': 'Mostra ' + label.toLowerCase(), 'aria-pressed': 'false' }, icon('eye'));
            toggle.addEventListener('click', function () {
                var reveal = input.type === 'password';
                input.type = reveal ? 'text' : 'password';
                toggle.setAttribute('aria-pressed', reveal ? 'true' : 'false');
                toggle.setAttribute('aria-label', (reveal ? 'Nascondi ' : 'Mostra ') + label.toLowerCase());
                replace(toggle, icon(reveal ? 'eyeOff' : 'eye'));
            });
            control = h('div', { class: 'ac-input-group' }, input, toggle);
        }
        if (options.action) {
            control = h('div', { class: 'ac-input-group' }, control, options.action);
        }
        return h('div', { class: 'ac-field' },
            h('label', { class: 'ac-label', htmlFor: id, text: label }),
            control,
            options.hint || options.html ? hint(options.hint, options.html) : null);
    }

    function select(id, label, choices, options) {
        options = options || {};
        var node = h('select', { id: id, class: 'ac-select', data: options.key ? { key: options.key } : null },
            (choices || []).map(function (choice) { return h('option', { value: choice.value, text: choice.label }); }));
        if (!label) return node;
        return h('div', { class: 'ac-field' },
            h('label', { class: 'ac-label', htmlFor: id, text: label }), node,
            options.hint || options.html ? hint(options.hint, options.html) : null);
    }

    function check(id, label, description) {
        return h('label', { class: 'ac-check', htmlFor: id },
            h('input', { type: 'checkbox', id: id }),
            h('span', null, label, description ? h('small', { text: description }) : null));
    }

    function details(summary, children, open) {
        return h('details', { class: 'ac-details', open: !!open }, h('summary', { text: summary }), h('div', null, children));
    }

    function progress(value, indeterminate) {
        var fill = h('span');
        var node = h('div', { class: 'ac-progress' + (indeterminate ? ' is-indeterminate' : ''), role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100' }, fill);
        setProgress(node, value, indeterminate);
        return node;
    }

    function setProgress(node, value, indeterminate) {
        if (!node) return;
        node.classList.toggle('is-indeterminate', !!indeterminate);
        if (indeterminate) {
            node.removeAttribute('aria-valuenow');
            node.firstChild.style.width = '';
        } else {
            var clamped = Math.max(0, Math.min(100, Math.round(value || 0)));
            node.setAttribute('aria-valuenow', String(clamped));
            node.firstChild.style.width = clamped + '%';
        }
    }

    /** Circular gauge. `part` of `total`; tone follows the percentage unless given. */
    function ring(part, total, options) {
        options = options || {};
        var radius = 42;
        var length = 2 * Math.PI * radius;
        var value = pct(part, total);
        var tone = options.tone || (total === 0 ? 'none' : value >= 95 ? 'ok' : value >= 60 ? 'warn' : '');
        var node = h('div', { class: 'ac-ring' + (tone ? ' is-' + tone : ''), role: 'img', 'aria-label': options.label ? options.label + ': ' + value + '%' : value + '%' });
        node.innerHTML = '<svg viewBox="0 0 100 100" aria-hidden="true"><circle class="ac-ring-track" cx="50" cy="50" r="' + radius + '"/>'
            + '<circle class="ac-ring-value" cx="50" cy="50" r="' + radius + '" stroke-dasharray="' + length.toFixed(2) + '" stroke-dashoffset="' + length.toFixed(2) + '"/></svg>';
        node.appendChild(h('span', { class: 'ac-ring-label', 'aria-hidden': 'true', text: total ? value + '%' : '—' }));
        var arc = node.querySelector('.ac-ring-value');
        requestAnimationFrame(function () {
            requestAnimationFrame(function () { arc.setAttribute('stroke-dashoffset', (length * (1 - value / 100)).toFixed(2)); });
        });
        return node;
    }

    function meterRow(label, part, total) {
        var value = pct(part, total);
        var tone = !total ? 'none' : value >= 100 ? '' : value >= 60 ? 'warn' : 'bad';
        return h('div', { class: 'ac-meter-row' },
            h('span', { text: label }),
            h('span', { class: 'ac-meter' + (tone ? ' is-' + tone : '') }, h('span', { style: 'width:' + value + '%' })),
            h('span', { text: total ? num(part) + '/' + num(total) : '—' }));
    }

    function poster(item, options) {
        options = options || {};
        var id = val(item, 'id');
        var name = val(item, 'name') || 'Senza titolo';
        var node = h('div', { class: 'ac-poster' + (options.cls ? ' ' + options.cls : '') },
            h('span', { class: 'ac-poster-fallback', 'aria-hidden': 'true', text: truncate(name, 48) }));
        var hasImage = val(item, 'hasPrimaryImage');
        if (id && hasImage !== false) {
            var image = h('img', { alt: '', loading: 'lazy', decoding: 'async', src: imageUrl(id, 'Primary', { fillHeight: options.height || 360, quality: 90 }) });
            image.addEventListener('error', function () { image.remove(); });
            node.appendChild(image);
        }
        return node;
    }

    function empty(title, text, action) {
        return h('div', { class: 'ac-empty' },
            h('img', { src: AC.api.asset('AnimeClickMascotSmall'), alt: '' }),
            h('strong', { text: title }),
            text ? h('p', { class: 'ac-hint', text: text }) : null,
            action || null);
    }

    function skeleton(height) {
        return h('div', { class: 'ac-skel', style: 'height:' + (height || 120) + 'px', 'aria-hidden': 'true' });
    }

    AC.dom = { h: h, clear: clear, replace: replace, icon: icon };
    AC.util = {
        val: val, list: list, num: num, pct: pct, truncate: truncate, plural: plural,
        relTime: relTime, normalize: normalize
    };
    AC.ui = {
        toast: toast, confirm: confirmDialog, drawer: drawer, setBusy: setBusy, status: status, liveStatus: liveStatus,
        button: button, badge: badge, callout: callout, sectionHead: sectionHead, card: card, hint: hint,
        switchRow: switchRow, switches: switches, field: field, select: select, check: check, details: details,
        progress: progress, setProgress: setProgress, ring: ring, meterRow: meterRow, poster: poster,
        empty: empty, skeleton: skeleton
    };
}());
