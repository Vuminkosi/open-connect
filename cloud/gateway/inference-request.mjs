import { parseInferenceRequest } from './inference-admission.mjs';

// Authorize before reading; retain the exact request bytes only within the callback.
export async function withInferenceRequest(request, process, now = () => Date.now()) {
    const limit = 65536;
    const media = request.headers?.get('content-type');
    if (!media || !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(media) || request.headers?.has('content-encoding')) return { code: 'unsupported_media_type' };
    const declared = request.headers?.get('content-length');
    if (declared !== null && declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limit)) return { code: 'payload_too_large' };
    if (typeof request.readBody !== 'function') return { code: 'invalid_request' };
    let body;
    try {
        try { body = await request.readBody(limit); } catch { return { code: 'invalid_request' }; }
        if (!body || body.tooLarge || body.bytes?.byteLength > limit) return { code: 'payload_too_large' };
        if (!(body.bytes instanceof Uint8Array)) return { code: 'invalid_request' };
        let value;
        try { value = parseInferenceRequest(body.bytes, now()); } catch { return { code: 'invalid_semantic_request' }; }
        return await process(value, body.bytes);
    } finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}
