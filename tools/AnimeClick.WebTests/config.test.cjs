const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
let browser, server, origin;
const defaults = {
    ConfigurationVersion: 1, PreferItalianTitle: true, EnablePlot: true,
    EnableEpisodeTitles: true, EnableEpisodeTitleFallback: true, EnableEpisodeSynopsisTranslation: true,
    EnableAiTranslation: true, MinPosterWidth: 400, MaxSearchResults: 10,
    CacheHours: 48, NegativeCacheHours: 12, RequestDelayMilliseconds: 1000,
    TranslationCacheHours: 87600, EpisodeTranslationTimeoutSec: 90,
    BaseUrl: 'https://www.animeclick.it', UserAgent: 'test',
    AiProvider: 'custom', AiEndpoint: 'https://translation.example/v1/chat/completions',
    AiModel: 'saved-model', AiApiKey: 'test-secret', TmdbApiKey: '', TvdbApiKey: ''
};

before(async () => {
    server = http.createServer((req, res) => {
        const name = new URL(req.url, 'http://localhost').searchParams.get('name');
        const assets = {
            AnimeClickCss: ['Web/assets/animeclick.css', 'text/css'],
            AnimeClickConfigJs: ['Web/assets/animeclick-config.js', 'text/javascript'],
            AnimeClickLibraryJs: ['Web/assets/animeclick-library.js', 'text/javascript'],
            AnimeClickLogo: ['assets/logo.png', 'image/png']
        };
        const [file, type] = assets[name] || ['Configuration/configPage.html', 'text/html'];
        res.setHeader('Content-Type', type);
        let content = fs.readFileSync(path.join(root, file));
        if (type === 'text/html') content = content.toString().replace('</head>', '<style>body{margin:0;padding:24px;background:#17191e;color:#f1f2f4;font-family:system-ui}*{box-sizing:border-box}</style></head>');
        res.end(content);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true });
});
after(async () => {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
});

async function mount(options = {}) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    const state = { config: { ...defaults, ...options.config }, writes: [], errors: [], reads: 0, identifications: [],
        requests: [], activities: options.activities || [], titleRuns: 0, synopsisRuns: 0 };
    page.on('pageerror', error => state.errors.push(error.message));
    await page.addInitScript(() => {
        window.ApiClient = {
            getUrl: route => '/base/' + route,
            accessToken: () => 'test-token',
            getPluginConfiguration: () => fetch('/base/config').then(r => { if (!r.ok) throw new Error('Server non disponibile'); return r.json(); }),
            updatePluginConfiguration: (_, config) => fetch('/base/config', { method: 'POST', body: JSON.stringify(config) }).then(r => { if (!r.ok) throw new Error('Salvataggio non riuscito'); })
        };
    });
    await page.route('**/base/**', async route => {
        const request = route.request();
        const target = new URL(request.url()).pathname;
        state.requests.push({ target, method: request.method() });
        if (target.endsWith('/Activities')) {
            if (options.failActivities) return route.fulfill({ status: 503, body: 'Stato non disponibile' });
            return route.fulfill({ json: state.activities });
        }
        if (target.endsWith('/LibraryAudit')) {
            if (options.failTitleAudit) return route.fulfill({ status: 503, body: 'Analisi titoli non disponibile' });
            if (options.auditDelay) await new Promise(resolve => setTimeout(resolve, options.auditDelay));
            return route.fulfill({ json: options.titleReport || { episodeTitlesEnabled: true, seriesCount: 1, episodeCount: 12,
                missingTitleCount: 3, recoverableTitleCount: 2, waitingTitleCount: 1, unavailableTitleCount: 0,
                series: [{ id: 'series-1', name: 'Una serie', animeClickId: '72', episodeCount: 12, missingTitleCount: 3,
                    recoverableTitleCount: 2, waitingTitleCount: 1, reason: 'PendingRefresh', reasonLabel: 'Titoli da completare', seasons: [] }] } });
        }
        if (target.endsWith('/LibraryQualityAudit')) return route.fulfill({ json: options.qualityReport || {
            itemCount: 13, italianCount: 10, repairableCount: 1, missingCount: 1, englishCount: 0, unknownCount: 2,
            maximumRepairItems: 100, series: [] } });
        if (target.endsWith('/RunMissingTitlesTask') || target.endsWith('/RunSynopsisRepairTask')) {
            const titles = target.endsWith('/RunMissingTitlesTask');
            if (titles) state.titleRuns++; else state.synopsisRuns++;
            state.activities = [{ key: titles ? 'titles' : 'synopses', state: 'Running', isActive: true, total: 12,
                processed: 4, applied: 3, skipped: 1, errors: 0, progress: 33, message: 'Una serie · S1 E5: lettura del titolo…' }];
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
        if (target.endsWith('/VirtualFolders')) return route.fulfill({ json: [{ Name: 'Anime', LibraryOptions: { TypeOptions: [{ Type: 'Series', MetadataFetchers: ['AnimeClick'], MetadataFetcherOrder: ['AnimeClick'] }] } }] });
        if (target.endsWith('/Items')) return route.fulfill({ json: { Items: [{ Id: 'safe-item-id', Name: 'Un titolo <img src=x onerror=alert(1)>', ProductionYear: 2026 }] } });
        if (target.endsWith('/TestFanart')) {
            state.fanartTest = request.postDataJSON();
            return route.fulfill({ json: { Success: true, ImageCount: 90 } });
        }
        if (target.endsWith('/IdentifyAndRefresh')) {
            state.identifications.push(request.postDataJSON());
            return route.fulfill({ json: { Success: true, RefreshTriggered: true } });
        }
        return route.fulfill({ json: {} });
    });
    await page.goto(origin);
    await page.evaluate(() => document.querySelector('#AnimeClickConfigPage').dispatchEvent(new Event('pageshow')));
    if (!options.failFirstLoad) await page.waitForFunction(() => document.querySelector('#acLoadState').hidden);
    return { page, state };
}

