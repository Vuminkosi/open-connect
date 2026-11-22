import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHmac } from 'node:crypto';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { executeAnalytics } from './analytics-execution.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const id = '11111111-1111-1111-1111-111111111111';
const identity = { credentialId: id, tenantId: id, applicationId: '22222222-2222-2222-2222-222222222222', environment: 'Development' };
const deployment = { endpointReference: 'analytics' };
const body = () => readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url));
const output = () => Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: id, kind: 'analytics.analyze', result: {
    feature: 'weight', unit: 'kg', lineage: 'dataset-1', sample_size: 4, observed_count: 3, missing_count: 1,
    coverage: 0.75, minimum: 1, maximum: 4, median: 2, median_absolute_deviation: 1, threshold: 3.5,
    anomalies: [], warnings: ['insufficient_baseline'], algorithm_version: 'robust-mad-v1',
    trend: { algorithm_version: 'linear-position-v1', sample_size: 4, observed_count: 3, slope_per_sample: 1,
        warnings: ['descriptive_only_no_significance_test', 'missing_values'] },
    }, usage: { computeMilliseconds: 14 } }));

function fixture() {
    const calls = []; const captured = {}; let workerProof;
    const controlPlane = {
        findAnalyticsFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        reserveAnalytics: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        inspectAnalytics: async () => { calls.push('input'); return { status: 'allowed' }; },
        claimAnalytics: async (_credential, _reservation, proof) => { calls.push('claim'); workerProof = proof.workerPayloadFingerprint; return { status: 'claimed', claimId: id }; },
        authorize: async () => { calls.push('authorize'); return { authorized: true, identity }; },
        matchAnalyticsDeployment: async () => { calls.push('deployment'); return true; },
        inspectAnalyticsResponse: async () => { calls.push('output'); return { status: 'allowed' }; },
        completeAnalytics: async (_credential, _reservation, _claim, milliseconds) => { calls.push('complete'); assert.equal(milliseconds, 14); return { status: 'completed' }; },
    };
    const dispatcher = { executeClaimed: async (prepared, claim) => {
        calls.push('dispatch'); captured.payload = prepared.payload; captured.key = claim.key;
        assert.equal(createHmac('sha256', claim.key).update(prepared.payload).digest('base64'), workerProof);
        captured.response = output(); return { status: 'received', bytes: captured.response };
    } };
    const keyring = createFingerprintKeyring({ activeKeyId: 'active', keys: [{ id: 'active', key: Buffer.alloc(32, 3) }], maximumBodyBytes: 262144 });
    return { calls, captured, controlPlane, dispatcher, keyring };
}

const run = f => executeAnalytics({ credential: 'token', identity, body: body(), deployment },
    { controlPlane: f.controlPlane, dispatcher: f.dispatcher, keyring: f.keyring, clock: () => now });

test('releases a validated response only after privacy, claim, access and completion checks', async () => {
    const f = fixture();
    try {
        const result = await run(f);
        assert.equal(result.status, 'completed');
        assert.deepEqual(f.calls, ['lookup', 'reserve', 'input', 'claim', 'authorize', 'deployment', 'dispatch',
            'authorize', 'deployment', 'output', 'authorize', 'deployment', 'complete', 'authorize', 'deployment']);
        for (const buffer of Object.values(f.captured)) assert.ok(buffer.every(byte => byte === 0));
    } finally { f.keyring.dispose(); }
});

test('never releases a result after a failed privacy, claim, dispatch, access or settlement step', async () => {
    for (const step of ['input', 'claim', 'dispatch', 'output', 'completion', 'access']) {
        const f = fixture();
        if (step === 'input') f.controlPlane.inspectAnalytics = async () => ({ status: 'privacy_blocked' });
        if (step === 'claim') f.controlPlane.claimAnalytics = async () => ({ status: 'unavailable' });
        if (step === 'dispatch') f.dispatcher.executeClaimed = async () => ({ status: 'upstream_unavailable' });
        if (step === 'output') f.controlPlane.inspectAnalyticsResponse = async () => ({ status: 'privacy_blocked' });
        if (step === 'completion') f.controlPlane.completeAnalytics = async () => ({ status: 'unavailable' });
        if (step === 'access') f.controlPlane.authorize = async () => ({ authorized: false });
        try {
            const result = await run(f);
            assert.notEqual(result.status, 'completed', step);
            if (['input', 'claim', 'access'].includes(step)) assert.ok(!f.calls.includes('dispatch'));
        } finally { f.keyring.dispose(); }
    }
});

test('snapshots the worker response before asynchronous access checks', async () => {
    const f = fixture();
    const monitor = { signal: new AbortController().signal, start: async () => true,
        recheck: async () => { f.captured.response.fill(0); return true; }, dispose() {} };
    f.controlPlane.inspectAnalyticsResponse = async (_credential, input) => {
        assert.equal(JSON.parse(input.body.toString()).taskId, id);
        return { status: 'allowed' };
    };
    try {
        const result = await executeAnalytics({ credential: 'token', identity, body: body(), deployment },
            { controlPlane: f.controlPlane, dispatcher: f.dispatcher, keyring: f.keyring,
                clock: () => now, monitorFactory: () => monitor });
        assert.equal(result.status, 'completed');
        for (const buffer of Object.values(f.captured)) assert.ok(buffer.every(byte => byte === 0));
    } finally { f.keyring.dispose(); }
});

test('a broken monitor cleanup still clears worker buffers', async () => {
    const f = fixture();
    const monitor = { signal: new AbortController().signal, start: async () => true,
        recheck: async () => true, dispose() { throw new Error('private diagnostic'); } };
    try {
        const result = await executeAnalytics({ credential: 'token', identity, body: body(), deployment },
            { controlPlane: f.controlPlane, dispatcher: f.dispatcher, keyring: f.keyring,
                clock: () => now, monitorFactory: () => monitor });
        assert.equal(result.status, 'completed');
        for (const buffer of Object.values(f.captured)) assert.ok(buffer.every(byte => byte === 0));
    } finally { f.keyring.dispose(); }
});
