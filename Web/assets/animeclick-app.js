/* AnimeClick per Jellyfin — struttura della pagina, navigazione e ciclo di vita. */
(function () {
    'use strict';

    var AC = window.AnimeClickUI;
    var h = AC.dom.h, icon = AC.dom.icon, clear = AC.dom.clear;
    var truncate = AC.util.truncate;
    var ui = AC.ui, api = AC.api;

    var VIEWS = [
        { id: 'home', label: 'Inizio', icon: 'home' },
        { id: 'library', label: 'Libreria', icon: 'library' },
        { id: 'preferences', label: 'Preferenze', icon: 'sliders' },
        { id: 'sources', label: 'Fonti', icon: 'plug' },
        { id: 'tools', label: 'Strumenti', icon: 'wrench' },
        { id: 'community', label: 'Comunità', icon: 'users' }
    ];

    var page = null;
    var current = 'home';
    var loaded = false;
    var sequence = 0;
    var setupOpen = false;

    function el(id) {
        return page ? page.querySelector('#' + id) : null;
    }

    function build(root) {
        clear(root);
        var tabs = VIEWS.map(function (view) {
            return h('button', {
                type: 'button', class: 'ac-tab', id: 'acTab_' + view.id, role: 'tab', 'aria-controls': 'acView_' + view.id,
                'aria-selected': view.id === current ? 'true' : 'false', tabIndex: view.id === current ? 0 : -1, data: { view: view.id }
            }, icon(view.icon), view.label);
        });
        var live = h('button', { type: 'button', id: 'acLive', class: 'ac-live', hidden: true, on: { click: function () { go('library'); } } }, 'Attività in corso');
        root.appendChild(h('header', { class: 'ac-top' },
            h('div', { class: 'ac-brand' },
                h('img', { class: 'ac-brand-mark', src: api.asset('AnimeClickMascotSmall'), alt: '' }),
                h('div', { class: 'ac-wordmark' }, h('strong', null, 'Anime', h('span', { text: 'Click' })), h('small', { text: 'I tuoi anime, in italiano' }))),
            live,
            h('span', { class: 'ac-version', id: 'acVersion', text: 'v' + AC.version }),
            h('nav', { class: 'ac-nav', id: 'acTabs', role: 'tablist', 'aria-label': 'Sezioni di AnimeClick' }, tabs)));
        root.appendChild(h('div', { id: 'acLoadState', class: 'ac-loadstate', role: 'status', 'aria-live': 'polite' }, h('span', { class: 'ac-spinner' }), 'Caricamento delle impostazioni…'));
        root.appendChild(h('div', { id: 'acSetup', class: 'ac-setup', hidden: true }));

        var main = h('main', { class: 'ac-main', 'aria-busy': 'true' });
        VIEWS.forEach(function (view) {
            main.appendChild(h('section', {
                class: 'ac-view', id: 'acView_' + view.id, role: 'tabpanel', 'aria-labelledby': 'acTab_' + view.id,
                data: { view: view.id }, hidden: view.id !== current
            }));
        });
        root.appendChild(main);
        root.appendChild(AC.settings.buildSaveBar());
        root.appendChild(h('footer', { class: 'ac-foot' },
            h('span', null, 'Fatto con ', h('span', { 'aria-label': 'amore', text: '♥' }), ' per chi ama gli anime · Dati da AnimeClick.it'),
            h('a', { href: 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick#readme', target: '_blank', rel: 'noopener noreferrer', text: 'Guida' }),
            h('a', { href: 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues', target: '_blank', rel: 'noopener noreferrer', text: 'Segnala un problema' }),
            h('a', { href: 'https://buymeacoffee.com/iCosiSenpai', target: '_blank', rel: 'noopener noreferrer', text: 'Offrimi un caffè' }),
            h('a', { href: 'https://www.paypal.com/donate/?hosted_button_id=5A4E26XC45GLQ', target: '_blank', rel: 'noopener noreferrer', text: 'PayPal' })));

        AC.settings.attach(page);
        AC.library.attach(page);
        AC.home.attach(page);
        AC.setup.attach(page);
        AC.home.build(el('acView_home'));
        AC.library.build(el('acView_library'));
        AC.settings.buildPreferences(el('acView_preferences'));
        AC.settings.buildSources(el('acView_sources'));
        AC.settings.buildTools(el('acView_tools'));
        AC.settings.buildCommunity(el('acView_community'));
        AC.settings.wire();

        tabs.forEach(function (tab, index) {
            tab.addEventListener('click', function () { go(tab.dataset.view); });
            tab.addEventListener('keydown', function (event) {
                var next = null;
                if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % tabs.length;
                if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index - 1 + tabs.length) % tabs.length;
                if (event.key === 'Home') next = 0;
                if (event.key === 'End') next = tabs.length - 1;
                if (next == null) return;
                event.preventDefault();
                go(tabs[next].dataset.view, true);
            });
        });

        AC.bus.on('jobs', function () {
            var active = AC.library.isActive('titles') || AC.library.isActive('synopses');
            var badge = el('acLive');
            if (badge) badge.hidden = !active;
            var tab = el('acTab_library');
            var dot = tab && tab.querySelector('.ac-dot');
            if (active && !dot) tab.appendChild(h('span', { class: 'ac-dot', 'aria-hidden': 'true' }));
            if (!active && dot) dot.remove();
        });
    }

    function go(view, focusTab) {
        if (!VIEWS.some(function (item) { return item.id === view; })) view = 'home';
        var previous = current;
        current = view;
        VIEWS.forEach(function (item) {
            var tab = el('acTab_' + item.id);
            var panel = el('acView_' + item.id);
            var selected = item.id === view;
            if (tab) {
                tab.setAttribute('aria-selected', selected ? 'true' : 'false');
                tab.tabIndex = selected ? 0 : -1;
            }
            if (panel) panel.hidden = !selected;
        });
        if (focusTab && el('acTab_' + view)) el('acTab_' + view).focus();
        if (!loaded || setupOpen) return;
        if (previous === 'home' && view !== 'home') AC.home.leave();
        if (view === 'home') AC.home.enter();
        else if (view === 'library') AC.library.enter();
        else AC.library.stop();
    }

    function showSetup(open) {
        setupOpen = open;
        el('acSetup').hidden = !open;
        el('acTabs').hidden = open;
        page.querySelector('.ac-main').hidden = open;
        if (open) {
            el('acSaveBar').hidden = true;
            AC.library.stop();
            AC.home.leave();
        }
    }

    function show(pageElement) {
        if (page === pageElement && loaded) {
            if (!setupOpen) go(current);
            return;
        }
        page = pageElement;
        var root = page.querySelector('#acRoot');
        var mySequence = ++sequence;
        loaded = false;
        AC.settings.unload();
        AC.library.reset();
        AC.home.reset();
        if (root.getAttribute('data-ac-built') !== AC.version) {
            build(root);
            root.setAttribute('data-ac-built', AC.version);
        }
        var main = page.querySelector('.ac-main');
        var state = el('acLoadState');
        main.inert = true;
        main.setAttribute('aria-busy', 'true');
        state.hidden = false;
        state.className = 'ac-loadstate';
        state.setAttribute('role', 'status');
        clear(state).appendChild(h('span', { class: 'ac-spinner' }));
        state.appendChild(document.createTextNode('Caricamento delle impostazioni…'));

        api.getConfig().then(function (config) {
            if (mySequence !== sequence) return;
            loaded = true;
            AC.settings.load(config);
            main.inert = false;
            main.setAttribute('aria-busy', 'false');
            state.hidden = true;
            var setup = AC.setup.needed(config);
            if (setup) {
                AC.setup.open(setup);
            } else {
                showSetup(false);
                go(current);
            }
        }).catch(function (error) {
            if (mySequence !== sequence) return;
            main.setAttribute('aria-busy', 'false');
            state.className = 'ac-loadstate is-bad';
            state.setAttribute('role', 'alert');
            clear(state).appendChild(icon('alert'));
            state.appendChild(h('span', { class: 'ac-grow', text: 'Non riesco a caricare le impostazioni. ' + truncate(error.message, 160) }));
            state.appendChild(ui.button('Riprova', { small: true, icon: 'refresh', onClick: function () { page = null; show(pageElement); } }));
        });
    }

    document.addEventListener('visibilitychange', function () {
        if (!loaded || !page || !page.isConnected) return;
        if (!document.hidden && (current === 'home' || current === 'library') && !setupOpen) AC.library.poll();
        else AC.library.stop();
    });

    window.addEventListener('beforeunload', function (event) {
        if (!AC.settings.isDirty()) return;
        event.preventDefault();
        event.returnValue = '';
    });

    AC.app = {
        show: show,
        go: go,
        current: function () { return current; },
        showSetup: showSetup,
        pageHidden: function () { AC.library.stop(); AC.home.leave(); }
    };
}());
