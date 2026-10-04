const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const results = path.join(__dirname, 'test-results');
let browser, server, origin;

const defaults = {
    ConfigurationVersion: 2, SetupCompletedVersion: 2, PreferItalianTitle: true, EnablePlot: true,
    EnableEpisodeTitles: true, EnableEpisodeTitleFallback: true, EnableEpisodeSynopsisTranslation: true,
    EnableAiTranslation: true, MinPosterWidth: 400, MaxSearchResults: 10,
    CacheHours: 48, NegativeCacheHours: 12, RequestDelayMilliseconds: 1000,
    TranslationCacheHours: 87600, EpisodeTranslationTimeoutSec: 90,
    BaseUrl: 'https://www.animeclick.it', UserAgent: 'test',
    AiProvider: 'custom', AiEndpoint: 'https://translation.example/v1/chat/completions',
    AiModel: 'saved-model', AiApiKey: 'test-secret', TmdbApiKey: 'saved-tmdb', TvdbApiKey: ''
};

const ids = { frieren: 'a1000000000000000000000000000001', dandadan: 'a1000000000000000000000000000002', mononoke: 'a1000000000000000000000000000003', lost: 'a1000000000000000000000000000004' };

const catalog = [
    { id: ids.dandadan, name: 'Dandadan', year: 2024, type: 'Series', animeClickId: '46016', hasPrimaryImage: true, updatedAt: '2026-10-04T09:00:00Z' },
    { id: ids.frieren, name: 'Frieren - Oltre la fine del viaggio', year: 2023, type: 'Series', animeClickId: '44005', hasPrimaryImage: true, updatedAt: '2026-10-04T10:00:00Z' },
    { id: ids.mononoke, name: 'La principessa Mononoke', year: 1997, type: 'Movie', animeClickId: '420', hasPrimaryImage: true, updatedAt: '2026-10-01T10:00:00Z' }
];

const titleReport = {
    episodeTitlesEnabled: true, seriesCount: 3, episodeCount: 40, missingTitleCount: 3, recoverableTitleCount: 2,
    waitingTitleCount: 1, unavailableTitleCount: 0,
    series: [
        { id: ids.dandadan, name: 'Dandadan', year: 2024, animeClickId: '46016', episodeCount: 12, missingTitleCount: 3,
            recoverableTitleCount: 2, waitingTitleCount: 1, reason: 'PendingRefresh', reasonLabel: 'Basta un ricontrollo',
            seasons: [{ seasonNumber: 1, missingTitleCount: 3, animeClickId: '46016', reason: 'PendingRefresh', reasonLabel: 'Basta un ricontrollo' }] },
        { id: ids.frieren, name: 'Frieren - Oltre la fine del viaggio', year: 2023, animeClickId: '44005', episodeCount: 28, missingTitleCount: 0, reason: 'Ok', reasonLabel: '', seasons: [] },
        { id: ids.lost, name: 'Serie senza scheda', year: 2025, animeClickId: null, episodeCount: 0, missingTitleCount: 0, reason: 'NotIdentified', reasonLabel: 'Serie non identificata', seasons: [] }
    ]
};

const qualityReport = {
    itemCount: 44, italianCount: 40, repairableCount: 2, missingCount: 1, englishCount: 1, unknownCount: 2,
    maximumRepairItems: 100,
    series: [{ id: ids.dandadan, name: 'Dandadan', year: 2024, itemCount: 13, englishCount: 1, missingCount: 1, unknownCount: 2,
        items: [
            { id: 'e1', itemType: 'Episode', name: 'Episodio 5', seasonNumber: 1, episodeNumber: 5, status: 'English', canRepair: true, languageRepairable: true, preview: 'Okarun meets a ghost.' },
            { id: 'e2', itemType: 'Episode', name: 'Episodio 6', seasonNumber: 1, episodeNumber: 6, status: 'Missing', canRepair: true, languageRepairable: true, repairState: 'no-source', suppressed: true },
            { id: 'e3', itemType: 'Episode', name: 'Episodio 7', seasonNumber: 1, episodeNumber: 7, status: 'Unknown', languageRepairable: true },
            { id: 'e4', itemType: 'Episode', name: 'Episodio 8', seasonNumber: 1, episodeNumber: 8, status: 'Unknown', languageRepairable: true }
        ] }]
};

