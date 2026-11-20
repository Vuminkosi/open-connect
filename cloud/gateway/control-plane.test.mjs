import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createCloudAuthorizationClient } from './control-plane.mjs';

test('requires HTTPS and deployment-owned TLS material', () => {
    for (const origin of ['http://localhost', 'https://user:password@example.com', 'https://example.com/path', 'https://example.com/?target=x']) {
        assert.throws(() => createCloudAuthorizationClient({ origin, ca: 'ca', certificate: 'cert', privateKey: 'key' }));
    }
    assert.throws(() => createCloudAuthorizationClient({ origin: 'https://example.com', certificate: 'cert', privateKey: 'key' }));
});

test('aborted and invalid calls fail closed without a network request', async () => {
    const client = createCloudAuthorizationClient({ origin: 'https://localhost', ca: 'ca', certificate: 'cert', privateKey: 'key' });
    const controller = new AbortController();
    controller.abort();
    assert.deepEqual(await client.authorize('key', 'inference.chat', controller.signal), { authorized: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.authorize('', 'inference.chat'), { authorized: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.authorize('key', 'x'.repeat(81)), { authorized: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectClassification('key', '11111111-1111-1111-1111-111111111111', 'private', controller.signal), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectClassification('key\r\ninjected', '11111111-1111-1111-1111-111111111111', 'private'), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectClassification('key', 'invalid', 'private'), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInference('key', '11111111-1111-1111-1111-111111111111', [{ role: 'invalid', content: 'private' }], 'key', Buffer.alloc(32).toString('base64')), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInference('key', '11111111-1111-1111-1111-111111111111', [{ role: 'user', content: 'private', extra: 1 }], 'key', Buffer.alloc(32).toString('base64')), { allowed: false, code: 'capability_unavailable' });
    assert.equal(await client.matchClassificationDeployment('key', { endpointReference: 'https://other.example', region: 'region', contractVersion: 'v1', modelRevision: 'v1' }), false);
    assert.equal(await client.matchClassificationDeployment('key', { endpointReference: 'worker', region: 'region', contractVersion: 'v1', modelRevision: 'v1' }, controller.signal), false);
    assert.deepEqual(await client.findClassificationFingerprintKey('key', 'not-a-uuid'), { status: 'unavailable' });
    assert.deepEqual(await client.findClassificationFingerprintKey('key', '11111111-1111-1111-1111-111111111111', controller.signal), { status: 'unavailable' });
    const reservation = { requestId: '11111111-1111-1111-1111-111111111111', fingerprintKeyId: 'key1', fingerprint: Buffer.alloc(32).toString('base64'), expiresAt: new Date().toISOString() };
    assert.deepEqual(await client.reserveClassification('key', reservation, controller.signal), { status: 'quota_unavailable' });
    assert.deepEqual(await client.reserveClassification('key\r\ninjected', reservation), { status: 'quota_unavailable' });
    assert.deepEqual(await client.reserveClassification('key', { ...reservation, fingerprint: 'bad' }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.claimClassification('key', 'invalid', reservation), { status: 'unavailable' });
    assert.deepEqual(await client.claimClassification('key\r\ninjected', reservation.requestId, reservation), { status: 'unavailable' });
    assert.deepEqual(await client.claimClassification('key', reservation.requestId, reservation, controller.signal), { status: 'unavailable' });
    assert.deepEqual(await client.claimClassification('key', reservation.requestId, { ...reservation, fingerprint: 'bad' }), { status: 'unavailable' });
    assert.deepEqual(await client.inspectInference('key', '11111111-1111-1111-1111-111111111111', [{ role: 'user', content: 'private' }], 'key', 'invalid'), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInferenceOutput('key', reservation.requestId, reservation.requestId, 'invalid', 'safe'), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInferenceOutput('key\r\ninjected', reservation.requestId, reservation.requestId, reservation.requestId, 'safe'), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInferenceOutput('key', reservation.requestId, reservation.requestId, reservation.requestId, 'safe', controller.signal), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await client.inspectInferenceOutput('key', reservation.requestId, reservation.requestId, reservation.requestId, 'x'.repeat(131073)), { allowed: false, code: 'capability_unavailable' });
    assert.equal(await client.verifyInferenceClaim('key', reservation.requestId, 'invalid'), false);
    assert.equal(await client.verifyInferenceClaim('key', reservation.requestId, reservation.requestId, controller.signal), false);
    assert.deepEqual(await client.findInferenceFingerprintKey('key', 'invalid'), { status: 'unavailable' });
    assert.deepEqual(await client.reserveInference('key', { ...reservation, reservedTokens: 0 }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.reserveInference('key', { ...reservation, reservedTokens: Number.MAX_SAFE_INTEGER + 1 }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.claimInference('key', reservation.requestId, { ...reservation, modelId: 'bad model' }), { status: 'unavailable' });
    assert.deepEqual(await client.completeClassification('key', reservation.requestId, 'invalid'), { status: 'unavailable' });
    assert.deepEqual(await client.completeClassification('key\r\ninjected', reservation.requestId, reservation.requestId), { status: 'unavailable' });
    assert.deepEqual(await client.completeClassification('key', reservation.requestId, reservation.requestId, controller.signal), { status: 'unavailable' });
});

test('invalid TLS configuration fails closed without exposing diagnostics', async () => {
    const client = createCloudAuthorizationClient({ origin: 'https://localhost', ca: 'not a certificate', certificate: 'invalid', privateKey: 'invalid', timeoutMs: 1000 });
    assert.deepEqual(await client.authorize('secret', 'inference.chat'), { authorized: false, code: 'capability_unavailable' });
});
