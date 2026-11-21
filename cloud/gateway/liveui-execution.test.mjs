import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { executeLiveUI } from './liveui-execution.mjs';
import { createLiveUIAccessMonitor } from './liveui-access-monitor.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { tenantId: '22222222-2222-2222-2222-222222222222', applicationId: '33333333-3333-3333-3333-333333333333' };
const body = () => Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.liveui/v1', requestId: id, capability: 'inference.liveui',
    expiresAt: new Date(Date.now() + 60000).toISOString(), input: { schemaVersion: 'v1', intent: 'open item',
        context: { route: '/items', availableActions: [{ target: 'item', action: 'activate' }] }, generation: { maxActions: 2, maxOutputTokens: 32 } } }));
function fixture() {
    const calls = []; const lifetime = new AbortController();
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    const controlPlane = {
        findFingerprintKey: async () => ({ status: 'not_found' }),
        reserve: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claim: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; },
        inspectOutput: async (_, requestId, reservationId, claimId, fields) => {
            calls.push('output'); assert.deepEqual([requestId, reservationId, claimId, fields], [id, id, id, ['v1', 'activate', 'item']]);
            return { allowed: true, code: 'allowed' };
        },
    };
    const authorization = { inspectLiveUI: async () => { calls.push('input'); return { allowed: true }; } };
    const result = { plan: { schemaVersion: 'v1', actions: [{ type: 'activate', target: 'item' }] }, provider: 'deterministic' };
    const dispatcher = { executeClaimed: async () => { calls.push('dispatch'); return { status: 'completed', result }; } };
    const monitor = { signal: lifetime.signal, start: async () => { calls.push('start'); return true; },
        recheck: async () => { calls.push('check'); return true; }, dispose: () => calls.push('dispose') };
    const run = (createAccessMonitor = () => monitor) => executeLiveUI({ credential: 'credential', identity, body: body(), deployment: {}, modelId: 'model' },
        { controlPlane, authorization, keyring, dispatcher, createAccessMonitor });
    return { calls, controlPlane, dispatcher, monitor, run, result, dispose: () => keyring.dispose() };
}
test('output privacy and access are checked before a settled plan is released', async () => {
    const f = fixture(); try {
        assert.deepEqual(await f.run(), { status: 'completed', result: f.result });
        assert.deepEqual(f.calls, ['input', 'reserve', 'claim', 'start', 'dispatch', 'check', 'output', 'check', 'dispose']);
    } finally { f.dispose(); }
});
test('failure, denial and revocation never release a plan', async () => {
    for (const mode of ['blocked', 'inspection-failure', 'revoked', 'dispatch-failure', 'monitor-failure']) {
        const f = fixture(); try {
            if (mode === 'blocked') f.controlPlane.inspectOutput = async () => ({ allowed: false, code: 'privacy_blocked' });
            if (mode === 'inspection-failure') f.controlPlane.inspectOutput = async () => { throw new Error('sensitive'); };
            if (mode === 'revoked') f.monitor.recheck = async () => false;
            if (mode === 'dispatch-failure') f.dispatcher.executeClaimed = async () => ({ status: 'upstream_unavailable' });
            if (mode === 'monitor-failure') f.monitor.start = async () => false;
            assert.deepEqual(await f.run(), { status: mode === 'blocked' ? 'privacy_blocked' : 'unavailable' });
            assert.equal(f.calls.at(-1), 'dispose');
        } finally { f.dispose(); }
    }
});
test('real access monitor rejects revocation during dispatch', async () => {
    const f = fixture(); try {
        let allowed = true;
        f.controlPlane.verifyClaim = async () => allowed;
        f.dispatcher.executeClaimed = async () => { allowed = false; return { status: 'completed', result: f.result }; };
        assert.deepEqual(await f.run(createLiveUIAccessMonitor), { status: 'unavailable' });
        assert.ok(!f.calls.includes('output'));
    } finally { f.dispose(); }
});