const showcase = {
    seriesCount: 2, movieCount: 1,
    items: [
        { id: ids.frieren, name: 'Frieren - Oltre la fine del viaggio', originalTitle: 'Sousou no Frieren', year: 2023, type: 'Series', animeClickId: '44005',
            animeClickUrl: 'https://www.animeclick.it/anime/44005', overview: 'La maga elfa Frieren, sconfitto il Re dei demoni con i compagni, scopre quanto sia breve la vita degli umani.',
            genres: ['Avventura', 'Fantasy'], communityRating: 8.9, updatedAt: '2026-10-04T10:00:00Z', hasPrimaryImage: true, hasBackdropImage: true },
        { id: ids.dandadan, name: 'Dandadan', year: 2024, type: 'Series', animeClickId: '46016', overview: 'Momo e Okarun tra fantasmi e alieni.',
            genres: ['Azione'], updatedAt: '2026-10-04T09:00:00Z', hasPrimaryImage: true },
        { id: ids.mononoke, name: 'La principessa Mononoke', year: 1997, type: 'Movie', animeClickId: '420', genres: [], updatedAt: '2026-10-01T10:00:00Z', hasPrimaryImage: true }
    ]
};

const librariesFixture = () => [
    { id: 'lib-anime', name: 'Anime', collectionType: 'tvshows', state: 'inactive', canEnable: true,
        types: [{ type: 'Series', configured: true }, { type: 'Season', configured: true }, { type: 'Episode', configured: true }] },
    { id: 'lib-film', name: 'Film', collectionType: 'movies', state: 'active', canEnable: false,
        types: [{ type: 'Movie', configured: true, metadataEnabled: true, metadataFirst: true, imagesEnabled: true }] }
];

function poster(url) {
    const id = new URL(url, 'http://localhost').pathname.split('/')[3] || 'x';
    const hue = parseInt(id.slice(-3), 16) % 360;
    return `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${hue},70%,45%)"/><stop offset="1" stop-color="hsl(${(hue + 60) % 360},70%,20%)"/></linearGradient></defs><rect width="400" height="600" fill="url(#g)"/><circle cx="300" cy="140" r="90" fill="rgba(255,255,255,.15)"/></svg>`;
}

before(async () => {
    const assets = {
        AnimeClickCss: ['Web/assets/animeclick.css', 'text/css'],
        AnimeClickCoreJs: ['Web/assets/animeclick-core.js', 'text/javascript'],
        AnimeClickSettingsJs: ['Web/assets/animeclick-settings.js', 'text/javascript'],
        AnimeClickLibraryJs: ['Web/assets/animeclick-library.js', 'text/javascript'],
        AnimeClickHomeJs: ['Web/assets/animeclick-home.js', 'text/javascript'],
        AnimeClickSetupJs: ['Web/assets/animeclick-setup.js', 'text/javascript'],
        AnimeClickAppJs: ['Web/assets/animeclick-app.js', 'text/javascript'],
        AnimeClickMascot: ['Web/assets/mascot-360.png', 'image/png'],
        AnimeClickMascotSmall: ['Web/assets/mascot-96.png', 'image/png'],
        AnimeClickLogoTmdb: ['assets/providers/tmdb.svg', 'image/svg+xml'],
        AnimeClickLogoFanart: ['assets/providers/fanart.png', 'image/png'],
        AnimeClickLogoTvdb: ['assets/providers/thetvdb-dark.png', 'image/png']
    };
    server = http.createServer((req, res) => {
        const name = new URL(req.url, 'http://localhost').searchParams.get('name');
        const [file, type] = assets[name] || ['Configuration/configPage.html', 'text/html'];
        res.setHeader('Content-Type', type);
        let content = fs.readFileSync(path.join(root, file));
        if (type === 'text/html') content = content.toString().replace('</head>', '<style>body{margin:0;padding:16px;background:#101010;color:#fff;font-family:system-ui}</style></head>');
        res.end(content);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true });
    fs.mkdirSync(results, { recursive: true });
});

after(async () => {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
});

