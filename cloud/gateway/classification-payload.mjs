import { createHmac, randomBytes } from 'node:crypto';

// The caller owns and clears key and payload after sending them to the chosen worker.
export function bindClassificationPayload(payload) {
    if (!(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 131072)
        throw new TypeError('Invalid worker payload.');
    const key = randomBytes(32);
    const fingerprint = createHmac('sha256', key).update(payload).digest();
    return { key, fingerprint };
}
