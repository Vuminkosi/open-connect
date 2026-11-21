import { createHmac, randomBytes } from 'node:crypto';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

// Caller owns and clears the returned key and fingerprint.
export function bindWorkerPayload(payload) {
    if (!(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 131072)
        throw new TypeError('Invalid worker payload.');
    const key = randomBytes(32);
    return { key, fingerprint: createHmac('sha256', key).update(payload).digest() };
}

// Cloud GUID bytes map to the host ULID in network byte order.
export function cloudTenantToHostId(tenantId) {
    if (typeof tenantId !== 'string' || !guid.test(tenantId)) throw new TypeError('Invalid Cloud tenant.');
    let number = BigInt(`0x${tenantId.replaceAll('-', '')}`);
    let result = '';
    for (let i = 0; i < 26; i++) { result = alphabet[Number(number & 31n)] + result; number >>= 5n; }
    return result;
}
