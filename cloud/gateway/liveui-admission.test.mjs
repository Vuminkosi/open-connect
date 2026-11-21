import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { withLiveUIAdmission } from './liveui-admission.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { tenantId: '22222222-2222-2222-2222-222222222222', applicationId: '33333333-3333-3333-3333-333333333333' };
const envelope = () => ({ schemaVersion: 'openconnect.cloud.liveui/v1', requestId: id, capability: 'inference.liveui',
    expiresAt: new Date(Date.now() + 60000).toISOString(), input: { schemaVersion: 'v1', intent: 'open item',
        context: { route: '/items', availableActions: [{ target: 'item', action: 'activate', label: 'Open' }] },
        generation: { maxActions: 2, maxOutputTokens: 32 } } });
function fixture() {
    const calls = []; let fingerprint, proof, buffers;
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    const authorization = { inspectLiveUI: async (_, requestId, fields, keyId, value) => {
        calls.push('privacy'); assert.equal(requestId, id); assert.equal(keyId, 'key'); fingerprint = value;
        assert.deepEqual(fields, ['v1', 'open item', '/items', 'item', 'activate', 'Open']); return { allowed: true }; } };
    const controlPlane = {
        findFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        reserve: async (_, input) => { calls.push('reserve'); assert.equal(input.fingerprint, fingerprint); return { status: 'reserved', reservationId: id }; },
        claim: async (_, reservationId, input) => { calls.push('claim'); assert.equal(reservationId, id); assert.equal(input.fingerprint, fingerprint);
            proof = input.workerPayloadFingerprint; return { status: 'claimed', claimId: id }; },
    };
    const dispatch = async ({ payload, key, request }) => { calls.push('dispatch'); buffers = { payload, key };
        assert.equal(createHmac('sha256', key).update(payload).digest('base64'), proof);
        assert.deepEqual(JSON.parse(payload.toString()).input, request.input); return { status: 'completed' }; };
    const run = (body = Buffer.from(JSON.stringify(envelope())), extra = {}) => withLiveUIAdmission(
        { credential: 'credential', identity, body, deployment: {}, modelId: 'registry/model', ...extra },
        { controlPlane, authorization, keyring }, extra.dispatch ?? dispatch);
    return { calls, authorization, controlPlane, keyring, run, buffers: () => buffers };
}

test('inspects all input fields before reserving and claiming the exact worker payload', async () => {
    const f = fixture(); try {
        assert.deepEqual(await f.run(), { status: 'completed' });
        assert.deepEqual(f.calls, ['lookup', 'privacy', 'reserve', 'claim', 'dispatch']);
        for (const value of Object.values(f.buffers())) assert.ok(value.every(byte => byte === 0));
    } finally { f.keyring.dispose(); }
});

test('invalid, denied, duplicate, revoked and cancelled requests do not dispatch', async () => {
    for (const scenario of ['invalid', 'privacy', 'duplicate', 'claim', 'cancel']) {
        const f = fixture(); const controller = new AbortController();
        try {
            if (scenario === 'privacy') f.authorization.inspectLiveUI = async () => ({ allowed: false, code: 'privacy_blocked' });
            if (scenario === 'duplicate') f.controlPlane.reserve = async () => ({ status: 'duplicate', reservationId: id });
            if (scenario === 'claim') f.controlPlane.claim = async () => ({ status: 'unavailable' });
            if (scenario === 'cancel') f.controlPlane.claim = async () => { controller.abort(); return { status: 'claimed', claimId: id }; };
            const body = scenario === 'invalid' ? Buffer.from('{"requestId":1,"requestId":2}') : Buffer.from(JSON.stringify(envelope()));
            const result = await f.run(body, { signal: controller.signal });
            assert.equal(result.status, scenario === 'privacy' ? 'privacy_blocked' : scenario === 'duplicate' ? 'duplicate' : 'unavailable');
            assert.ok(!f.calls.includes('dispatch'));
            if (['invalid', 'privacy'].includes(scenario)) assert.ok(!f.calls.includes('reserve'));
        } finally { f.keyring.dispose(); }
    }
});

test('snapshots the caller body before asynchronous lookup', async () => {
    const f = fixture(); const body = Buffer.from(JSON.stringify(envelope()));
    f.controlPlane.findFingerprintKey = async () => { body.fill(0); return { status: 'not_found' }; };
    try { assert.deepEqual(await f.run(body), { status: 'completed' }); } finally { f.keyring.dispose(); }
});

test('metered admission reserves plan and bounded token capacity together', async () => {
    const f = fixture();
    f.controlPlane.reserveDual = async (_, input) => {
        f.calls.push('dual-reserve'); assert.equal(input.tokenAmount, 2048);
        return { status: 'reserved', reservationId: id };
    };
    try {
        assert.deepEqual(await f.run(Buffer.from(JSON.stringify(envelope())), { tokenAmount: 2048 }), { status: 'completed' });
        assert.deepEqual(f.calls, ['lookup', 'privacy', 'dual-reserve', 'claim', 'dispatch']);
    } finally { f.keyring.dispose(); }
});

test('invalid or unavailable metering never falls back to a plan-only reservation', async () => {
    for (const tokenAmount of [0, 255, 32769, 2048]) {
        const f = fixture();
        try {
            assert.equal((await f.run(Buffer.from(JSON.stringify(envelope())), { tokenAmount })).status, 'unavailable');
            assert.ok(!f.calls.includes('reserve') && !f.calls.includes('claim') && !f.calls.includes('dispatch'));
        } finally { f.keyring.dispose(); }
    }
});