async function mount(options = {}) {
    const page = await browser.newPage({ viewport: options.viewport || { width: 1280, height: 1000 } });
    const state = {
        config: { ...defaults, ...options.config }, writes: [], errors: [], reads: 0, identifications: [], requests: [],
        activities: options.activities || [], titleRuns: 0, synopsisRuns: 0, enabled: [], libraries: librariesFixture(),
        repairs: [], lookups: 0
    };
    page.on('pageerror', error => state.errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && !/Failed to load resource/.test(message.text())) state.errors.push(message.text()); });
    await page.addInitScript(() => {
        window.ApiClient = {
            getUrl: route => '/base/' + route,
            accessToken: () => 'test-token',
            serverId: () => 'server-1',
            getPluginConfiguration: () => fetch('/base/config').then(r => { if (!r.ok) throw new Error('Server non disponibile'); return r.json(); }),
            updatePluginConfiguration: (_, config) => fetch('/base/config', { method: 'POST', body: JSON.stringify(config) }).then(r => { if (!r.ok) throw new Error('Salvataggio non riuscito'); })
        };
    });
    await page.route('**/base/**', async route => {
        const request = route.request();
        const url = new URL(request.url());
        const target = url.pathname;
        state.requests.push({ target, method: request.method() });
        if (target.includes('/Images/')) return route.fulfill({ contentType: 'image/svg+xml', body: poster(target) });
        if (target.endsWith('/Activities')) {
            if (options.failActivities) return route.fulfill({ status: 503, body: 'Stato non disponibile' });
            return route.fulfill({ json: state.activities });
        }
        if (target.endsWith('/LibraryAudit')) {
            if (options.failTitleAudit) return route.fulfill({ status: 503, body: 'Analisi titoli non disponibile' });
            if (options.auditDelay) await new Promise(resolve => setTimeout(resolve, options.auditDelay));
            return route.fulfill({ json: options.titleReport || titleReport });
        }
        if (target.endsWith('/LibraryAuditSeries')) {
            const body = request.postDataJSON();
            const fresh = { ...titleReport.series.find(s => s.id === body.itemId), missingTitleCount: 1, recoverableTitleCount: 1, waitingTitleCount: 0 };
            return route.fulfill({ json: fresh });
        }
        if (target.endsWith('/LibraryQualityAudit')) return route.fulfill({ json: options.qualityReport || qualityReport });
        if (target.endsWith('/LibraryQualityRepair')) { state.repairs.push(request.postDataJSON()); return route.fulfill({ json: { queuedCount: 1, consideredCount: 1 } }); }
        if (target.endsWith('/Catalog')) return route.fulfill({ json: options.catalog || catalog });
        if (target.endsWith('/Showcase')) return route.fulfill({ json: options.showcase || showcase });
        if (target.endsWith('/Libraries')) return route.fulfill({ json: state.libraries });
        if (target.endsWith('/Libraries/Enable')) {
            const body = request.postDataJSON();
            state.enabled.push(body.libraryId);
            state.libraries = state.libraries.map(lib => lib.id === body.libraryId ? { ...lib, state: 'active', canEnable: false,
                types: lib.types.map(type => ({ ...type, metadataEnabled: true, metadataFirst: true, imagesEnabled: true })) } : lib);
            return route.fulfill({ json: { success: true, changed: true, library: state.libraries.find(lib => lib.id === body.libraryId) } });
        }
        if (target.endsWith('/RunMissingTitlesTask') || target.endsWith('/RunSynopsisRepairTask')) {
            const titles = target.endsWith('/RunMissingTitlesTask');
            if (titles) state.titleRuns++; else state.synopsisRuns++;
            state.activities = [{ key: titles ? 'titles' : 'synopses', state: 'Running', isActive: true, total: 12,
                processed: 4, applied: 3, skipped: 1, errors: 0, progress: 33, message: 'Dandadan · S1 E5: lettura del titolo…' }];
            return route.fulfill({ json: { queued: true, message: 'Attività avviata' } });
        }
        if (target.endsWith('/CancelActivity')) {
            state.activities = state.activities.map(job => ({ ...job, state: 'Cancelled', isActive: false, message: 'Attività interrotta.' }));
            return route.fulfill({ json: state.activities[0] });
        }
        if (target === '/base/config') {
            if (request.method() === 'POST') {
                if (options.failSave) return route.fulfill({ status: 500, body: 'error' });
                state.config = request.postDataJSON();
                state.writes.push(state.config);
            } else {
                state.reads++;
                if (options.failFirstLoad && state.reads === 1) return route.fulfill({ status: 503, body: 'error' });
            }
            return route.fulfill({ json: state.config });
        }
        if (target.endsWith('/AiProviders')) {
            if (options.failProviders) return route.fulfill({ status: 503, body: 'error' });
            return route.fulfill({ json: [{ id: 'custom', displayName: 'Personalizzato', requiresApiKey: false, chatEndpoint: '' }] });
        }
        if (target.endsWith('/Items')) return route.fulfill({ json: { Items: [{ Id: 'safe-item-id', Name: 'Un titolo <img src=x onerror=alert(1)>', ProductionYear: 2026, Type: 'Series', ImageTags: {} }] } });
        if (target.endsWith('/TestLookup')) {
            state.lookups++;
            return route.fulfill({ json: [{ name: 'Seishun Buta Yarou', year: 2018, animeClickId: '25493/seishun-buta-yarou', imageUrl: null }] });
        }
        if (target.endsWith('/TestFanart')) {
            state.fanartTest = request.postDataJSON();
            return route.fulfill({ json: { Success: true, ImageCount: 90 } });
        }
        if (target.endsWith('/TestTmdb')) return route.fulfill({ json: { success: true, sampleName: 'Frieren' } });
        if (target.endsWith('/IdentifyAndRefresh')) {
            state.identifications.push(request.postDataJSON());
            return route.fulfill({ json: { Success: true, RefreshTriggered: true } });
        }
        return route.fulfill({ json: {} });
    });
    await page.goto(origin);
    await page.evaluate(() => document.querySelector('#AnimeClickConfigPage').dispatchEvent(new Event('pageshow')));
    if (!options.failFirstLoad) await page.waitForFunction(() => document.querySelector('#acLoadState') && document.querySelector('#acLoadState').hidden);
    return { page, state };
}

