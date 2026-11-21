import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createLiveUIControlPlaneClient } from './liveui-control-plane.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const options = { origin: 'https://localhost', ca: 'invalid', certificate: 'invalid', privateKey: 'invalid', timeoutMs: 500 };
const fingerprint = Buffer.alloc(32).toString('base64');
test('LiveUI control-plane calls reject malformed inputs and fail closed on invalid TLS', async () => {
    assert.throws(() => createLiveUIControlPlaneClient({ ...options, origin: 'http://localhost' }));
    const client = createLiveUIControlPlaneClient(options);
    const cancelled = new AbortController(); cancelled.abort();
    assert.deepEqual(await client.findFingerprintKey('token', id, cancelled.signal), { status: 'unavailable' });
    assert.deepEqual(await client.reserve('token', { requestId: id, fingerprintKeyId: 'key', fingerprint: 'bad', expiresAt: new Date().toISOString() }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.claim('token', id, { fingerprintKeyId: 'key', fingerprint, workerPayloadFingerprint: fingerprint,
        modelId: 'model', deployment: { endpointReference: 'worker', region: 'region', contractVersion: 'v1', modelRevision: 'r1', modelDigest: 'sha256:' + 'a'.repeat(64) } }), { status: 'unavailable' });
    assert.equal(await client.verifyClaim('token', id, id), false);
    assert.deepEqual(await client.inspectOutput('token', id, id, id, ['v1', 'person@', 'example.test']), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectOutput('token', id, id, id, ['v1', null, 'target']), { allowed: false, code: 'capability_unavailable' });
});
