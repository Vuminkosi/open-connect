import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { reserveAnalyticsRequest } from './analytics-quota.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const original = () => Buffer.from(readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url)));
const identity = { tenantId: '11111111-1111-1111-1111-111111111111', applicationId: '22222222-2222-2222-2222-222222222222' };
const ring = () => createFingerprintKeyring({ activeKeyId: 'active', keys: [{ id: 'active', key: Buffer.alloc(32, 3) },
    { id: 'old', key: Buffer.alloc(32, 4) }], maximumBodyBytes: 262144 });

test('snapshots the request and binds tenant, application, route and retained key', async () => {
    const keyring = ring(); const body = original(); const expected = Buffer.from(body); let reserved;
    const controlPlane = {
        async findAnalyticsFingerprintKey() { body.fill(0); return { status: 'found', keyId: 'old', expiresAt: '2030-01-01T00:01:00.000Z' }; },
        async reserveAnalytics(_, payload) { reserved = payload; return { status: 'reserved', reservationId: '33333333-3333-3333-3333-333333333333' }; },
    };
    const result = await reserveAnalyticsRequest({ credential: 'credential', identity, body }, { controlPlane, keyring, clock: () => now });
    const fingerprint = keyring.compute('old', { ...identity, capability: 'analytics.analyze', method: 'POST',
        route: '/cloud/v1/analytics', mediaType: 'application/json', body: expected });
    assert.equal(result.status, 'reserved');
    assert.equal(reserved.fingerprint, fingerprint.toString('base64'));
    assert.equal(reserved.fingerprintKeyId, 'old');
    assert.equal(reserved.expiresAt, '2030-01-01T00:01:00.000Z');
    keyring.dispose();
});

test('invalid identity, expired lookup and revoked requests never reserve', async () => {
    const keyring = ring(); let calls = 0;
    const controlPlane = { findAnalyticsFingerprintKey: async () => ({ status: 'found', keyId: 'old', expiresAt: '2030-01-01T00:02:00.000Z' }),
        reserveAnalytics: async () => { calls++; return { status: 'reserved' }; } };
    assert.equal((await reserveAnalyticsRequest({ credential: 'c', identity, body: original() }, { controlPlane, keyring, clock: () => now })).status, 'quota_unavailable');
    const controller = new AbortController(); controller.abort();
    assert.equal((await reserveAnalyticsRequest({ credential: 'c', identity, body: original(), signal: controller.signal }, { controlPlane, keyring, clock: () => now })).status, 'quota_unavailable');
    controlPlane.findAnalyticsFingerprintKey = async () => ({ status: 'not_found' });
    assert.equal((await reserveAnalyticsRequest({ credential: 'c', identity: { ...identity, tenantId: 'invalid' }, body: original() }, { controlPlane, keyring, clock: () => now })).status, 'quota_unavailable');
    assert.equal(calls, 0);
    keyring.dispose();
});
