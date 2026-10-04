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
    // Public server information is available before Jellyfin finishes migrations.
    for (let attempt = 0; attempt < 60; attempt++) {
        const response = await fetch(new URL('/Plugins/AnimeClick/AiProviders', origin));
        if (response.status !== 503) {
            assert.equal(response.status, 401, 'Anonymous plugin API access denied');
            break;
        }
        if (attempt === 59) assert.fail('Jellyfin did not finish startup');
        await new Promise(resolve => setTimeout(resolve, 1000));
    }
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
    assert.equal(plugin?.Version, '1.3.0.0');
    assert.equal(plugin.Status, 'Active');
    const configPage = (await call('/web/ConfigurationPages')).find(page => page.Name === 'AnimeClick Plugin');
    assert.equal(configPage?.EnableInMainMenu, true);
    assert.equal(configPage?.MenuIcon, 'movie');
    const openapi = await call('/api-docs/openapi.json');
    assert.ok(openapi.paths['/Items/{itemId}/RemoteImages'], 'Plugin DTOs must not break Jellyfin OpenAPI');
    const config = await call('/Plugins/' + pluginId + '/Configuration');
    assert.equal(config.EnableEpisodeSynopsisTranslation, true);
    assert.equal(config.EnableEpisodeTitleFallback, true);
    assert.equal(config.AiModel, '');
    assert.equal(config.EnableCommunityMappings, false);
    assert.equal(config.EnableCommunitySharing, false);
    assert.equal(config.CommunityGitHubToken, '');
    assert.equal(config.SetupCompletedVersion, 0, 'A fresh installation starts with the full guided setup');
    assert.equal(config.ConfigurationVersion, 2);
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
    const emptyShowcase = await call('/Plugins/AnimeClick/Showcase?limit=8');
    assert.equal((emptyShowcase.Items ?? emptyShowcase.items).length, 0);
    assert.deepEqual(await call('/Plugins/AnimeClick/Catalog'), []);
    await call('/Plugins/AnimeClick/Libraries/Enable', { LibraryId: 'not-a-library' }, 400);
    // A disposable TV library whose provider list does not include AnimeClick yet.
    const libraryName = 'Anime di prova';
    if (!(await call('/Library/VirtualFolders')).some(folder => folder.Name === libraryName)) {
        const typeOptions = ['Series', 'Season', 'Episode'].map(type => ({ Type: type, MetadataFetchers: ['TheMovieDb'], MetadataFetcherOrder: ['TheMovieDb'], ImageFetchers: ['TheMovieDb'], ImageFetcherOrder: ['TheMovieDb'] }));
        await call('/Library/VirtualFolders?name=' + encodeURIComponent(libraryName) + '&collectionType=tvshows&paths=%2Fmedia%2Fanime&refreshLibrary=false',
            { LibraryOptions: { TypeOptions: typeOptions } }, 204);
    }
    const libraries = await call('/Plugins/AnimeClick/Libraries');
    const disposable = libraries.find(library => (library.Name ?? library.name) === libraryName);
    assert.ok(disposable, 'The plugin lists the disposable library: ' + JSON.stringify(libraries));
    assert.equal(disposable.State ?? disposable.state, 'inactive');
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
    await call('/Plugins/AnimeClick/TestFanart', {}, 403);
    await call('/Plugins/AnimeClick/Libraries', undefined, 403);
    await call('/Plugins/AnimeClick/Libraries/Enable', { LibraryId: randomUUID() }, 403);
    await call('/Plugins/AnimeClick/Showcase', undefined, 403);
    token = adminToken;
    assert.equal((await call('/Plugins/AnimeClick/TestFanart', {})).Success, false);
    const integratedConfig = await call('/Plugins/' + pluginId + '/Configuration');
    assert.equal(integratedConfig.EnableIntegratedMetadata, true);
    assert.equal(integratedConfig.EnableIntegratedImages, true);
    assert.equal(integratedConfig.EnableFanartImages, true);
    assert.equal(integratedConfig.FanartPersonalApiKey, '');
    for (const [content, types] of [['tvshows', ['Series', 'Season', 'Episode']], ['movies', ['Movie']]]) {
        const available = await call('/Libraries/AvailableOptions?libraryContentType=' + content);
        for (const type of types) {
            const options = available.TypeOptions.find(option => option.Type === type);
            assert.ok(options, type + ' is supported');
            assert.equal(options.MetadataFetchers.filter(provider => provider.Name === 'AnimeClick').length, 1, type + ' has one AnimeClick metadata provider');
            assert.equal(options.ImageFetchers.filter(provider => provider.Name === 'AnimeClick').length, 1, type + ' has one AnimeClick image provider');
        }
    }

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
        // A fresh installation opens on the guided setup.
        await page.getByRole('heading', { name: 'I tuoi anime, in italiano.' }).waitFor({ timeout: 30000 });
        await page.screenshot({ path: 'test-results/runtime-setup.png', fullPage: true });
        await page.getByRole('button', { name: 'Iniziamo' }).click();
        await page.locator('#acSetupNext').click();
        await page.getByRole('button', { name: 'Salva e continua' }).click();
        const libraryBox = page.locator('#acSetupLibraries input[type=checkbox]').first();
        await libraryBox.waitFor({ timeout: 30000 });
        assert.equal(await libraryBox.isChecked(), true, 'Libraries named after anime are preselected');
        await page.locator('#acSetupEnable').click();
        await page.getByRole('dialog').getByRole('button', { name: 'Attiva', exact: true }).click();
        await page.locator('#acSetupLibrariesResult').filter({ hasText: 'Fatto!' }).waitFor({ timeout: 30000 });
        const folder = (await call('/Library/VirtualFolders')).find(item => item.Name === libraryName);
        for (const type of ['Series', 'Season', 'Episode']) {
            const options = folder.LibraryOptions.TypeOptions.find(option => option.Type === type);
            assert.equal(options.MetadataFetcherOrder[0], 'AnimeClick', type + ' puts AnimeClick first');
            assert.ok(options.MetadataFetchers.includes('TheMovieDb'), type + ' keeps the other providers');
            assert.ok(options.ImageFetchers.includes('AnimeClick'), type + ' offers AnimeClick images');
        }
        await page.locator('#acSetupNext').click();
        await page.locator('#acSetupFinish').click();
        await page.locator('#acSetup').waitFor({ state: 'hidden' });
        assert.equal((await call('/Plugins/' + pluginId + '/Configuration')).SetupCompletedVersion, 2);
        await page.locator('#acHomeLibraries').getByText(libraryName, { exact: true }).waitFor({ timeout: 30000 });
        await page.waitForTimeout(800);
        await page.screenshot({ path: 'test-results/runtime-home.png', fullPage: true });
        await page.getByRole('tab', { name: 'Preferenze', exact: true }).click();
        const expectedTitlePreference = !await page.locator('#acPreferItalianTitle').isChecked();
        await page.locator('#acPreferItalianTitle').setChecked(expectedTitlePreference);
        await page.locator('#acBtnSave').click();
        await page.waitForFunction(() => document.querySelector('#acSaveBar').hidden);
        assert.equal((await call('/Plugins/' + pluginId + '/Configuration')).PreferItalianTitle, expectedTitlePreference);
        await page.getByRole('tab', { name: 'Libreria', exact: true }).click();
        await page.getByText(/Analisi aggiornata alle/).waitFor();
        await page.screenshot({ path: 'test-results/runtime-library.png', fullPage: true });
        await page.locator('#acBtnRunTitles').click();
        await page.getByRole('dialog').getByRole('button', { name: 'Avvia', exact: true }).click();
        await page.locator('#acActivityBadge_titles').filter({ hasText: 'Completata' }).waitFor({ timeout: 30000 });
        assert.equal((await call('/Plugins/AnimeClick/Activities')).find(a => a.Key === 'titles').State, 'Completed');
        await page.getByRole('tab', { name: 'Fonti', exact: true }).click();
        await page.screenshot({ path: 'test-results/runtime-sources.png', fullPage: true });
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