async function select(page, tab) { await page.getByRole('tab', { name: tab, exact: true }).click(); }
async function openDetails(page, title) { await page.getByText(title, { exact: true }).click(); }

test('home is usable without external services and tabs work with the keyboard', async () => {
    const { page, state } = await mount();
    try {
        assert.equal(await page.getByRole('heading', { name: 'Più storie. Meno impostazioni.' }).count(), 1);
        assert.equal(await page.locator('input:visible').count(), 0);
        await page.getByRole('tab', { name: 'Inizio', exact: true }).focus();
        await page.keyboard.press('ArrowDown');
        assert.equal(await page.locator('#acTabLibreria').getAttribute('aria-selected'), 'true');
        await page.keyboard.press('End');
        assert.equal(await page.locator('#acTabStrumenti').getAttribute('aria-selected'), 'true');
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('zero cache values survive saving and a failed provider list preserves the profile', async () => {
    const { page, state } = await mount({ failProviders: true });
    try {
        await select(page, 'Avanzate');
        await openDetails(page, 'Ricerca, rete e compatibilità');
        await page.locator('#acNegativeCacheHours').fill('0');
        await select(page, 'Fonti aggiuntive');
        await openDetails(page, 'Traduci i testi mancanti');
        await openDetails(page, 'Indirizzo del servizio e tempi di attesa');
        await page.locator('#acTranslationCacheHours').fill('0');
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
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
        await page.evaluate(() => window.AC.config.show(document.querySelector('#AnimeClickConfigPage')));
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), false);
        await page.locator('#acBtnDiscard').click();
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), true);
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
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
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
        await page.getByRole('alert').filter({ hasText: 'Salvataggio fallito' }).waitFor();
        assert.equal(await page.locator('#acPreferItalianTitle').isChecked(), false);
        assert.equal(await page.locator('.ac-main').evaluate(n => n.inert), false);
        assert.equal(state.writes.length, 0);
    } finally { await page.close(); }
});

test('manual identification selects a title safely and preserves artwork by default', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Avanzate');
        await page.locator('#acItemSearch').fill('Un titolo');
        await page.locator('#acBtnFindItem').click();
        await page.locator('#acItemCandidates button').click();
        assert.equal(await page.locator('#acSelectedItem img').count(), 0);
        await page.locator('#acAnimeClickId').fill('https://www.animeclick.it/anime/25493/seishun-buta-film');
        await page.locator('#acBtnIdentify').click();
        await page.getByText('Abbinamento salvato', { exact: true }).waitFor();
        assert.equal(state.identifications.length, 1);
        assert.equal(state.identifications[0].itemId, 'safe-item-id');
        assert.equal(state.identifications[0].replaceAllImages, false);
        assert.equal(state.identifications[0].replaceAllMetadata, false);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('removing a saved AI key also clears its legacy copy', async () => {
    const { page, state } = await mount({ config: { OllamaCloudApiKey: 'old-test-key' } });
    try {
        await select(page, 'Fonti aggiuntive');
        await openDetails(page, 'Traduci i testi mancanti');
        await page.locator('#acClearAiKey').check();
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal(state.config.AiApiKey, '');
        assert.equal(state.config.OllamaCloudApiKey, '');
    } finally { await page.close(); }
});

test('a concurrent edit of the same setting is reported without overwriting it', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'Avanzate');
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
        await select(page, 'Fonti aggiuntive');
        await openDetails(page, 'Traduci i testi mancanti');
        await openDetails(page, 'Indirizzo del servizio e tempi di attesa');
        await page.locator('#acAiEndpoint').fill('https://different.example/v1/chat/completions');
        await page.locator('#acBtnSave').click();
        await page.getByText(/inserisci la nuova chiave/).first().waitFor();
        assert.equal(state.writes.length, 0);
        assert.equal(state.config.AiEndpoint, defaults.AiEndpoint);
    } finally { await page.close(); }
});

