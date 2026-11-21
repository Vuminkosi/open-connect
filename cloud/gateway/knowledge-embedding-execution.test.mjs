import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { executeKnowledgeEmbedding } from './knowledge-embedding-execution.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const id = '11111111-1111-1111-1111-111111111111';
const identity = { tenantId: '00000000-0000-0000-0000-000000000001', applicationId: '22222222-2222-2222-2222-222222222222' };
const request = { schemaVersion: 'openconnect.cloud.knowledge-embed/v1', requestId: id, capability: 'knowledge.embed',
    expiresAt: '2030-01-01T00:01:00.000Z', input: { inputKind: 'query', texts: ['private'] } };
const workerResponse = { schemaVersion: 'openconnect.v1', taskId: id, kind: 'knowledge.embed', result: {
    model_id: 'model', fingerprint: 'a'.repeat(64), space_id: 'b'.repeat(64), dimension: 2,
    items: [{ vector: [0.6, 0.8], truncated: false, norm: 1 }] } };

function setup(failAt = '') {
    const calls = []; const response = Buffer.from(JSON.stringify(workerResponse)); let payload; let key;
    const allowed = name => { calls.push(name); return { status: name === failAt ? 'privacy_blocked' : 'allowed' }; };
    const controlPlane = {
        inspectEmbedding: async () => allowed('input'),
        claimEmbedding: async () => { calls.push('claim'); return { status: failAt === 'claim' ? 'denied' : 'claimed', claimId: id }; },
        inspectEmbeddingResponse: async () => allowed('output'),
        completeEmbedding: async () => { calls.push('settle'); return { status: failAt === 'settle' ? 'failed' : 'completed' }; },
    };
    const monitor = { signal: new AbortController().signal, start: async () => { calls.push('monitor'); return failAt !== 'monitor'; },
        recheck: async () => { calls.push('recheck'); return failAt !== 'recheck'; }, dispose: () => calls.push('dispose') };
    const dependencies = { controlPlane, reserve: async () => { calls.push('reserve'); return { status: failAt === 'reserve' ? 'quota_exceeded' : 'reserved',
        reservationId: id, fingerprintKeyId: 'key', fingerprint: 'fingerprint' }; },
    dispatcher: { executeClaimed: async (prepared, claim) => { calls.push('dispatch'); payload = prepared.payload; key = claim.key; return { status: 'received', bytes: response }; } },
    monitorFactory: () => monitor, clock: () => now };
    return { calls, response, dependencies, get payload() { return payload; }, get key() { return key; } };
}

test('checks privacy and settlement before returning an embedding and clears sensitive buffers', async () => {
    const state = setup(); const body = Buffer.from(JSON.stringify(request));
    const outcome = await executeKnowledgeEmbedding({ credential: 'credential', identity, body, deployment: { endpointReference: 'worker' } }, state.dependencies);
    assert.equal(outcome.status, 'completed');
    assert.deepEqual(outcome.result, workerResponse.result);
    assert.deepEqual(state.calls, ['reserve', 'input', 'claim', 'monitor', 'dispatch', 'recheck', 'output', 'recheck', 'settle', 'recheck', 'dispose']);
    assert.ok(state.payload.every(value => value === 0));
    assert.ok(state.key.every(value => value === 0));
    assert.ok(state.response.every(value => value === 0));
    assert.ok(body.some(value => value !== 0));
});

test('denials and failed settlement never release a result', async () => {
    for (const point of ['reserve', 'input', 'claim', 'monitor', 'recheck', 'output', 'settle']) {
        const state = setup(point);
        const outcome = await executeKnowledgeEmbedding({ credential: 'credential', identity, body: Buffer.from(JSON.stringify(request)), deployment: {} }, state.dependencies);
        assert.notEqual(outcome.status, 'completed', point);
        assert.equal(outcome.result, undefined, point);
        if (['reserve', 'input', 'claim', 'monitor'].includes(point)) assert.ok(!state.calls.includes('dispatch'), point);
        if (['reserve', 'input', 'claim', 'monitor', 'recheck', 'output'].includes(point)) assert.ok(!state.calls.includes('settle'), point);
        if (state.payload) assert.ok(state.payload.every(value => value === 0), point);
        if (state.key) assert.ok(state.key.every(value => value === 0), point);
        if (state.calls.includes('dispatch')) assert.ok(state.response.every(value => value === 0), point);
    }
});