async function select(page, tab) { await page.getByRole('tab', { name: tab, exact: true }).click(); }
async function openDetails(page, title) { await page.locator('summary', { hasText: title }).click(); }
async function saveAndWait(page) {
    await page.locator('#acBtnSave').click();
    await page.waitForFunction(() => document.querySelector('#acSaveBar').hidden);
}
async function confirmDialog(page, label = 'Conferma') {
    await page.getByRole('dialog').getByRole('button', { name: label, exact: true }).click();
}
async function shot(page, name) { await page.screenshot({ path: path.join(results, name + '.png'), fullPage: true }); }

test('home opens on the showcase and dashboard, and the tabs work with the keyboard', async () => {
    const { page, state } = await mount();
    try {
        await page.locator('.ac-hero-title', { hasText: 'Frieren - Oltre la fine del viaggio' }).waitFor();
        assert.equal(await page.locator('#acHomeRail .ac-poster-card').count(), 3);
        await page.locator('#acHomeHealthBody .ac-ring').first().waitFor();
        assert.equal(await page.locator('#acView_home input:visible').count(), 0);
        await page.locator('#acHomeLibraries').getByText('Anime', { exact: true }).waitFor();
        await page.getByRole('tab', { name: 'Inizio', exact: true }).focus();
        await page.keyboard.press('ArrowRight');
        assert.equal(await page.locator('#acTab_library').getAttribute('aria-selected'), 'true');
        await page.keyboard.press('End');
        assert.equal(await page.locator('#acTab_community').getAttribute('aria-selected'), 'true');
        assert.equal(await page.locator('#acView_community').isVisible(), true);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('a fresh installation runs the full setup, saves its choices and enables the chosen library', async () => {
    const { page, state } = await mount({ config: { ConfigurationVersion: 2, SetupCompletedVersion: 0, TmdbApiKey: '' } });
    try {
        await page.locator('#acSetup').waitFor();
        assert.equal(await page.locator('.ac-main').isVisible(), false);
        assert.deepEqual(await page.locator('.ac-steps li').allInnerTexts(), ['Benvenuto', 'Fonti', 'Preferenze', 'Librerie', 'Fatto']);
        await shot(page, 'setup-welcome-desktop');
        await page.getByRole('button', { name: 'Iniziamo' }).click();
        await page.locator('#acSetupTmdbKey').fill('new-tmdb-key');
        await page.locator('#acSetupNext').click();
        await page.locator('#acSetupPref_EnableCast').uncheck();
        await page.getByRole('button', { name: 'Salva e continua' }).click();
        await page.locator('#acSetupLibraries .ac-lib').first().waitFor();
        assert.equal(state.writes.length, 1);
        assert.equal(state.config.TmdbApiKey, 'new-tmdb-key');
        assert.equal(state.config.EnableCast, false);
        assert.equal(state.config.SetupCompletedVersion, 0);
        assert.equal(await page.locator('#acSetupLib_lib-anime').isChecked(), true, 'libraries named after anime are preselected');
        assert.equal(await page.locator('#acSetupLib_lib-film').isDisabled(), true);
        await shot(page, 'setup-libraries-desktop');
        await page.locator('#acSetupEnable').click();
        await confirmDialog(page, 'Attiva');
        await page.locator('#acSetupLibrariesResult', { hasText: 'Fatto!' }).waitFor();
        assert.deepEqual(state.enabled, ['lib-anime']);
        await page.locator('#acSetupNext').click();
        await page.locator('#acSetupFinish').click();
        await page.locator('#acSetup').waitFor({ state: 'hidden' });
        assert.equal(state.config.SetupCompletedVersion, 2);
        assert.equal(state.config.TmdbApiKey, 'new-tmdb-key');
        assert.equal(await page.locator('#acView_home').isVisible(), true);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('an update shows only the new steps and skipping keeps every other setting', async () => {
    const { page, state } = await mount({ config: { SetupCompletedVersion: 1 } });
    try {
        await page.locator('#acSetup').waitFor();
        assert.deepEqual(await page.locator('.ac-steps li').allInnerTexts(), ['Novità', 'Librerie', 'Fatto']);
        const before = { ...state.config };
        await page.locator('#acSetupSkip').click();
        await confirmDialog(page, 'Salta');
        await page.locator('#acSetup').waitFor({ state: 'hidden' });
        assert.equal(state.config.SetupCompletedVersion, 2);
        assert.deepEqual({ ...state.config, SetupCompletedVersion: 1 }, before);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('zero cache values survive saving and a failed provider list preserves the profile', async () => {
    const { page, state } = await mount({ failProviders: true });
    try {
        await select(page, 'Strumenti');
        await openDetails(page, 'Ricerca, rete e compatibilità');
        await page.locator('#acNegativeCacheHours').fill('0');
        await select(page, 'Fonti');
        await openDetails(page, 'Indirizzo del servizio e tempi di attesa');
        await page.locator('#acTranslationCacheHours').fill('0');
        await saveAndWait(page);
        assert.equal(state.config.NegativeCacheHours, 0);
        assert.equal(state.config.TranslationCacheHours, 0);
        assert.equal(state.config.AiProvider, defaults.AiProvider);
        assert.equal(state.config.AiApiKey, defaults.AiApiKey);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('discard and repeated page show preserve the expected editing state', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Preferenze');
        await page.locator('#acPreferItalianTitle').uncheck();
        assert.equal(await page.locator('#acSaveBar').isVisible(), true);
        const bar = await page.locator('#acSaveBar').boundingBox();
        assert.ok(bar.y + bar.height <= 1000, 'the save bar stays on screen while editing at the top of a long page');
        await page.evaluate(() => window.AnimeClickUI.app.show(document.querySelector('#AnimeClickConfigPage')));
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), false);
        await page.locator('#acBtnDiscard').click();
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), true);
        assert.equal(await page.locator('#acSaveBar').isVisible(), false);
        assert.equal(state.writes.length, 0);
    } finally { await page.close(); }
});

test('configuration failures offer retry and never enable empty settings', async () => {
    const { page } = await mount({ failFirstLoad: true });
    try {
        await page.getByRole('button', { name: 'Riprova', exact: true }).waitFor();
        assert.equal(await page.locator('.ac-main').evaluate(n => n.inert), true);
        await page.getByRole('button', { name: 'Riprova', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('#acLoadState').hidden);
        assert.equal(await page.locator('.ac-main').evaluate(n => n.inert), false);
    } finally { await page.close(); }
});

test('saving merges only edited preferences, retaining another administrator’s changes', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Preferenze');
        await page.locator('#acPreferItalianTitle').uncheck();
        state.config.CacheHours = 72;
        await saveAndWait(page);
        assert.equal(state.config.CacheHours, 72);
        assert.equal(state.config.PreferItalianTitle, false);
    } finally { await page.close(); }
});

test('failed save keeps user edits available for retry', async () => {
    const { page, state } = await mount({ failSave: true });
    try {
        await select(page, 'Preferenze');
        await page.locator('#acPreferItalianTitle').uncheck();
        await page.locator('#acBtnSave').click();
        await page.getByRole('alert').filter({ hasText: 'Salvataggio non riuscito' }).waitFor();
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), false);
        assert.equal(await page.locator('.ac-main').evaluate(n => n.inert), false);
        assert.equal(await page.locator('#acSaveBar').isVisible(), true);
        assert.equal(state.writes.length, 0);
    } finally { await page.close(); }
});

test('manual identification selects a title safely, finds the AnimeClick card and preserves artwork by default', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Strumenti');
        await page.locator('#acItemSearch').fill('Un titolo');
        await page.locator('#acBtnFindItem').click();
        await page.locator('#acItemCandidates .ac-pick').click();
        assert.match(await page.locator('#acSelectedItem').innerText(), /Un titolo <img src=x onerror=alert\(1\)>/);
        assert.equal(await page.locator('img[src="x"]').count(), 0);
        await page.locator('#acAnimeClickSearch').fill('Seishun Buta');
        await page.locator('#acBtnLookup').click();
        await page.locator('#acAnimeClickCandidates .ac-pick').click();
        assert.equal(await page.locator('#acAnimeClickId').inputValue(), '25493/seishun-buta-yarou');
        await page.locator('#acBtnIdentify').click();
        await page.locator('#acIdentifyResult').getByText('Abbinamento salvato', { exact: true }).waitFor();
        assert.equal(state.identifications.length, 1);
        assert.equal(state.identifications[0].itemId, 'safe-item-id');
        assert.equal(state.identifications[0].animeClickId, '25493/seishun-buta-yarou');
        assert.equal(state.identifications[0].replaceAllImages, false);
        assert.equal(state.identifications[0].replaceAllMetadata, false);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('removing a saved AI key also clears its legacy copy', async () => {
    const { page, state } = await mount({ config: { OllamaCloudApiKey: 'old-test-key' } });
    try {
        await select(page, 'Fonti');
        await page.locator('#acClearAiKey').check();
        await saveAndWait(page);
        assert.equal(state.config.AiApiKey, '');
        assert.equal(state.config.OllamaCloudApiKey, '');
    } finally { await page.close(); }
});

test('a concurrent edit of the same setting is reported without overwriting it', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Strumenti');
        await openDetails(page, 'Ricerca, rete e compatibilità');
        await page.locator('#acCacheHours').fill('72');
        state.config.CacheHours = 96;
        await page.locator('#acBtnSave').click();
        await page.getByText(/Le stesse impostazioni sono cambiate/).waitFor();
        assert.equal(state.writes.length, 0);
        assert.equal(state.config.CacheHours, 96);
        assert.equal(await page.locator('#acCacheHours').inputValue(), '72');
    } finally { await page.close(); }
});

test('a saved AI credential cannot silently move to another destination', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Fonti');
        await openDetails(page, 'Indirizzo del servizio e tempi di attesa');
        await page.locator('#acAiEndpoint').fill('https://different.example/v1/chat/completions');
        await page.locator('#acBtnSave').click();
        await page.getByText(/inserisci la nuova chiave/).first().waitFor();
        assert.equal(state.writes.length, 0);
        assert.equal(state.config.AiEndpoint, defaults.AiEndpoint);
    } finally { await page.close(); }
});

