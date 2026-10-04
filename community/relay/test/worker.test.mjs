import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import worker from '../src/worker.js';
import { canonical, fingerprint, isValidMapping } from '../src/proposal.js';

const fixtures = JSON.parse(readFileSync(new URL('../../fixtures/proposals.json', import.meta.url), 'utf8'));
const saiki = fixtures.valid.find(example => example.name === 'season-saiki-final').mapping;
const INSTALL_A = 'a'.repeat(32);
const INSTALL_B = 'b'.repeat(32);

let env, calls, issues, limited;

function kv() {
    const store = new Map();
    return {
        store,
        get: async key => (store.has(key) ? store.get(key) : null),
        put: async (key, value) => { store.set(key, value); }
    };
}

beforeEach(() => {
    calls = [];
    issues = [];
    limited = false;
    env = {
        REPOSITORY: 'iCosiSenpai/jellyfin-plugin-animeclick',
        GITHUB_TOKEN: 'fake-relay-token',
        INSTALL_SALT: 'salt',
        PROPOSALS: kv(),
        IP_LIMITER: { limit: async () => ({ success: !limited }) }
    };
    globalThis.fetch = async (url, init = {}) => {
        calls.push({ url: String(url), method: init.method || 'GET', body: init.body, headers: init.headers });
        if (String(url).startsWith('https://api.github.com/search/issues')) return Response.json({ total_count: 0, items: [] });
        if (init.method === 'POST') {
            const number = 100 + issues.length;
            issues.push(JSON.parse(init.body));
            return Response.json({ number, html_url: `https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/${number}` }, { status: 201 });
        }
        if (init.method === 'PATCH') return Response.json({});
        return new Response('not found', { status: 404 });
    };
});

function send(mapping, installation = INSTALL_A, extra = {}) {
    const body = JSON.stringify({ schemaVersion: 2, installation, pluginVersion: '1.4.0.0', mapping, ...extra });
    return worker.fetch(new Request('https://relay.example/v1/proposals', {
        method: 'POST', body, headers: { 'content-type': 'application/json', 'cf-connecting-ip': '203.0.113.9' }
    }), env);
}

test('every valid fixture is accepted with the shared canonical text and fingerprint', async () => {
    for (const example of fixtures.valid) {
        assert.ok(isValidMapping(example.mapping), example.name);
        assert.equal(canonical(example.mapping), example.canonical, example.name);
        assert.equal(await fingerprint(example.mapping), example.fingerprint, example.name);
    }
});

test('every invalid fixture is rejected before anything is stored or sent', async () => {
    for (const example of fixtures.invalid) {
        const response = await send(example.mapping);
        assert.equal(response.status, 400, example.name);
    }
    assert.equal(calls.length, 0);
    assert.equal(env.PROPOSALS.store.size, 0);
});

test('extra request fields, a wrong installation code and oversized bodies are refused', async () => {
    assert.equal((await send(saiki, INSTALL_A, { title: 'Saiki' })).status, 400);
    assert.equal((await send(saiki, 'not-a-hex-code')).status, 400);
    const huge = await worker.fetch(new Request('https://relay.example/v1/proposals', { method: 'POST', body: 'x'.repeat(5000) }), env);
    assert.equal(huge.status, 413);
    assert.equal(calls.length, 0);
});

test('a new proposal opens one issue with only validated fields and returns its link', async () => {
    const response = await send(saiki);
    assert.equal(response.status, 201);
    const result = await response.json();
    assert.deepEqual(result, { issue: 100, url: 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/100', duplicate: false });
    const [issue] = issues;
    const print = await fingerprint(saiki);
    assert.equal(issue.title, `[Proposta] Season · AnimeClick 26035 · mapping-${print}`);
    assert.ok(issue.body.includes(canonical(saiki)));
    assert.ok(issue.body.includes('Conferme: 1 installazione'));
    assert.ok(!issue.body.includes(INSTALL_A) && !issue.body.includes('203.0.113.9'), 'neither installation nor address is published');
    const created = calls.find(call => call.method === 'POST');
    assert.equal(created.headers.authorization, 'Bearer fake-relay-token');
    assert.equal(created.url, 'https://api.github.com/repos/iCosiSenpai/jellyfin-plugin-animeclick/issues');
    assert.ok(![...env.PROPOSALS.store.values()].some(value => value.includes('203.0.113.9')), 'the address is never stored');
});

test('the same proposal never opens a second issue and counts each installation once', async () => {
    await send(saiki);
    const again = await send(saiki);
    assert.equal(again.status, 200);
    assert.equal((await again.json()).duplicate, true);
    assert.equal(calls.filter(call => call.method === 'PATCH').length, 0, 'the same installation is not a confirmation');

    const confirmed = await send(saiki, INSTALL_B);
    assert.deepEqual(await confirmed.json(), { issue: 100, url: 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/100', duplicate: true });
    const patch = calls.find(call => call.method === 'PATCH');
    assert.equal(patch.url, 'https://api.github.com/repos/iCosiSenpai/jellyfin-plugin-animeclick/issues/100');
    assert.ok(JSON.parse(patch.body).body.includes('Conferme: 2 installazioni'));
    assert.equal(issues.length, 1);
});

test('an issue opened directly with a token is reused instead of duplicated', async () => {
    const original = globalThis.fetch;
    globalThis.fetch = async (url, init = {}) => String(url).startsWith('https://api.github.com/search/issues')
        ? Response.json({ total_count: 1, items: [{ number: 7, html_url: 'https://github.com/iCosiSenpai/jellyfin-plugin-animeclick/issues/7' }] })
        : original(url, init);
    const response = await send(saiki);
    assert.equal(response.status, 200);
    assert.equal((await response.json()).issue, 7);
    assert.equal(issues.length, 0);
});

test('bursts, the daily installation limit and the global cap answer 429 with a retry time', async () => {
    limited = true;
    const burst = await send(saiki);
    assert.equal(burst.status, 429);
    assert.equal(burst.headers.get('retry-after'), '60');
    limited = false;

    for (let index = 0; index < 20; index++) {
        const mapping = { kind: 'Movie', animeClickId: String(1000 + index), providerIds: { Tmdb: String(5000 + index) } };
        assert.equal((await send(mapping)).status, 201);
    }
    assert.equal((await send(saiki)).status, 429, 'the 21st proposal of the day from one installation waits');

    const day = new Date().toISOString().slice(0, 10);
    await env.PROPOSALS.put(`issues:${day}`, '300');
    assert.equal((await send(saiki, INSTALL_B)).status, 429, 'the global daily cap protects the repository');
});

test('a GitHub outage is reported as temporary so the plugin retries', async () => {
    globalThis.fetch = async url => String(url).includes('/search/') ? Response.json({ total_count: 0, items: [] }) : new Response('down', { status: 502 });
    const response = await send(saiki);
    assert.equal(response.status, 503);
    assert.ok(Number(response.headers.get('retry-after')) > 0);
    assert.equal(env.PROPOSALS.store.size, 0, 'nothing is recorded for a proposal that was not published');
});

test('only POST /v1/proposals is served', async () => {
    assert.equal((await worker.fetch(new Request('https://relay.example/v1/proposals'), env)).status, 405);
    assert.equal((await worker.fetch(new Request('https://relay.example/other', { method: 'POST' }), env)).status, 404);
    assert.equal((await worker.fetch(new Request('https://relay.example/'), env)).status, 200);
});
