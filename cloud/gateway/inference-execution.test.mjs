import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { executeInference } from './inference-execution.mjs';
import { createInferenceAccessMonitor } from './inference-access-monitor.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { tenantId: '22222222-2222-2222-2222-222222222222', applicationId: '33333333-3333-3333-3333-333333333333' };
const body = () => Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.inference/v1', requestId: id, capability: 'inference.chat',
    expiresAt: new Date(Date.now() + 60000).toISOString(), modelId: 'model', input: { messages: [{ role: 'user', content: 'fixture' }], maxTokens: 5, stream: false } }));
function fixture() {
    const calls = []; const lifetime = new AbortController();
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    const controlPlane = {
        findInferenceFingerprintKey: async () => ({ status: 'not_found' }),
        inspectInference: async () => { calls.push('input-privacy'); return { allowed: true }; },
        reserveInference: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claimInference: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; },
        inspectInferenceOutput: async (_, requestId, reservationId, claimId, output) => {
            calls.push('output-privacy'); assert.deepEqual([requestId, reservationId, claimId, output], [id, id, id, 'safe']);
            return { allowed: true, code: 'allowed' };
        },
    };
    const quote = async ({ payload, key }) => ({ requestId: id, modelId: 'model', maxOutputTokens: 5, inputTokenUpperBound: 3,
        workerPayloadFingerprint: createHmac('sha256', key).update(payload).digest('base64') });
    const dispatcher = { executeClaimed: async claim => { calls.push('dispatch'); assert.equal(claim.reservedTokens, 8);
        return { status: 'completed', result: { output: 'safe', usage: { inputTokens: 3, outputTokens: 1 } } }; } };
    const monitor = { signal: lifetime.signal, start: async () => { calls.push('monitor-start'); return true; },
        recheck: async () => { calls.push('monitor-check'); return true; }, dispose: () => calls.push('monitor-dispose') };
    const run = () => executeInference({ credential: 'credential', identity, body: body(), deployment: { endpointReference: 'worker' } },
        { controlPlane, keyring, quote, dispatcher, createAccessMonitor: () => monitor });
    return { calls, controlPlane, dispatcher, monitor, lifetime, run, dispose: () => keyring.dispose() };
}

test('settled worker output is inspected and access rechecked before release', async () => {
    const f = fixture();
    try {
        assert.deepEqual(await f.run(), { status: 'completed', result: { output: 'safe', usage: { inputTokens: 3, outputTokens: 1 } } });
        assert.deepEqual(f.calls, ['input-privacy', 'reserve', 'claim', 'monitor-start', 'dispatch', 'monitor-check', 'output-privacy', 'monitor-check', 'monitor-dispose']);
    } finally { f.dispose(); }
});

test('output never leaves when inspection, access or dispatch fails', async () => {
    for (const mode of ['blocked', 'inspection-failure', 'revoked', 'dispatch-failure', 'monitor-failure']) {
        const f = fixture();
        try {
            if (mode === 'blocked') f.controlPlane.inspectInferenceOutput = async () => ({ allowed: false, code: 'privacy_blocked' });
            if (mode === 'inspection-failure') f.controlPlane.inspectInferenceOutput = async () => { throw new Error('sensitive'); };
            if (mode === 'revoked') f.monitor.recheck = async () => false;
            if (mode === 'dispatch-failure') f.dispatcher.executeClaimed = async () => ({ status: 'upstream_unavailable' });
            if (mode === 'monitor-failure') f.monitor.start = async () => false;
            assert.deepEqual(await f.run(), { status: mode === 'blocked' ? 'privacy_blocked' : 'unavailable' });
            assert.equal(f.calls.at(-1), 'monitor-dispose');
        } finally { f.dispose(); }
    }
});

test('real claim monitor prevents output when access is revoked during execution', async () => {
    const f = fixture();
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    try {
        let allowed = true;
        f.controlPlane.verifyInferenceClaim = async () => allowed;
        const dispatcher = { executeClaimed: async () => { allowed = false; return { status: 'completed', result: { output: 'safe' } }; } };
        const result = await executeInference({ credential: 'credential', identity, body: body(), deployment: { endpointReference: 'worker' } },
            { controlPlane: f.controlPlane, keyring,
                quote: async ({ payload, key }) => ({ requestId: id, modelId: 'model', maxOutputTokens: 5, inputTokenUpperBound: 3,
                    workerPayloadFingerprint: createHmac('sha256', key).update(payload).digest('base64') }), dispatcher, createAccessMonitor: createInferenceAccessMonitor });
        assert.deepEqual(result, { status: 'unavailable' });
        assert.ok(!f.calls.includes('output-privacy'));
    } finally { keyring.dispose(); f.dispose(); }
});
