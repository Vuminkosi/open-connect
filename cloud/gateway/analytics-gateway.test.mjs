import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { handle } from './gateway.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { credentialId: id, tenantId: id, applicationId: '22222222-2222-2222-2222-222222222222', environment: 'Development' };

test('analytics route authorizes, checks worker readiness and settles before responding', async () => {
    const calls = [];
    const authorization = { authorize: async () => ({ authorized: true, identity }) };
    const controlPlane = {
        ...authorization,
        matchAnalyticsDeployment: async () => true,
        findAnalyticsFingerprintKey: async () => ({ status: 'not_found' }),
        reserveAnalytics: async () => ({ status: 'reserved', reservationId: id }),
        inspectAnalytics: async () => ({ status: 'allowed' }),
        claimAnalytics: async () => ({ status: 'claimed', claimId: id }),
        inspectAnalyticsResponse: async () => ({ status: 'allowed' }),
        completeAnalytics: async (_credential, _reservation, _claim, milliseconds) => { calls.push('complete'); assert.equal(milliseconds, 14); return { status: 'completed' }; },
    };
    const result = { feature: 'weight', unit: 'kg', lineage: 'fixture', sample_size: 3, observed_count: 3, missing_count: 0,
        coverage: 1, minimum: 1, maximum: 3, median: 2, median_absolute_deviation: 1, threshold: 3.5,
        anomalies: [], warnings: ['insufficient_baseline'], algorithm_version: 'robust-mad-v1' };
    const dispatcher = { executeClaimed: async () => ({ status: 'received', bytes: Buffer.from(JSON.stringify({
        schemaVersion: 'openconnect.v1', taskId: id, kind: 'analytics.analyze', result, usage: { computeMilliseconds: 14 } })) }) };
    const keyring = createFingerprintKeyring({ activeKeyId: 'active', keys: [{ id: 'active', key: Buffer.alloc(32, 3) }], maximumBodyBytes: 262144 });
    const analyticsExecution = { controlPlane, keyring, dispatcher, probe: { probe: async () => true }, deployment: { endpointReference: 'worker' } };
    const value = { schemaVersion: 'openconnect.cloud.analytics/v1', requestId: id, capability: 'analytics.analyze',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { feature: 'weight', unit: 'kg', lineage: 'fixture', values: [1, 2, 3] } };
    const bytes = Buffer.from(JSON.stringify(value));
    const request = { method: 'POST', url: 'http://localhost/cloud/v1/analytics', headers: new Headers({ authorization: 'Bearer token', 'content-type': 'application/json' }),
        readBody: async () => ({ bytes }) };
    try {
        const response = await handle(request, { authorization, analyticsExecution });
        assert.equal(response.status, 200);
        const responseBody = await response.json();
        assert.deepEqual(responseBody.result, result);
        assert.deepEqual(responseBody.usage, { computeMilliseconds: 14 });
        assert.deepEqual(calls, ['complete']);
        assert.ok(bytes.every(byte => byte === 0));
    } finally { keyring.dispose(); }
});
