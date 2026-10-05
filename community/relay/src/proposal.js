// Validation, canonical text and fingerprint of a community proposal. Kept in step with
// tools/community_mappings.py and Services/AnimeClickCommunityData.cs through the shared
// examples in community/fixtures/proposals.json.

const WORK_PROVIDERS = ['Tmdb', 'Tvdb', 'AniList'];
// AniList identifies a single cour, never the whole series a season belongs to.
const SERIES_PROVIDERS = ['Tmdb', 'Tvdb'];

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const hasExactly = (value, keys) => {
    const own = Object.keys(value).sort();
    return own.length === keys.length && [...keys].sort().every((key, index) => own[index] === key);
};

export function isPublicId(value) {
    return typeof value === 'string' && /^[1-9][0-9]{0,9}$/.test(value);
}

function validIds(ids, allowed) {
    return isObject(ids)
        && Object.keys(ids).length > 0
        && Object.keys(ids).every(key => allowed.includes(key) && isPublicId(ids[key]));
}

function wholeNumber(value, low, high) {
    return Number.isInteger(value) && value >= low && value <= high;
}

export function isValidMapping(mapping) {
    if (!isObject(mapping) || !isPublicId(mapping.animeClickId)) return false;
    if (mapping.kind === 'Series' || mapping.kind === 'Movie') {
        return hasExactly(mapping, ['kind', 'animeClickId', 'providerIds']) && validIds(mapping.providerIds, WORK_PROVIDERS);
    }
    if (mapping.kind === 'Season') {
        return hasExactly(mapping, ['kind', 'animeClickId', 'series', 'seasonNumber', 'episodeCount'])
            && validIds(mapping.series, SERIES_PROVIDERS)
            && wholeNumber(mapping.seasonNumber, 0, 100)
            && wholeNumber(mapping.episodeCount, 1, 2000);
    }
    return false;
}

/** The request a plugin sends: the proposal plus who is sending it, never published. */
export function isValidRequest(body) {
    return isObject(body)
        && hasExactly(body, ['schemaVersion', 'installation', 'pluginVersion', 'mapping'])
        && body.schemaVersion === 2
        && typeof body.installation === 'string' && /^[0-9a-f]{32}$/.test(body.installation)
        && typeof body.pluginVersion === 'string' && /^\d{1,5}(\.\d{1,5}){3}$/.test(body.pluginVersion)
        && isValidMapping(body.mapping);
}

function sortedObject(value) {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, value[key]]));
}

/** Sorted keys, no whitespace: the exact text the plugin and the review tool hash. */
export function canonical(mapping) {
    const ordered = { animeClickId: mapping.animeClickId };
    if (mapping.kind === 'Season') {
        ordered.episodeCount = mapping.episodeCount;
        ordered.kind = mapping.kind;
        ordered.seasonNumber = mapping.seasonNumber;
        ordered.series = sortedObject(mapping.series);
    } else {
        ordered.kind = mapping.kind;
        ordered.providerIds = sortedObject(mapping.providerIds);
    }
    return JSON.stringify(ordered);
}

export async function sha256Hex(text) {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
    return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
}

export async function fingerprint(mapping) {
    return (await sha256Hex(canonical(mapping))).slice(0, 20);
}
