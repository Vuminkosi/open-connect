import { parseAnalyticsRequest } from './analytics-request.mjs';
import { createHmac, randomBytes } from 'node:crypto';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const maxBytes = 256 * 1024;

// The caller owns and clears both returned buffers after claim and dispatch.
export function bindAnalyticsWorkerPayload(payload) {
    if (!(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > maxBytes)
        throw new TypeError('Invalid analytics worker payload.');
    const key = randomBytes(32);
    return { key, fingerprint: createHmac('sha256', key).update(payload).digest() };
}

// The Cloud tenant GUID uses network byte order, matching CloudTenantIdentity in the .NET host.
export function cloudTenantToHostId(tenantId) {
    if (typeof tenantId !== 'string' || !guid.test(tenantId)) throw new TypeError('Invalid Cloud tenant.');
    let number = BigInt(`0x${tenantId.replaceAll('-', '')}`);
    let result = '';
    for (let i = 0; i < 26; i++) { result = alphabet[Number(number & 31n)] + result; number >>= 5n; }
    return result;
}

// Caller must authenticate, authorize, inspect privacy and reserve a claim before dispatching.
// The returned buffer is sensitive and must be cleared by its owner after use.
export function prepareAnalyticsWorkerPayload(body, identity, now = Date.now()) {
    if (!(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > maxBytes ||
        !identity || typeof identity.applicationId !== 'string' || !guid.test(identity.applicationId) ||
        !Number.isSafeInteger(now)) return null;
    let snapshot; let payload;
    try {
        snapshot = Buffer.from(body);
        const request = parseAnalyticsRequest(snapshot, now);
        const tenantId = cloudTenantToHostId(identity.tenantId);
        payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: request.requestId,
            kind: 'analytics.analyze', tenantId, issuedAt: new Date(now).toISOString(),
            expiresAt: request.expiresAt, payload: request.input }), 'utf8');
        if (payload.length > maxBytes) { payload.fill(0); return null; }
        return { payload, requestId: request.requestId, tenantId, expiresAt: request.expiresAt };
    } catch { payload?.fill(0); return null; }
    finally { snapshot?.fill(0); }
}