test('a broken monitor cleanup cannot expose diagnostics or retain sensitive buffers', async () => {
    const state = setup();
    const factory = state.dependencies.monitorFactory;
    state.dependencies.monitorFactory = () => {
        const monitor = factory();
        monitor.dispose = () => { throw new Error('private diagnostic'); };
        return monitor;
    };
    const result = await executeKnowledgeEmbedding({ credential: 'credential', identity,
        body: Buffer.from(JSON.stringify(request)), deployment: {} }, state.dependencies);
    assert.equal(result.status, 'completed');
    assert.ok(state.payload.every(value => value === 0));
    assert.ok(state.key.every(value => value === 0));
    assert.ok(state.response.every(value => value === 0));
});

test('dependency mutations cannot change the inspected or dispatched snapshots', async () => {
    const state = setup();
    state.dependencies.reserve = async ({ body }) => {
        assert.equal(JSON.parse(body).input.texts[0], 'private');
        body.fill(0);
        return { status: 'reserved', reservationId: id, fingerprintKeyId: 'key', fingerprint: 'fingerprint' };
    };
    state.dependencies.controlPlane.inspectEmbedding = async (_credential, proof) => {
        assert.equal(JSON.parse(proof.body).input.texts[0], 'private');
        proof.body.fill(0);
        return { status: 'allowed' };
    };
    state.dependencies.controlPlane.inspectEmbeddingResponse = async (_credential, proof) => {
        assert.equal(JSON.parse(proof.body).result.items[0].vector[0], 0.6);
        proof.body.fill(0);
        return { status: 'allowed' };
    };
    const result = await executeKnowledgeEmbedding({ credential: 'credential', identity,
        body: Buffer.from(JSON.stringify(request)), deployment: {} }, state.dependencies);
    assert.equal(result.status, 'completed');
    assert.deepEqual(result.result, workerResponse.result);
    assert.ok(state.payload.every(value => value === 0));
    assert.ok(state.response.every(value => value === 0));
});

test('oversized worker responses never reach privacy inspection or settlement and are cleared', async () => {
    const state = setup();
    const oversized = Buffer.alloc(8 * 1024 * 1024 + 1, 65);
    state.dependencies.dispatcher.executeClaimed = async () => ({ status: 'received', bytes: oversized });
    const result = await executeKnowledgeEmbedding({ credential: 'credential', identity,
        body: Buffer.from(JSON.stringify(request)), deployment: {} }, state.dependencies);
    assert.equal(result.status, 'unavailable');
    assert.ok(!state.calls.includes('output'));
    assert.ok(!state.calls.includes('settle'));
    assert.ok(oversized.every(value => value === 0));
});

test('the claim binds the exact worker payload dispatched with its ephemeral key', async () => {
    const state = setup();
    let fingerprint;
    state.dependencies.controlPlane.claimEmbedding = async (_credential, _reservation, claim) => {
        fingerprint = claim.workerPayloadFingerprint;
        return { status: 'claimed', claimId: id };
    };
    const dispatch = state.dependencies.dispatcher.executeClaimed;
    state.dependencies.dispatcher.executeClaimed = async (prepared, claim) => {
        assert.equal(createHmac('sha256', claim.key).update(prepared.payload).digest('base64'), fingerprint);
        return dispatch(prepared, claim);
    };
    const result = await executeKnowledgeEmbedding({ credential: 'credential', identity,
        body: Buffer.from(JSON.stringify(request)), deployment: {} }, state.dependencies);
    assert.equal(result.status, 'completed');
    assert.ok(state.key.every(value => value === 0));
});
