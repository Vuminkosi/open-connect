import { createHmac } from 'node:crypto';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const keyIdPattern = /^[A-Za-z0-9_-]{1,64}$/;

// Key bytes come only from deployment-controlled secret storage. Retained IDs support retries across rotation.
export function createFingerprintKeyring({ activeKeyId, keys }) {
    if (typeof activeKeyId !== 'string' || !keyIdPattern.test(activeKeyId) || !Array.isArray(keys) || keys.length < 1 || keys.length > 16)
        throw new TypeError('Invalid fingerprint key configuration.');
    const retained = new Map();
    try {
        for (const entry of keys) {
            if (!entry || typeof entry.id !== 'string' || !keyIdPattern.test(entry.id) || !(entry.key instanceof Uint8Array) ||
                entry.key.byteLength < 32 || entry.key.byteLength > 64 || retained.has(entry.id))
                throw new TypeError('Invalid fingerprint key configuration.');
            retained.set(entry.id, Buffer.from(entry.key));
        }
        if (!retained.has(activeKeyId)) throw new TypeError('Invalid fingerprint key configuration.');
    } catch (error) {
        for (const key of retained.values()) key.fill(0);
        throw error;
    }
    let disposed = false;
    return Object.freeze({
        activeKeyId,
        selectKeyId(lookup) {
            if (disposed || !lookup || typeof lookup !== 'object') throw new TypeError('Fingerprint key is unavailable.');
            if (lookup.status === 'not_found' && lookup.keyId == null) return activeKeyId;
            if (lookup.status === 'found' && typeof lookup.keyId === 'string' && retained.has(lookup.keyId)) return lookup.keyId;
            throw new TypeError('Fingerprint key is unavailable.');
        },
        compute(keyId, input) {
            const key = retained.get(keyId);
            if (disposed || !key || !input || typeof input.tenantId !== 'string' || !guid.test(input.tenantId) ||
                typeof input.applicationId !== 'string' || !guid.test(input.applicationId) ||
                typeof input.capability !== 'string' || !input.capability.trim() || input.capability.length > 80 ||
                typeof input.method !== 'string' || !input.method.trim() || input.method.length > 16 ||
                typeof input.route !== 'string' || !input.route.trim() || input.route.length > 2048 ||
                typeof input.mediaType !== 'string' || !input.mediaType.trim() || input.mediaType.length > 255 ||
                !(input.body instanceof Uint8Array) || input.body.byteLength > 65536)
                throw new TypeError('Invalid fingerprint request.');
            const hmac = createHmac('sha256', key);
            const add = value => { const length = Buffer.alloc(8); length.writeBigUInt64BE(BigInt(value.byteLength)); hmac.update(length); hmac.update(value); };
            const addText = value => add(Buffer.from(value, 'utf8'));
            addText('openconnect-cloud-request-v1');
            addText(keyId);
            addText(input.tenantId.replaceAll('-', '').toLowerCase());
            addText(input.applicationId.replaceAll('-', '').toLowerCase());
            addText(input.capability); addText(input.method); addText(input.route); addText(input.mediaType);
            add(input.body);
            return hmac.digest();
        },
        dispose() { if (!disposed) { disposed = true; for (const key of retained.values()) key.fill(0); retained.clear(); } },
    });
}
