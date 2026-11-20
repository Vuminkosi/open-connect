import { test } from 'node:test';
import assert from 'node:assert/strict';
import { withClassificationRequest } from './classification-request.mjs';
import { reserveClassificationRequest } from './classification-quota.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';

const identity = { tenantId: '11111111-1111-1111-1111-111111111111', applicationId: '22222222-2222-2222-2222-222222222222' };
const requestId = '33333333-3333-3333-3333-333333333333';
const expiresAt = new Date(Date.now() + 60000).toISOString();
const envelope = { schemaVersion: 'openconnect.cloud.classification/v2', requestId, capability: 'classification.classify', expiresAt, input: { text: 'private fixture' } };
const keyring = () => createFingerprintKeyring({ activeKeyId: 'new', keys: [{ id: 'new', key: Buffer.alloc(32, 1) }, { id: 'old', key: Buffer.alloc(32, 2) }] });

async function run(raw, controlPlane, ring, signal) {
    let bytes;
    const request = { headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes: bytes = Buffer.from(raw) }) };
    const result = await withClassificationRequest(request, (value, body) => reserveClassificationRequest(
        { credential: 'fixture', identity, value, body, signal }, { controlPlane, keyring: ring }));
    assert.ok(bytes.every(byte => byte === 0));
    return result;
}

test('reservation fingerprints exact validated bytes and retries use the retained key and original expiry', async () => {
    const ring = keyring();
    const originalExpiry = new Date(Date.now() + 30000).toISOString();
    const calls = [];
    const controlPlane = { findClassificationFingerprintKey: async () => ({ status: 'found', keyId: 'old', expiresAt: originalExpiry }),
        reserveClassification: async (_, input) => { calls.push(input); return { status: 'duplicate', reservationId: requestId }; } };
    try {
        const raw = JSON.stringify(envelope);
        assert.equal((await run(raw, controlPlane, ring)).status, 'duplicate');
        assert.equal(calls[0].fingerprintKeyId, 'old');
        assert.equal(calls[0].expiresAt, originalExpiry);
        await run(JSON.stringify(envelope, null, 2), controlPlane, ring);
        assert.notEqual(calls[0].fingerprint, calls[1].fingerprint);
    } finally { ring.dispose(); }
});

test('new reservations use the active key; unknown keys, errors and cancellation cannot reserve', async () => {
    const ring = keyring();
    let calls = 0;
    const controlPlane = { findClassificationFingerprintKey: async () => ({ status: 'not_found' }),
        reserveClassification: async (_, input) => { calls++; assert.equal(input.fingerprintKeyId, 'new'); return { status: 'reserved', reservationId: requestId }; } };
    try {
        assert.equal((await run(JSON.stringify(envelope), controlPlane, ring)).status, 'reserved');
        controlPlane.findClassificationFingerprintKey = async () => ({ status: 'found', keyId: 'retired', expiresAt });
        assert.equal((await run(JSON.stringify(envelope), controlPlane, ring)).status, 'quota_unavailable');
        controlPlane.findClassificationFingerprintKey = async () => { throw new Error('private diagnostic'); };
        assert.deepEqual(await run(JSON.stringify(envelope), controlPlane, ring), { status: 'quota_unavailable' });
        const controller = new AbortController(); controller.abort();
        assert.equal((await run(JSON.stringify(envelope), controlPlane, ring, controller.signal)).status, 'quota_unavailable');
        assert.equal(calls, 1);
        const pending = new AbortController();
        controlPlane.findClassificationFingerprintKey = async () => ({ status: 'not_found' });
        controlPlane.reserveClassification = async () => { pending.abort(); return { status: 'reserved', reservationId: requestId }; };
        assert.equal((await run(JSON.stringify(envelope), controlPlane, ring, pending.signal)).status, 'quota_unavailable');
    } finally { ring.dispose(); }
});
