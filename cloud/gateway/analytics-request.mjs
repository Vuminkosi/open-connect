import { parseUniqueJson } from './classification-request.mjs';

const limit = 256 * 1024;
const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, keys) => object(value) && Object.keys(value).every(key => keys.includes(key));
const text = (value, max) => typeof value === 'string' && !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value) &&
    [...value].length >= 1 && [...value].length <= max;
const timestamp = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value) &&
    Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value;
const boundedInteger = (value, min, max) => Number.isInteger(value) && value >= min && value <= max;

// This is an admission contract only; tenant and application scope come from authenticated identity.
export function parseAnalyticsRequest(bytes, now) {
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.analytics/v1' || typeof value.requestId !== 'string' ||
        !guid.test(value.requestId) || value.capability !== 'analytics.analyze' || !timestamp(value.expiresAt) ||
        !Number.isFinite(now) || Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000)
        throw new TypeError('Invalid analytics request.');
    const input = value.input;
    if (!only(input, ['feature', 'unit', 'lineage', 'values', 'threshold', 'forecast', 'trend']) ||
        !text(input.feature, 128) || !text(input.unit, 64) || !text(input.lineage, 256) ||
        !Array.isArray(input.values) || input.values.length > 10000 ||
        input.values.some(item => item !== null && (typeof item !== 'number' || !Number.isFinite(item) || Math.abs(item) > 1e150)) ||
        (Object.hasOwn(input, 'threshold') && (typeof input.threshold !== 'number' || !Number.isFinite(input.threshold) || input.threshold <= 0 || input.threshold > 100)) ||
        (Object.hasOwn(input, 'trend') && typeof input.trend !== 'boolean')) throw new TypeError('Invalid analytics input.');
    if (input.forecast !== undefined && input.forecast !== null) {
        const forecast = input.forecast;
        if (!only(forecast, ['timestamps', 'intervalSeconds', 'seasonalPeriod', 'horizon']) ||
            !Array.isArray(forecast.timestamps) || forecast.timestamps.length !== input.values.length ||
            forecast.timestamps.some(item => !timestamp(item)) || !boundedInteger(forecast.intervalSeconds, 1, 31536000) ||
            !boundedInteger(forecast.seasonalPeriod, 1, 365) || !boundedInteger(forecast.horizon, 1, 100))
            throw new TypeError('Invalid analytics input.');
        Object.freeze(forecast.timestamps);
        Object.freeze(forecast);
    }
    Object.freeze(input.values);
    Object.freeze(input);
    return Object.freeze(value);
}

// The caller must authorize before entering; exact bytes are cleared when the callback completes.
export async function withAnalyticsRequest(request, process, now = () => Date.now()) {
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
        try { value = parseAnalyticsRequest(body.bytes, now()); } catch { return { code: 'invalid_semantic_request' }; }
        return await process(value, body.bytes);
    } finally { if (body?.bytes instanceof Uint8Array) body.bytes.fill(0); }
}
