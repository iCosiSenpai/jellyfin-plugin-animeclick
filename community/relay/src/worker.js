// AnimeClick community relay: receives proposals from plugins without a GitHub account and opens
// one public issue per proposal in the plugin repository. It stores no request bodies and no IP
// addresses; installations are kept only as keyed hashes, to count confirmations and limit abuse.
import { fingerprint, isValidRequest, issueBody, issueTitle, sha256Hex } from './proposal.js';

const MAX_BODY = 2048;
const PER_INSTALLATION_PER_DAY = 20;
const NEW_ISSUES_PER_DAY = 300;
const MAX_CONFIRMATIONS = 50;
const DAY = 24 * 60 * 60;

const json = (status, body, headers = {}) => new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...headers }
});

const today = () => new Date().toISOString().slice(0, 10);

async function readJson(kv, key) {
    const text = await kv.get(key);
    if (!text) return null;
    try { return JSON.parse(text); } catch { return null; }
}

async function bump(kv, key) {
    const count = Number(await kv.get(key)) || 0;
    await kv.put(key, String(count + 1), { expirationTtl: 2 * DAY });
    return count + 1;
}

function github(env, path, init = {}) {
    return fetch(`https://api.github.com/repos/${env.REPOSITORY}${path}`, {
        ...init,
        redirect: 'manual',
        headers: {
            authorization: `Bearer ${env.GITHUB_TOKEN}`,
            accept: 'application/vnd.github+json',
            'user-agent': 'AnimeClick-Community-Relay/1.0',
            'x-github-api-version': '2022-11-28',
            ...(init.body ? { 'content-type': 'application/json' } : {})
        }
    });
}

/** An issue another channel already opened for the same proposal, if GitHub can find it. */
async function findIssue(env, print) {
    const query = encodeURIComponent(`repo:${env.REPOSITORY} in:title mapping-${print}`);
    const response = await fetch(`https://api.github.com/search/issues?q=${query}`, {
        redirect: 'manual',
        headers: { authorization: `Bearer ${env.GITHUB_TOKEN}`, accept: 'application/vnd.github+json', 'user-agent': 'AnimeClick-Community-Relay/1.0' }
    });
    if (!response.ok) return null;
    const result = await response.json();
    const issue = result.items && result.items[0];
    return issue ? { issue: issue.number, url: issue.html_url } : null;
}

async function handleProposal(request, env) {
    const length = Number(request.headers.get('content-length') || 0);
    if (length > MAX_BODY) return json(413, { error: 'Proposta troppo grande.' });
    const text = await request.text();
    if (text.length > MAX_BODY) return json(413, { error: 'Proposta troppo grande.' });
    let body;
    try { body = JSON.parse(text); } catch { return json(400, { error: 'JSON non valido.' }); }
    if (!isValidRequest(body)) return json(400, { error: 'Proposta non valida.' });

    // Bursts from one address are cut by Cloudflare's limiter; the address itself is never stored.
    const address = request.headers.get('cf-connecting-ip') || 'unknown';
    if (env.IP_LIMITER) {
        const { success } = await env.IP_LIMITER.limit({ key: address });
        if (!success) return json(429, { error: 'Troppe proposte: riprova tra poco.' }, { 'retry-after': '60' });
    }

    const day = today();
    const installation = await sha256Hex(`${env.INSTALL_SALT}:${body.installation}`);
    const installKey = `install:${day}:${installation.slice(0, 32)}`;
    if ((Number(await env.PROPOSALS.get(installKey)) || 0) >= PER_INSTALLATION_PER_DAY) {
        return json(429, { error: 'Limite giornaliero raggiunto: le proposte ripartiranno domani.' }, { 'retry-after': String(DAY) });
    }

    const print = await fingerprint(body.mapping);
    const recordKey = `proposal:${print}`;
    let record = await readJson(env.PROPOSALS, recordKey);
    if (!record) {
        const existing = await findIssue(env, print);
        if (existing) record = { ...existing, installations: [] };
    }

    if (record) {
        // Same proposal again: count a confirmation from a new installation, never a new issue.
        if (!record.installations.includes(installation) && record.installations.length < MAX_CONFIRMATIONS) {
            record.installations.push(installation);
            await env.PROPOSALS.put(recordKey, JSON.stringify(record));
            await bump(env.PROPOSALS, installKey);
            await github(env, `/issues/${record.issue}`, {
                method: 'PATCH',
                body: JSON.stringify({ body: issueBody(body.mapping, record.installations.length) })
            }).catch(() => null);
        }
        return json(200, { issue: record.issue, url: record.url, duplicate: true });
    }

    const globalKey = `issues:${day}`;
    if ((Number(await env.PROPOSALS.get(globalKey)) || 0) >= NEW_ISSUES_PER_DAY) {
        return json(429, { error: 'Il servizio ha raggiunto il limite di oggi: riprova domani.' }, { 'retry-after': String(DAY / 2) });
    }

    const created = await github(env, '/issues', {
        method: 'POST',
        body: JSON.stringify({ title: issueTitle(body.mapping, print), body: issueBody(body.mapping, 1), labels: ['proposta'] })
    });
    if (!created.ok) return json(503, { error: 'GitHub non disponibile: riprova più tardi.' }, { 'retry-after': '600' });
    const issue = await created.json();
    record = { issue: issue.number, url: issue.html_url, installations: [installation] };
    await env.PROPOSALS.put(recordKey, JSON.stringify(record));
    await bump(env.PROPOSALS, globalKey);
    await bump(env.PROPOSALS, installKey);
    return json(201, { issue: record.issue, url: record.url, duplicate: false });
}

export default {
    async fetch(request, env) {
        const { pathname } = new URL(request.url);
        if (pathname === '/' && request.method === 'GET') {
            return new Response('AnimeClick community relay. Proposals: POST /v1/proposals\n', { headers: { 'content-type': 'text/plain; charset=utf-8' } });
        }
        if (pathname !== '/v1/proposals') return json(404, { error: 'Non trovato.' });
        if (request.method !== 'POST') return json(405, { error: 'Usa POST.' }, { allow: 'POST' });
        try {
            return await handleProposal(request, env);
        } catch {
            return json(503, { error: 'Servizio temporaneamente non disponibile.' }, { 'retry-after': '600' });
        }
    }
};