test('an invalid number sends the user to the field instead of saving', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Preferenze');
        await page.locator('#acMinPosterWidth').fill('99999');
        await select(page, 'Comunità');
        await page.locator('#acBtnSave').click();
        await page.getByRole('alert').filter({ hasText: /Controlla «Larghezza minima della locandina/ }).waitFor();
        assert.equal(await page.locator('#acTab_preferences').getAttribute('aria-selected'), 'true');
        assert.equal(await page.evaluate(() => document.activeElement.id), 'acMinPosterWidth');
        assert.equal(state.writes.length, 0);
    } finally { await page.close(); }
});

test('every view fits narrow screens and has no duplicate IDs', async () => {
    const { page, state } = await mount();
    try {
        for (const width of [320, 390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            for (const tab of ['Inizio', 'Libreria', 'Preferenze', 'Fonti', 'Strumenti', 'Comunità']) {
                await select(page, tab);
                const fits = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
                assert.ok(fits, `${tab} overflows at ${width}px`);
            }
        }
        const duplicates = await page.locator('[id]').evaluateAll(nodes => {
            const seen = nodes.map(n => n.id);
            return seen.filter((id, i) => seen.indexOf(id) !== i);
        });
        assert.deepEqual(duplicates, []);
        assert.deepEqual(state.errors, []);
        await page.setViewportSize({ width: 1280, height: 1000 });
        for (const [tab, name] of [['Inizio', 'home'], ['Libreria', 'library'], ['Preferenze', 'preferences'], ['Fonti', 'sources'], ['Strumenti', 'tools'], ['Comunità', 'community']]) {
            await select(page, tab);
            await page.waitForTimeout(500);
            await shot(page, name + '-desktop');
        }
        await page.setViewportSize({ width: 390, height: 844 });
        for (const [tab, name] of [['Inizio', 'home'], ['Libreria', 'library']]) {
            await select(page, tab);
            await page.waitForTimeout(500);
            await shot(page, name + '-mobile');
        }
    } finally { await page.close(); }
});

test('library joins titles and synopses per anime, filters them and opens the detail', async () => {
    const { page, state } = await mount({ auditDelay: 1500 });
    try {
        await select(page, 'Libreria');
        await page.locator('#acLibraryScanProgress:not([hidden])').waitFor();
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        assert.deepEqual(await page.locator('#acLibraryGrid .ac-poster-title').allInnerTexts(), ['Serie senza scheda', 'Dandadan']);
        assert.match(await page.locator('#acFilter_all').innerText(), /4/);
        await page.locator('#acFilter_complete').click();
        assert.deepEqual(await page.locator('#acLibraryGrid .ac-poster-title').allInnerTexts(), ['Frieren - Oltre la fine del viaggio', 'La principessa Mononoke']);
        await page.locator('#acFilter_all').click();
        await page.locator('#acLibrarySearch').fill('dandadan');
        await page.locator('#acLibraryGrid .ac-anime-card').click();
        const drawer = page.getByRole('dialog', { name: 'Dandadan' });
        await drawer.waitFor();
        await drawer.getByText('S1E5 · Episodio 5').waitFor();
        assert.match(await drawer.innerText(), /3 titoli da sistemare/);
        await shot(page, 'library-detail-desktop');
        await drawer.getByRole('button', { name: 'Rileggi da AnimeClick' }).click();
        await drawer.getByText(/1 titolo da sistemare/).waitFor();
        await page.keyboard.press('Escape');
        await drawer.waitFor({ state: 'detached' });
        assert.equal(state.titleRuns, 0); assert.equal(state.synopsisRuns, 0);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('title repair sends one start, shows real progress, and can be stopped', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        await page.locator('#acBtnRunTitles').click();
        await confirmDialog(page, 'Avvia');
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'In corso' }).waitFor();
        assert.equal(state.titleRuns, 1);
        assert.equal(await page.locator('#acActivityProgress_titles').getAttribute('aria-valuenow'), '33');
        assert.equal(await page.locator('#acBtnRunTitles').isDisabled(), true);
        assert.equal(await page.locator('#acLive').isVisible(), true);
        await page.getByRole('button', { name: 'Interrompi titoli episodio', exact: true }).click();
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'Interrotta' }).waitFor();
        assert.equal(state.titleRuns, 1);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('titles absent on AnimeClick can be checked in configured alternatives without promising recovery', async () => {
    const { page, state } = await mount({ titleReport: { episodeTitlesEnabled: true, alternativeTitleLookupEnabled: true,
        seriesCount: 1, episodeCount: 12, missingTitleCount: 12, recoverableTitleCount: 0,
        waitingTitleCount: 0, unavailableTitleCount: 12, series: [] } });
    try {
        await select(page, 'Libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        assert.equal(await page.locator('#acAuditSummary').getByText('Assenti su AnimeClick', { exact: true }).count(), 1);
        assert.equal(await page.locator('#acAuditSummary').getByText('Da verificare', { exact: true }).count(), 1);
        assert.equal(await page.locator('#acBtnRunTitles').isEnabled(), true);
        await page.locator('#acBtnRunTitles').click();
        const dialog = page.getByRole('dialog');
        await dialog.waitFor();
        const confirmation = await dialog.innerText();
        assert.match(confirmation, /12 episodi con titolo da sistemare/);
        assert.match(confirmation, /riconoscibilmente inglesi/);
        assert.match(confirmation, /Titoli italiani o incerti, campi bloccati/);
        assert.match(confirmation, /servizio AI può avere un costo/);
        await dialog.getByRole('button', { name: 'Annulla', exact: true }).click();
        assert.equal(state.titleRuns, 0);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('the number to verify is the one the title repair will inspect', async () => {
    const { page, state } = await mount({ titleReport: { ...titleReport, alternativeTitleLookupEnabled: true, missingTitleCount: 312, checkableTitleCount: 290 } });
    try {
        await select(page, 'Libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        assert.equal(await page.locator('#acAuditSummary .ac-number', { hasText: 'Da verificare' }).locator('b').innerText(), '290');
        await page.locator('#acBtnRunTitles').click();
        assert.match(await page.getByRole('dialog').innerText(), /i 290 episodi con titolo da sistemare/);
        await page.getByRole('dialog').getByRole('button', { name: 'Annulla', exact: true }).click();
        assert.equal(state.titleRuns, 0);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('a running activity is visible on reopening and polling stops outside home and library', async () => {
    const { page, state } = await mount({ activities: [{ key: 'titles', state: 'Running', isActive: true, total: 10,
        processed: 5, applied: 3, skipped: 2, progress: 50, message: 'Lettura in corso…' }] });
    try {
        await page.locator('#acHomeBadge_titles').filter({ hasText: 'In corso' }).waitFor();
        await select(page, 'Libreria');
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'In corso' }).waitFor();
        await select(page, 'Preferenze');
        const count = state.requests.filter(r => r.target.endsWith('/Activities')).length;
        await page.waitForTimeout(2300);
        assert.equal(state.requests.filter(r => r.target.endsWith('/Activities')).length, count);
        await select(page, 'Libreria');
        await page.locator('#acActivityProgress_titles[aria-valuenow="50"]').waitFor();
        assert.equal(state.titleRuns, 0);
    } finally { await page.close(); }
});

test('partial audit and polling errors are visible and retry remains available', async () => {
    const { page } = await mount({ failTitleAudit: true, failActivities: true });
    try {
        await select(page, 'Libreria');
        await page.locator('#acLibraryScanState').filter({ hasText: /Analisi incompleta/ }).waitFor();
        await page.locator('#acActivityConnection').filter({ hasText: 'Stato non disponibile' }).waitFor();
        assert.equal(await page.locator('#acLibraryScan').isEnabled(), true);
        assert.equal(await page.locator('#acBtnRunTitles').isDisabled(), true);
    } finally { await page.close(); }
});

test('synopses without a source can be retried in a balanced batch', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        await page.locator('#acBtnQualityRetryNoSource').click();
        await confirmDialog(page, 'Riprova');
        await page.getByText(/elementi accodati/).waitFor();
        assert.deepEqual(state.repairs, [{ itemIds: ['e2'], force: true }]);
    } finally { await page.close(); }
});

test('community sharing is opt-in, requires a token, and allows revoking it', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Comunità');
        assert.equal(await page.locator('#acEnableCommunitySharing').isChecked(), false);
        assert.equal(await page.locator('#acEnableCommunityMappings').isChecked(), false);
        await page.locator('#acEnableCommunitySharing').check();
        await page.locator('#acBtnSave').click();
        await page.getByText(/Per l’invio automatico serve/).waitFor();
        assert.equal(state.writes.length, 0);
        await page.locator('#acCommunityGitHubToken').fill('fake-browser-test-token');
        await saveAndWait(page);
        assert.equal(state.config.EnableCommunitySharing, true);
        assert.equal(state.config.CommunityGitHubToken, 'fake-browser-test-token');
        assert.equal(await page.locator('#acCommunityGitHubToken').inputValue(), '');
        await page.locator('#acEnableCommunitySharing').uncheck();
        await page.locator('#acClearCommunityToken').check();
        await saveAndWait(page);
        assert.equal(state.config.CommunityGitHubToken, '');
        assert.equal(state.config.EnableCommunitySharing, false);
    } finally { await page.close(); }
});

test('every metadata switch and numeric preference survives save and reload', async () => {
    const { page, state } = await mount();
    try {
        const controls = await page.locator('[data-key].ac-switch').evaluateAll(nodes => nodes.map(n => ({ id: n.id, key: n.dataset.key, checked: n.checked })));
        assert.ok(controls.length >= 22, 'every boolean preference has a switch');
        await page.evaluate(controls => controls.forEach(({ id, checked }) => {
            const node = document.getElementById(id); node.checked = !checked; node.dispatchEvent(new Event('change'));
        }), controls);
        const numbers = { acMinPosterWidth: '640', acMaxSearchResults: '12', acCacheHours: '72', acNegativeCacheHours: '0',
            acRequestDelayMilliseconds: '1500', acTranslationCacheHours: '0', acEpisodeTranslationTimeoutSec: '120' };
        await page.evaluate(numbers => Object.entries(numbers).forEach(([id, value]) => {
            const node = document.getElementById(id); node.value = value; node.dispatchEvent(new Event('input'));
        }), numbers);
        // Sharing needs a token, so give it one for this round trip.
        await page.locator('#acCommunityGitHubToken').evaluate(node => { node.value = 'token-for-roundtrip'; node.dispatchEvent(new Event('input')); });
        await saveAndWait(page);
        for (const { key, checked } of controls) assert.equal(state.config[key], !checked, key);
        for (const [id, value] of Object.entries(numbers)) assert.equal(await page.locator('#' + id).inputValue(), value, id);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('Fanart keys are editable, verified and preserved alongside the single-provider preferences', async () => {
    const { page, state } = await mount({ config: { FanartPersonalApiKey: 'saved-fanart-personal', FanartProjectApiKey: '', EnableIntegratedMetadata: true, EnableIntegratedImages: true, EnableFanartImages: true } });
    try {
        await select(page, 'Fonti');
        assert.equal(await page.locator('#acFanartPersonalApiKey').getAttribute('type'), 'password');
        assert.equal(await page.locator('a[href="https://fanart.tv/get-an-api-key/"]').count(), 1);
        await page.locator('#acFanartPersonalApiKey').fill('edited-fanart-personal');
        await page.locator('#acFanartProjectApiKey').fill('edited-fanart-project');
        await page.getByRole('button', { name: 'Verifica Fanart', exact: true }).click();
        await page.locator('#acTestResult_fanart').filter({ hasText: '90 immagini' }).waitFor();
        assert.deepEqual(state.fanartTest, { personalApiKey: 'edited-fanart-personal', projectApiKey: 'edited-fanart-project' });
        assert.equal(await page.locator('#acSourceState_fanart').innerText(), 'Connessa');
        assert.equal(state.writes.length, 0);
        await saveAndWait(page);
        assert.equal(state.config.FanartPersonalApiKey, 'edited-fanart-personal');
        assert.equal(state.config.FanartProjectApiKey, 'edited-fanart-project');
        await select(page, 'Preferenze');
        await page.locator('#acPreferItalianTitle').uncheck();
        await saveAndWait(page);
        assert.equal(state.config.FanartPersonalApiKey, 'edited-fanart-personal');
        assert.equal(state.config.EnableIntegratedMetadata, true);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('enabling a library from the dashboard asks first and updates its card', async () => {
    const { page, state } = await mount();
    try {
        const card = page.locator('#acHomeLibraries .ac-lib', { hasText: 'Anime' });
        await card.getByRole('button', { name: 'Attiva AnimeClick' }).click();
        await page.getByRole('dialog').getByRole('button', { name: 'Annulla', exact: true }).click();
        assert.deepEqual(state.enabled, []);
        await card.getByRole('button', { name: 'Attiva AnimeClick' }).click();
        await confirmDialog(page, 'Attiva');
        await page.locator('#acHomeLibraries .ac-lib', { hasText: 'Anime' }).getByText('Attivo', { exact: true }).waitFor();
        assert.deepEqual(state.enabled, ['lib-anime']);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});
