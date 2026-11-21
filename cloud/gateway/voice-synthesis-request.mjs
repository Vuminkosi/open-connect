import { parseUniqueJson } from './classification-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, keys) => object(value) && Object.keys(value).every(key => keys.includes(key));
const length = value => [...value].length;

// This contract reflects the currently implemented Kokoro engine, not every potential TTS backend.
export function parseVoiceSynthesisRequest(bytes, now = Date.now()) {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 65536) throw new TypeError('Invalid voice request.');
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.voice-synthesis/v1' || typeof value.requestId !== 'string' || !guid.test(value.requestId) ||
        value.capability !== 'voice.synthesize' || typeof value.expiresAt !== 'string' ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value.expiresAt) ||
        !Number.isFinite(Date.parse(value.expiresAt)) || new Date(value.expiresAt).toISOString() !== value.expiresAt ||
        !Number.isFinite(now) || Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000 ||
        !only(value.input, ['text', 'voiceId', 'locale', 'format']) ||
        typeof value.input.text !== 'string' || !value.input.text.trim() || length(value.input.text) > 2000 ||
        value.input.voiceId !== 'af_heart' || typeof value.input.locale !== 'string' ||
        length(value.input.locale) < 1 || length(value.input.locale) > 16 || !['wav', 'pcm'].includes(value.input.format))
        throw new TypeError('Invalid voice request.');
    Object.freeze(value.input);
    return Object.freeze(value);
}

// These are the strings forwarded to the worker and must be inspected before dispatch.
export function voiceSynthesisInspectionFields(request) {
    return [request.input.text, request.input.voiceId, request.input.locale, request.input.format];
}

// Call only after authorization; exact bytes are retained only for the callback.
export async function withVoiceSynthesisRequest(request, process, now = () => Date.now()) {
    const limit = 65536;
    const media = request.headers?.get('content-type');
    if (!media || !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(media) || request.headers?.has('content-encoding'))
        return { code: 'unsupported_media_type' };
    const declared = request.headers?.get('content-length');
    if (declared !== null && declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limit)) return { code: 'payload_too_large' };
    if (typeof request.readBody !== 'function') return { code: 'invalid_request' };
    let body;
    try {
        try { body = await request.readBody(limit); } catch { return { code: 'invalid_request' }; }
        if (!body || body.tooLarge || body.bytes?.byteLength > limit) return { code: 'payload_too_large' };
        if (!(body.bytes instanceof Uint8Array)) return { code: 'invalid_request' };
        let value;
        try { value = parseVoiceSynthesisRequest(body.bytes, now()); } catch { return { code: 'invalid_semantic_request' }; }
        return await process(value, body.bytes);
    } finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}
