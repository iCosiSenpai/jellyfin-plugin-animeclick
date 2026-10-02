// Destructive only to a NEW disposable localhost Jellyfin instance. Never use production data.
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const { chromium } = require('playwright');

async function main() {
    const origin = new URL(process.argv[2]);
    assert.equal(origin.hostname, '127.0.0.1', 'Only disposable localhost instances are accepted');
    assert.notEqual(origin.port, '8096', 'The default production port is forbidden');
    const pluginId = '1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11';
    // Deliberately non-secret test credentials: the server is loopback-only and disposable.
    const password = 'AnimeClick-isolated-' + origin.port;
    const authorization = 'MediaBrowser Client="AnimeClick smoke", Device="Isolated test", DeviceId="animeclick-smoke", Version="1.0.0"';
    let token;
    async function call(route, data, status = 200, authenticated = true) {
        const response = await fetch(new URL(route, origin), {
            method: data === undefined ? 'GET' : 'POST',
            headers: { 'Content-Type': 'application/json', Authorization: authorization + (authenticated && token ? `, Token="${token}"` : ''),
                ...(authenticated && token ? { 'X-Emby-Token': token } : {}) },
            body: data === undefined ? undefined : JSON.stringify(data),
            signal: AbortSignal.timeout(15000)
        });
        assert.equal(response.status, status, route + ' status');
        const text = await response.text();
        try { return JSON.parse(text); } catch { return text; }
    }
    let info;
    for (let attempt = 0; attempt < 60; attempt++) {
        try { info = await call('/System/Info/Public'); break; }
        catch (error) {
            if (attempt === 59) throw error;
            await new Promise(resolve => setTimeout(resolve, 1000));
        }
    }
    if (info.StartupWizardCompleted) {
        assert.ok(process.argv.includes('--resume'), 'Refusing to modify an already configured server without --resume');
        assert.equal(info.ServerName, 'AnimeClick isolated audit', 'Refusing a non-test server');
    }
    await call('/Plugins/AnimeClick/AiProviders', undefined, 401, false);
    if (!info.StartupWizardCompleted) {
        await call('/Startup/User');
        await call('/Startup/User', { Name: 'animeclick-smoke', Password: password }, 204);
        await call('/Startup/Configuration', { ServerName: 'AnimeClick isolated audit', MetadataCountryCode: 'IT', PreferredMetadataLanguage: 'it', UICulture: 'it' }, 204);
        await call('/Startup/Complete', {}, 204);
    }
    const auth = await call('/Users/AuthenticateByName', { Username: 'animeclick-smoke', Pw: password });
    token = auth.AccessToken;
    assert.ok(token);
    const plugins = await call('/Plugins');
    const plugin = plugins.find(p => p.Id?.replaceAll('-', '').toLowerCase() === pluginId.replaceAll('-', ''));
    assert.ok(plugin, 'Plugin is absent: ' + JSON.stringify(plugins));
    assert.equal(plugin?.Version, '1.1.2.0');
    assert.equal(plugin.Status, 'Active');
    const config = await call('/Plugins/' + pluginId + '/Configuration');
    assert.equal(config.EnableEpisodeSynopsisTranslation, true);
    assert.equal(config.EnableEpisodeTitleFallback, true);
    assert.equal(config.AiModel, '');
    assert.equal(config.EnableCommunityMappings, false);
    assert.equal(config.EnableCommunitySharing, false);
    assert.equal(config.CommunityGitHubToken, '');
    await call('/Plugins/' + pluginId + '/Configuration', { ...config, NegativeCacheHours: 0, EnableEpisodeTitleFallback: false }, 204);
    assert.equal((await call('/Plugins/' + pluginId + '/Configuration')).NegativeCacheHours, 0);
    assert.equal((await call('/Plugins/' + pluginId + '/Configuration')).EnableEpisodeTitleFallback, false);
    await call('/Plugins/' + pluginId + '/Configuration', { ...config, NegativeCacheHours: 0 }, 204);
    const providers = await call('/Plugins/AnimeClick/AiProviders');
    assert.ok(providers.length > 1);
    await call('/Plugins/AnimeClick/LibraryQualityAudit');
    await call('/Plugins/AnimeClick/LibraryAudit');
    const activities = await call('/Plugins/AnimeClick/Activities');
    assert.equal(activities.length, 2);
    await call('/Plugins/AnimeClick/CancelActivity', { Key: 'invalid' }, 400);
    await call('/Plugins/AnimeClick/Community/Status');
    const exported = await call('/Plugins/AnimeClick/Community/Export');
    assert.deepEqual(exported, { schemaVersion: 1, mappings: [] });
    await call('/Plugins/AnimeClick/Community/Preview?itemId=invalid', undefined, 400);
    await call('/Plugins/AnimeClick/Community/Retry', {});
    await call('/Plugins/AnimeClick/IdentifyStatus?itemId=invalid', undefined, 400);
    await call('/Plugins/AnimeClick/IdentifyAndRefresh', { ItemId: randomUUID(), AnimeClickId: 'https://evil.invalid/anime/1/x' }, 400);
    await call('/Plugins/AnimeClick/IdentifyAndRefresh', { ItemId: randomUUID(), AnimeClickId: '72/naruto' }, 404);
    const existingUsers = await call('/Users');
    const ordinary = existingUsers.find(user => user.Name === 'ordinary-smoke')
        || await call('/Users/New', { Name: 'ordinary-smoke' });
    const ordinaryAuth = await call('/Users/AuthenticateByName', { Username: ordinary.Name, Pw: '' });
    const adminToken = token;
    token = ordinaryAuth.AccessToken;
    await call('/Plugins/AnimeClick/AiProviders', undefined, 403);
    await call('/Plugins/AnimeClick/Activities', undefined, 403);
    await call('/Plugins/AnimeClick/Community/Status', undefined, 403);
    await call('/Plugins/AnimeClick/Community/Export', undefined, 403);
    await call('/Plugins/AnimeClick/RunMissingTitlesTask', {}, 403);
    token = adminToken;

    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const errors = [];
        page.on('pageerror', e => errors.push({ message: e.message, stack: e.stack }));
        await page.goto(new URL('/web/#/login', origin).href);
        await page.locator('#loginPage').waitFor({ timeout: 30000 });
        await page.locator('#txtManualName').waitFor({ timeout: 30000 });
        await page.locator('#txtManualName').fill('animeclick-smoke');
        await page.locator('#txtManualPassword').fill(password);
        await page.locator('.manualLoginForm button[type=submit]').click();
        try {
            await page.waitForURL(url => !url.hash.includes('login'), { timeout: 30000 });
        } catch (error) {
            console.error('Web sign-in state:', page.url(), (await page.locator('body').innerText()).slice(0, 1200), errors);
            throw error;
        }
        await page.goto(new URL('/web/#/configurationpage?name=AnimeClick%20Plugin', origin).href);
        await page.getByRole('heading', { name: 'Più storie. Meno impostazioni.' }).waitFor({ timeout: 30000 });
        await page.waitForFunction(() => document.querySelector('#acLoadState')?.hidden);
        await page.getByRole('tab', { name: 'Preferenze', exact: true }).click();
        const expectedTitlePreference = !await page.locator('#acPreferItalianTitle').isChecked();
        await page.locator('#acPreferItalianTitle').setChecked(expectedTitlePreference);
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').style.display === 'none');
        assert.equal((await call('/Plugins/' + pluginId + '/Configuration')).PreferItalianTitle, expectedTitlePreference);
        await page.getByRole('tab', { name: 'La tua libreria', exact: true }).click();
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        await page.locator('#acBtnRunTitles').click();
        await page.getByRole('dialog').getByRole('button', { name: 'Conferma', exact: true }).click();
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'Completata' }).waitFor({ timeout: 30000 });
        assert.equal((await call('/Plugins/AnimeClick/Activities')).find(a => a.Key === 'titles').State, 'Completed');
        await page.getByRole('tab', { name: 'Comunità', exact: true }).click();
        assert.equal(await page.locator('#acEnableCommunitySharing').isChecked(), false);
        await page.locator('#acCommunityStatus').click();
        await page.locator('#acCommunityResult').filter({ hasText: 'disattivata' }).waitFor();
        // The host cancels outstanding home-page requests on navigation. Report that
        // separately; do not hide any other browser exception or plugin failure.
        const cancellations = errors.filter(e => e.message === 'CancelledError');
        if (cancellations.length) console.log('Jellyfin navigation cancellations: ' + cancellations.length);
        assert.deepEqual(errors.filter(e => e.message !== 'CancelledError'), []);
    } finally { await browser.close(); }
    console.log(`Jellyfin ${info.Version}: plugin load, configuration, admin APIs, access controls and real web UI PASS`);
}
// A prematurely drained event loop must never produce a false successful smoke test.
process.exitCode = 1;
main().then(() => { process.exitCode = 0; }, error => { console.error(error); });
