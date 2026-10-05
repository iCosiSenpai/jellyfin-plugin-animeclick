// AnimeClick community relay: receives proposals from plugins without a GitHub account and keeps them
// until the repository's intake workflow collects them, opens one public issue each and reports back.
// The relay holds no GitHub credential; it stores no request bodies and no IP addresses, and keeps
// installations only as keyed hashes, to count confirmations and limit abuse.
import { fingerprint, isValidMapping, isValidRequest, sha256Hex } from './proposal.js';

const MAX_BODY = 2048;
const PER_INSTALLATION_PER_DAY = 20;
const NEW_PROPOSALS_PER_DAY = 300;
const MAX_CONFIRMATIONS = 50;
const MAX_PENDING_BATCH = 50;
const DAY = 24 * 60 * 60;
const ISSUE_URL = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/issues\/(\d{1,9})$/;
const FINGERPRINT = /^[0-9a-f]{20}$/;

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

/** Constant-time comparison of the intake secret, so its length and prefix cannot be probed. */
async function authorized(request, env) {
    const header = request.headers.get('authorization') || '';
    if (!env.ADMIN_SECRET || !header.startsWith('Bearer ')) return false;
    const [given, expected] = await Promise.all([sha256Hex(header.slice(7)), sha256Hex(env.ADMIN_SECRET)]);
    let difference = 0;
    for (let index = 0; index < expected.length; index++) difference |= given.charCodeAt(index) ^ expected.charCodeAt(index);
    return difference === 0;
}

async function readBody(request) {
    const length = Number(request.headers.get('content-length') || 0);
    if (length > MAX_BODY) return { error: json(413, { error: 'Richiesta troppo grande.' }) };
    const text = await request.text();
    if (text.length > MAX_BODY) return { error: json(413, { error: 'Richiesta troppo grande.' }) };
    try { return { body: JSON.parse(text) }; } catch { return { error: json(400, { error: 'JSON non valido.' }) }; }
}

async function handleProposal(request, env) {
    const { body, error } = await readBody(request);
    if (error) return error;
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
    const published = await readJson(env.PROPOSALS, `proposal:${print}`);
    if (published) {
        // Already an issue: count the confirmation for the next report, never a second issue.
        if (!published.installations.includes(installation) && published.installations.length < MAX_CONFIRMATIONS) {
            published.installations.push(installation);
            await env.PROPOSALS.put(`proposal:${print}`, JSON.stringify(published));
            await bump(env.PROPOSALS, installKey);
        }
        return json(200, { issue: published.issue, url: published.url, duplicate: true });
    }

    const pendingKey = `pending:${print}`;
    const pending = await readJson(env.PROPOSALS, pendingKey);
    if (pending) {
        if (!pending.installations.includes(installation) && pending.installations.length < MAX_CONFIRMATIONS) {
            pending.installations.push(installation);
            await env.PROPOSALS.put(pendingKey, JSON.stringify(pending));
            await bump(env.PROPOSALS, installKey);
        }
        return json(202, { queued: true, fingerprint: print, duplicate: true });
    }

    const globalKey = `proposals:${day}`;
    if ((Number(await env.PROPOSALS.get(globalKey)) || 0) >= NEW_PROPOSALS_PER_DAY) {
        return json(429, { error: 'Il servizio ha raggiunto il limite di oggi: riprova domani.' }, { 'retry-after': String(DAY / 2) });
    }

    await env.PROPOSALS.put(pendingKey, JSON.stringify({ mapping: body.mapping, installations: [installation], receivedAt: new Date().toISOString() }));
    await bump(env.PROPOSALS, globalKey);
    await bump(env.PROPOSALS, installKey);
    return json(202, { queued: true, fingerprint: print, duplicate: false });
}

/** For the intake workflow: the proposals waiting for an issue. */
async function handlePending(env) {
    const listed = await env.PROPOSALS.list({ prefix: 'pending:', limit: MAX_PENDING_BATCH });
    const proposals = [];
    for (const key of listed.keys) {
        const record = await readJson(env.PROPOSALS, key.name);
        const print = key.name.slice('pending:'.length);
        if (!record || !isValidMapping(record.mapping) || await fingerprint(record.mapping) !== print) {
            await env.PROPOSALS.delete(key.name);
            continue;
        }
        proposals.push({ fingerprint: print, mapping: record.mapping, confirmations: record.installations.length });
    }
    return json(200, { proposals });
}

/** For the intake workflow: the issue a pending proposal became. */
async function handlePublished(request, env) {
    const { body, error } = await readBody(request);
    if (error) return error;
    const match = body && typeof body.url === 'string' ? ISSUE_URL.exec(body.url) : null;
    if (!body || !FINGERPRINT.test(body.fingerprint || '') || !match || Number(match[1]) !== body.issue) {
        return json(400, { error: 'Pubblicazione non valida.' });
    }
    const pending = await readJson(env.PROPOSALS, `pending:${body.fingerprint}`);
    const existing = await readJson(env.PROPOSALS, `proposal:${body.fingerprint}`);
    const installations = [...new Set([...(existing?.installations || []), ...(pending?.installations || [])])];
    await env.PROPOSALS.put(`proposal:${body.fingerprint}`, JSON.stringify({ issue: body.issue, url: body.url, installations }));
    await env.PROPOSALS.delete(`pending:${body.fingerprint}`);
    return json(200, { ok: true });
}

/** For plugins: whether a proposal already has its issue. */
async function handleState(env, print) {
    if (!FINGERPRINT.test(print)) return json(400, { error: 'Impronta non valida.' });
    const published = await readJson(env.PROPOSALS, `proposal:${print}`);
    if (published) return json(200, { state: 'published', issue: published.issue, url: published.url });
    if (await env.PROPOSALS.get(`pending:${print}`)) return json(200, { state: 'pending' });
    return json(404, { state: 'unknown' });
}

export default {
    async fetch(request, env) {
        const { pathname } = new URL(request.url);
        try {
            if (pathname === '/' && request.method === 'GET') {
                return new Response('AnimeClick community relay. Proposals: POST /v1/proposals\n', { headers: { 'content-type': 'text/plain; charset=utf-8' } });
            }
            if (pathname === '/v1/proposals') {
                return request.method === 'POST' ? await handleProposal(request, env) : json(405, { error: 'Usa POST.' }, { allow: 'POST' });
            }
            if (pathname.startsWith('/v1/proposals/') && request.method === 'GET') {
                return await handleState(env, pathname.slice('/v1/proposals/'.length));
            }
            if (pathname === '/v1/pending' || pathname === '/v1/published') {
                if (!(await authorized(request, env))) return json(401, { error: 'Non autorizzato.' });
                if (pathname === '/v1/pending' && request.method === 'GET') return await handlePending(env);
                if (pathname === '/v1/published' && request.method === 'POST') return await handlePublished(request, env);
                return json(405, { error: 'Metodo non ammesso.' });
            }
            return json(404, { error: 'Non trovato.' });
        } catch {
            return json(503, { error: 'Servizio temporaneamente non disponibile.' }, { 'retry-after': '600' });
        }
    }
};
