import { parseUniqueJson } from './classification-request.mjs';

const limit = 128 * 1024;
const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, keys) => object(value) && Object.keys(value).every(key => keys.includes(key));
const text = value => typeof value === 'string' && value.length >= 1 && value.length <= 16384 &&
    !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value) && [...value].length <= 8192;
const timestamp = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value) &&
    Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value;

// Admission only: authenticated identity, privacy policy, quotas and delivery are separate gates.
export function parseKnowledgeEmbeddingRequest(bytes, now) {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength > limit) throw new TypeError('Invalid embedding request.');
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.knowledge-embed/v1' || typeof value.requestId !== 'string' ||
        !guid.test(value.requestId) || value.capability !== 'knowledge.embed' || !timestamp(value.expiresAt) ||
        !Number.isFinite(now) || Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000 ||
        !only(value.input, ['inputKind', 'texts']) || !['query', 'passage'].includes(value.input.inputKind) ||
        !Array.isArray(value.input.texts) || value.input.texts.length < 1 || value.input.texts.length > 32 ||
        value.input.texts.some(item => !text(item)) || value.input.texts.reduce((total, item) => total + [...item].length, 0) > 65536)
        throw new TypeError('Invalid embedding request.');
    Object.freeze(value.input.texts);
    Object.freeze(value.input);
    return Object.freeze(value);
}

// The caller must authorize first; the body is cleared even on rejection or callback failure.
export async function withKnowledgeEmbeddingRequest(request, process, now = () => Date.now()) {
    const media = request.headers?.get('content-type');
    if (!media || !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(media) || request.headers?.has('content-encoding'))
        return { code: 'unsupported_media_type' };
    const declared = request.headers?.get('content-length');
    if (declared !== null && declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limit))
        return { code: 'payload_too_large' };
    if (typeof request.readBody !== 'function') return { code: 'invalid_request' };
    let body;
    try {
        try { body = await request.readBody(limit); } catch { return { code: 'invalid_request' }; }
        if (!body || body.tooLarge || body.bytes?.byteLength > limit) return { code: 'payload_too_large' };
        if (!(body.bytes instanceof Uint8Array)) return { code: 'invalid_request' };
        let value;
        try { value = parseKnowledgeEmbeddingRequest(body.bytes, now()); } catch { return { code: 'invalid_semantic_request' }; }
        return await process(value, body.bytes);
    } finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}
