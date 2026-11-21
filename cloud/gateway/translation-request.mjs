import { parseUniqueJson } from './classification-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const language = /^[a-z]{3}_[A-Z][a-z]{3}$/;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, keys) => object(value) && Object.keys(value).every(key => keys.includes(key));

// This is an input contract, not evidence that a requested language pair is deployed.
export function parseTranslationRequest(bytes, now = Date.now()) {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 65536)
        throw new TypeError('Invalid translation request.');
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.translation/v1' || typeof value.requestId !== 'string' || !guid.test(value.requestId) ||
        value.capability !== 'translation.translate' || typeof value.expiresAt !== 'string' ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value.expiresAt) ||
        !Number.isFinite(Date.parse(value.expiresAt)) || new Date(value.expiresAt).toISOString() !== value.expiresAt ||
        !Number.isFinite(now) || Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000 ||
        !only(value.input, ['text', 'sourceLang', 'targetLang']) ||
        typeof value.input.text !== 'string' || !value.input.text.isWellFormed() || !value.input.text.trim() ||
        [...value.input.text].length > 5000 || typeof value.input.sourceLang !== 'string' || !language.test(value.input.sourceLang) ||
        typeof value.input.targetLang !== 'string' || !language.test(value.input.targetLang) || value.input.sourceLang === value.input.targetLang)
        throw new TypeError('Invalid translation request.');
    Object.freeze(value.input);
    return Object.freeze(value);
}

// All worker-visible strings must be included in the future input inspection.
export function translationInspectionFields(request) {
    return [request.input.text, request.input.sourceLang, request.input.targetLang];
}

// Invoke only after authorization; the original request bytes are cleared after the callback.
export async function withTranslationRequest(request, process, now = () => Date.now()) {
    const limit = 65536;
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
        try { value = parseTranslationRequest(body.bytes, now()); } catch { return { code: 'invalid_semantic_request' }; }
        return await process(value, body.bytes);
    } finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}
