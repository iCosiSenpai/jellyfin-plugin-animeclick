import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import worker from '../src/worker.js';
import { canonical, fingerprint, isValidMapping } from '../src/proposal.js';

const fixtures = JSON.parse(readFileSync(new URL('../../fixtures/proposals.json', import.meta.url), 'utf8'));
const saiki = fixtures.valid.find(example => example.name === 'season-saiki-final').mapping;
const INSTALL_A = 'a'.repeat(32);
const INSTALL_B = 'b'.repeat(32);
const SECRET = 'intake-secret-for-tests';
const ISSUE = 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/';

let env, limited;

function kv() {
    const store = new Map();
    return {
        store,
        get: async key => (store.has(key) ? store.get(key) : null),
        put: async (key, value) => { store.set(key, value); },
        delete: async key => { store.delete(key); },
        list: async ({ prefix, limit }) => ({ keys: [...store.keys()].filter(key => key.startsWith(prefix)).slice(0, limit).map(name => ({ name })) })
    };
}

beforeEach(() => {
    limited = false;
    env = {
        REPOSITORY: 'iCosiSenpai/jellyfin-plugin-animeclick',
        INSTALL_SALT: 'salt',
        ADMIN_SECRET: SECRET,
        PROPOSALS: kv(),
        IP_LIMITER: { limit: async () => ({ success: !limited }) }
    };
    globalThis.fetch = async () => { throw new Error('the relay must not call any other service'); };
});

const call = (path, init = {}) => worker.fetch(new Request('https://relay.example' + path, init), env);

function send(mapping, installation = INSTALL_A, extra = {}) {
    const body = JSON.stringify({ schemaVersion: 2, installation, pluginVersion: '1.5.0.0', mapping, ...extra });
    return call('/v1/proposals', { method: 'POST', body, headers: { 'content-type': 'application/json', 'cf-connecting-ip': '203.0.113.9' } });
}

const admin = (path, init = {}) => call(path, { ...init, headers: { ...(init.headers || {}), authorization: 'Bearer ' + SECRET } });
const publish = (print, issue) => admin('/v1/published', { method: 'POST', body: JSON.stringify({ fingerprint: print, issue, url: ISSUE + issue }) });

test('every valid fixture is accepted with the shared canonical text and fingerprint', async () => {
    for (const example of fixtures.valid) {
        assert.ok(isValidMapping(example.mapping), example.name);
        assert.equal(canonical(example.mapping), example.canonical, example.name);
        assert.equal(await fingerprint(example.mapping), example.fingerprint, example.name);
    }
});

test('every invalid fixture is rejected before anything is stored', async () => {
    for (const example of fixtures.invalid) {
        assert.equal((await send(example.mapping)).status, 400, example.name);
    }
    assert.equal(env.PROPOSALS.store.size, 0);
});

test('extra request fields, a wrong installation code and oversized bodies are refused', async () => {
    assert.equal((await send(saiki, INSTALL_A, { title: 'Saiki' })).status, 400);
    assert.equal((await send(saiki, 'not-a-hex-code')).status, 400);
    assert.equal((await call('/v1/proposals', { method: 'POST', body: 'x'.repeat(5000) })).status, 413);
    assert.equal(env.PROPOSALS.store.size, 0);
});

test('a proposal waits for the intake, which sees it once with its confirmations', async () => {
    const first = await send(saiki);
    assert.equal(first.status, 202);
    const print = await fingerprint(saiki);
    assert.deepEqual(await first.json(), { queued: true, fingerprint: print, duplicate: false });
    assert.equal((await send(saiki)).status, 202);
    assert.equal((await send(saiki, INSTALL_B)).status, 202);

    const pending = await (await admin('/v1/pending')).json();
    assert.deepEqual(pending, { proposals: [{ fingerprint: print, mapping: saiki, confirmations: 2 }] });
    assert.deepEqual(await (await call('/v1/proposals/' + print)).json(), { state: 'pending' });
    const stored = [...env.PROPOSALS.store.values()].join('\n');
    assert.ok(!stored.includes(INSTALL_A) && !stored.includes('203.0.113.9'), 'neither installation nor address is stored in clear');
});

test('once published, plugins get the issue and nobody opens a second one', async () => {
    await send(saiki);
    const print = await fingerprint(saiki);
    assert.equal((await publish(print, 41)).status, 200);
    assert.deepEqual(await (await admin('/v1/pending')).json(), { proposals: [] });
    assert.deepEqual(await (await call('/v1/proposals/' + print)).json(), { state: 'published', issue: 41, url: ISSUE + 41 });
    assert.deepEqual(await (await send(saiki, INSTALL_B)).json(), { issue: 41, url: ISSUE + 41, duplicate: true });
    assert.equal((await call('/v1/proposals/' + 'f'.repeat(20))).status, 404);
    assert.equal((await call('/v1/proposals/not-a-print')).status, 400);
});

test('the intake endpoints need the secret and validate what they are told', async () => {
    assert.equal((await call('/v1/pending')).status, 401);
    assert.equal((await call('/v1/pending', { headers: { authorization: 'Bearer wrong' } })).status, 401);
    assert.equal((await call('/v1/published', { method: 'POST', body: '{}' })).status, 401);
    const print = await fingerprint(saiki);
    assert.equal((await admin('/v1/published', { method: 'POST', body: JSON.stringify({ fingerprint: print, issue: 5, url: 'https://evil.example/issues/5' }) })).status, 400);
    assert.equal((await admin('/v1/published', { method: 'POST', body: JSON.stringify({ fingerprint: print, issue: 6, url: ISSUE + 5 }) })).status, 400);
    assert.equal((await admin('/v1/published', { method: 'POST', body: JSON.stringify({ fingerprint: 'x', issue: 5, url: ISSUE + 5 }) })).status, 400);
    delete env.ADMIN_SECRET;
    assert.equal((await admin('/v1/pending')).status, 401, 'without a configured secret nothing is served');
});

test('a corrupted pending record is dropped instead of reaching the repository', async () => {
    await env.PROPOSALS.put('pending:' + 'a'.repeat(20), JSON.stringify({ mapping: { kind: 'Series', animeClickId: '1', providerIds: { Tmdb: '1' } }, installations: [] }));
    await env.PROPOSALS.put('pending:' + 'b'.repeat(20), 'not json');
    assert.deepEqual(await (await admin('/v1/pending')).json(), { proposals: [] });
    assert.equal(env.PROPOSALS.store.size, 0);
});

test('bursts, the daily installation limit and the global cap answer 429 with a retry time', async () => {
    limited = true;
    const burst = await send(saiki);
    assert.equal(burst.status, 429);
    assert.equal(burst.headers.get('retry-after'), '60');
    limited = false;

    for (let index = 0; index < 20; index++) {
        const mapping = { kind: 'Movie', animeClickId: String(1000 + index), providerIds: { Tmdb: String(5000 + index) } };
        assert.equal((await send(mapping)).status, 202);
    }
    assert.equal((await send(saiki)).status, 429, 'the 21st proposal of the day from one installation waits');

    const day = new Date().toISOString().slice(0, 10);
    await env.PROPOSALS.put(`proposals:${day}`, '300');
    assert.equal((await send(saiki, INSTALL_B)).status, 429, 'the global daily cap protects the repository');
});

test('only the documented routes are served', async () => {
    assert.equal((await call('/v1/proposals')).status, 405);
    assert.equal((await call('/other', { method: 'POST' })).status, 404);
    assert.equal((await call('/')).status, 200);
    assert.equal((await admin('/v1/pending', { method: 'POST' })).status, 405);
});
