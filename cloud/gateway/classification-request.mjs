const limit = 65536;
const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const keysOnly = (value, allowed) => object(value) && Object.keys(value).every(key => allowed.includes(key));
const length = value => [...value].length;

// JSON.parse accepts duplicate names; reject them at every nesting level before applying the contract.
export function parseUniqueJson(text) {
    const result = JSON.parse(text);
    const stack = [];
    for (let i = 0; i < text.length; i++) {
        const char = text[i];
        if (char === '"') {
            const start = i++;
            while (i < text.length) {
                if (text[i] === '\\') { i += 2; continue; }
                if (text[i] === '"') break;
                i++;
            }
            let next = i + 1;
            while (/\s/.test(text[next] ?? '') && next < text.length) next++;
            if (text[next] === ':' && stack.at(-1) instanceof Set) {
                const name = JSON.parse(text.slice(start, i + 1));
                if (stack.at(-1).has(name)) throw new SyntaxError('Duplicate property');
                stack.at(-1).add(name);
            }
        } else if (char === '{' || char === '[') {
            stack.push(char === '{' ? new Set() : null);
            if (stack.length > 16) throw new SyntaxError('Too deeply nested');
        } else if (char === '}' || char === ']') stack.pop();
    }
    return result;
}

function valid(value, now) {
    if (!keysOnly(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'configuration', 'input']) ||
        !['openconnect.cloud.classification/v1', 'openconnect.cloud.classification/v2'].includes(value.schemaVersion) || typeof value.requestId !== 'string' || !guid.test(value.requestId) ||
        value.capability !== 'classification.classify' || !keysOnly(value.input, ['text', 'inputRef'])) return false;
    if (value.schemaVersion === 'openconnect.cloud.classification/v1') {
        if (Object.hasOwn(value, 'expiresAt')) return false;
    } else {
        if (typeof value.expiresAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value.expiresAt)) return false;
        const expiry = Date.parse(value.expiresAt);
        if (!Number.isFinite(expiry) || new Date(expiry).toISOString() !== value.expiresAt || !Number.isFinite(now) || expiry <= now || expiry > now + 300000) return false;
    }
    const input = value.input;
    const hasText = Object.hasOwn(input, 'text');
    const hasRef = Object.hasOwn(input, 'inputRef');
    if (hasText === hasRef || (hasText && (typeof input.text !== 'string' || length(input.text) < 1 || length(input.text) > 20000)) ||
        (hasRef && (typeof input.inputRef !== 'string' || input.inputRef.length > 2048 || !/^[A-Za-z0-9._~-]+$/.test(input.inputRef)))) return false;
    if (!Object.hasOwn(value, 'configuration')) return true;
    const config = value.configuration;
    return keysOnly(config, ['labelSetVersion', 'localeHint', 'threshold', 'maxLabels']) &&
        (!Object.hasOwn(config, 'labelSetVersion') || typeof config.labelSetVersion === 'string' && length(config.labelSetVersion) <= 32) &&
        (!Object.hasOwn(config, 'localeHint') || typeof config.localeHint === 'string' && length(config.localeHint) <= 16) &&
        (!Object.hasOwn(config, 'threshold') || typeof config.threshold === 'number' && Number.isFinite(config.threshold) && config.threshold >= 0 && config.threshold <= 1) &&
        (!Object.hasOwn(config, 'maxLabels') || Number.isInteger(config.maxLabels) && config.maxLabels >= 1 && config.maxLabels <= 10);
}

export async function prepareClassificationRequest(request, now = () => Date.now()) {
    const media = request.headers?.get('content-type');
    if (!media || !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(media) || request.headers?.has('content-encoding')) return { code: 'unsupported_media_type' };
    const declared = request.headers?.get('content-length');
    if (declared !== null && declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limit)) return { code: 'payload_too_large' };
    if (typeof request.readBody !== 'function') return { code: 'invalid_request' };
    let body;
    try {
        body = await request.readBody(limit);
        if (!body || body.tooLarge || body.bytes?.byteLength > limit) return { code: 'payload_too_large' };
        if (!(body.bytes instanceof Uint8Array)) return { code: 'invalid_request' };
        const text = new TextDecoder('utf-8', { fatal: true }).decode(body.bytes);
        const value = parseUniqueJson(text);
        return valid(value, now()) ? { code: null, value } : { code: 'invalid_semantic_request' };
    } catch { return { code: 'invalid_request' }; }
    finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}

export async function validateClassificationRequest(request, now) {
    return (await prepareClassificationRequest(request, now)).code;
}