test('all pages fit narrow screens and have no duplicate IDs', async () => {
    const { page, state } = await mount();
    try {
        for (const width of [320, 390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            for (const tab of ['Inizio', 'La tua libreria', 'Preferenze', 'Fonti aggiuntive', 'Comunità', 'Avanzate']) {
                await select(page, tab);
                const fits = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
                assert.ok(fits, `${tab} overflows at ${width}px`);
            }
        }
        const duplicates = await page.locator('[id]').evaluateAll(nodes => {
            const ids = nodes.map(n => n.id);
            return ids.filter((id, i) => ids.indexOf(id) !== i);
        });
        assert.deepEqual(duplicates, []);
        assert.deepEqual(state.errors, []);
        await select(page, 'Inizio');
        fs.mkdirSync(path.join(__dirname, 'test-results'), { recursive: true });
        await page.screenshot({ path: path.join(__dirname, 'test-results/home-desktop.png'), fullPage: true });
        await page.setViewportSize({ width: 390, height: 844 });
        await page.screenshot({ path: path.join(__dirname, 'test-results/home-mobile.png'), fullPage: true });
    } finally { await page.close(); }
});

test('library opens with a local scan and shows one metadata category at a time', async () => {
    const { page, state } = await mount({ auditDelay: 400 });
    try {
        await select(page, 'La tua libreria');
        await page.locator('#acLibraryScanProgress:not([hidden])').waitFor();
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        assert.equal(await page.locator('#acLibraryTitles').isVisible(), true);
        assert.equal(await page.locator('#acLibrarySynopses').isVisible(), false);
        await page.locator('#acLibraryMode_synopses').click();
        assert.equal(await page.locator('#acLibraryTitles').isVisible(), false);
        assert.equal(await page.locator('#acLibrarySynopses').isVisible(), true);
        assert.equal(state.titleRuns, 0); assert.equal(state.synopsisRuns, 0);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('title repair sends one start, shows real progress, and can be stopped', async () => {
    const { page, state } = await mount();
    try {
        await select(page, 'La tua libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        await page.locator('#acBtnRunTitles').click();
        await page.getByRole('dialog').getByRole('button', { name: 'Conferma', exact: true }).click();
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'In corso' }).waitFor();
        assert.equal(state.titleRuns, 1);
        assert.equal(await page.locator('#acActivityProgress_titles').getAttribute('value'), '33');
        assert.equal(await page.locator('#acBtnRunTitles').isDisabled(), true);
        await page.getByRole('button', { name: 'Interrompi titoli episodio', exact: true }).click();
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'Interrotta' }).waitFor();
        assert.equal(state.titleRuns, 1);
        assert.deepEqual(state.errors, []);
        fs.mkdirSync(path.join(__dirname, 'test-results'), { recursive: true });
        await page.screenshot({ path: path.join(__dirname, 'test-results/library-desktop.png'), fullPage: true });
        await page.setViewportSize({ width: 390, height: 844 });
        await page.screenshot({ path: path.join(__dirname, 'test-results/library-mobile.png'), fullPage: true });
    } finally { await page.close(); }
});

test('titles absent on AnimeClick can be checked in configured alternatives without promising recovery', async () => {
    const { page, state } = await mount({ titleReport: { episodeTitlesEnabled: true, alternativeTitleLookupEnabled: true,
        seriesCount: 1, episodeCount: 12, missingTitleCount: 12, recoverableTitleCount: 0,
        waitingTitleCount: 0, unavailableTitleCount: 12, series: [] } });
    try {
        await select(page, 'La tua libreria');
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        assert.equal(await page.getByText('Assenti su AnimeClick', { exact: true }).count(), 1);
        assert.equal(await page.locator('#acAuditSummary').getByText('Da verificare', { exact: true }).count(), 1);
        assert.equal(await page.locator('#acBtnRunTitles').isEnabled(), true);
        await page.locator('#acBtnRunTitles').click();
        const dialog = page.getByRole('dialog');
        await dialog.waitFor();
        assert.match(await dialog.innerText(), /12 episodi con titolo mancante/);
        assert.match(await dialog.innerText(), /servizio AI può avere un costo/);
        await dialog.getByRole('button', { name: 'Annulla', exact: true }).click();
        assert.equal(state.titleRuns, 0);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('a running activity is visible after reopening and polling stops outside library', async () => {
    const { page, state } = await mount({ activities: [{ key: 'titles', state: 'Running', isActive: true, total: 10,
        processed: 5, applied: 3, skipped: 2, progress: 50, message: 'Lettura in corso…' }] });
    try {
        await select(page, 'La tua libreria');
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'In corso' }).waitFor();
        await select(page, 'Preferenze');
        const count = state.requests.filter(r => r.target.endsWith('/Activities')).length;
        await page.waitForTimeout(2200);
        assert.equal(state.requests.filter(r => r.target.endsWith('/Activities')).length, count);
        await select(page, 'La tua libreria');
        await page.locator('#acActivityProgress_titles[value="50"]').waitFor();
        assert.equal(state.titleRuns, 0);
    } finally { await page.close(); }
});

test('partial audit and polling errors are visible and retry remains available', async () => {
    const { page } = await mount({ failTitleAudit: true, failActivities: true });
    try {
        await select(page, 'La tua libreria');
        await page.getByText(/Analisi incompleta/).waitFor();
        await page.locator('#acActivityConnection').filter({ hasText: 'Stato non disponibile' }).waitFor();
        assert.equal(await page.locator('#acLibraryScan').isEnabled(), true);
        assert.equal(await page.locator('#acBtnRunTitles').isDisabled(), true);
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
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal(state.config.EnableCommunitySharing, true);
        assert.equal(state.config.CommunityGitHubToken, 'fake-browser-test-token');
        assert.equal(await page.locator('#acCommunityGitHubToken').inputValue(), '');
        await page.locator('#acEnableCommunitySharing').uncheck();
        await page.locator('#acClearCommunityToken').check();
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal(state.config.CommunityGitHubToken, '');
        assert.equal(state.config.EnableCommunitySharing, false);
    } finally { await page.close(); }
});

test('every metadata switch and numeric preference survives save and reload', async () => {
    const { page, state } = await mount();
    try {
        const controls = await page.locator('#acPanelMetadati input[type="checkbox"], #acPanelSinossi input[type="checkbox"]')
            .evaluateAll(nodes => nodes.filter(n => !n.id.startsWith('acClear')).map(n => ({ id: n.id, checked: n.checked })));
        await page.evaluate(controls => controls.forEach(({ id, checked }) => {
            const node = document.getElementById(id); node.checked = !checked; node.dispatchEvent(new Event('change'));
        }), controls);
        const numbers = { acMinPosterWidth: '640', acMaxSearchResults: '12', acCacheHours: '72',
            acNegativeCacheHours: '0', acRequestDelay: '1500', acTranslationCacheHours: '0', acEpisodeTranslationTimeoutSec: '120' };
        await page.evaluate(numbers => Object.entries(numbers).forEach(([id, value]) => {
            const node = document.getElementById(id); node.value = value; node.dispatchEvent(new Event('input'));
        }), numbers);
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        for (const { id, checked } of controls) assert.equal(state.config[id.slice(2)], !checked, id);
        for (const [id, value] of Object.entries(numbers)) assert.equal(await page.locator('#' + id).inputValue(), value, id);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});

test('Fanart keys are editable, verified and preserved alongside the single-provider preferences', async () => {
    const { page, state } = await mount({ config: { FanartPersonalApiKey: 'saved-fanart-personal', FanartProjectApiKey: '', EnableIntegratedMetadata: true, EnableIntegratedImages: true, EnableFanartImages: true } });
    try {
        await select(page, 'Fonti aggiuntive');
        await page.getByText('Aggiungi Fanart', { exact: true }).click();
        assert.equal(await page.locator('#acFanartPersonalApiKey').getAttribute('type'), 'password');
        assert.equal(await page.locator('a[href="https://fanart.tv/get-an-api-key/"]').count(), 1);
        await page.locator('#acFanartPersonalApiKey').fill('edited-fanart-personal');
        await page.locator('#acFanartProjectApiKey').fill('edited-fanart-project');
        await page.getByRole('button', { name: 'Verifica Fanart', exact: true }).click();
        await page.locator('#acInlineResult_fanart').filter({ hasText: '90 immagini' }).waitFor();
        assert.deepEqual(state.fanartTest, { personalApiKey: 'edited-fanart-personal', projectApiKey: 'edited-fanart-project' });
        assert.equal(state.writes.length, 0);
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal(state.config.FanartPersonalApiKey, 'edited-fanart-personal');
        assert.equal(state.config.FanartProjectApiKey, 'edited-fanart-project');
        await select(page, 'Preferenze');
        await page.locator('#acPreferItalianTitle').uncheck();
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal(state.config.FanartPersonalApiKey, 'edited-fanart-personal');
        assert.equal(state.config.EnableIntegratedMetadata, true);
        assert.deepEqual(state.errors, []);
    } finally { await page.close(); }
});
