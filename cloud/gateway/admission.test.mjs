import { test } from 'node:test';
import assert from 'node:assert/strict';
import { admit } from './admission.mjs';

test('admission checks identity, policy, deployment, and quota in order', async () => {
    const calls = [];
    const adapters = {
        authenticate: async () => { calls.push('authenticate'); return { id: 'app' }; },
        authorize: async () => { calls.push('authorize'); return { allowed: true }; },
        prepare: async () => { calls.push('prepare'); return { valid: true }; },
        resolveDeployment: async () => { calls.push('deployment'); return { id: 'worker' }; },
        reserve: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: '11111111-1111-1111-1111-111111111111' }; },
    };
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, adapters);
    assert.equal(result.allowed, true);
    assert.deepEqual(calls, ['authenticate', 'authorize', 'prepare', 'deployment', 'reserve']);
});

test('authorization denial never resolves a worker or reserves quota', async () => {
    const calls = [];
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: false, code: 'capability_disabled' }),
        prepare: async () => calls.push('prepare'),
        resolveDeployment: async () => calls.push('deployment'),
        reserve: async () => calls.push('reserve'),
    });
    assert.deepEqual(result, { allowed: false, code: 'capability_disabled' });
    assert.deepEqual(calls, []);
});

test('adapter failure denies admission', async () => {
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, {
        authenticate: async () => { throw new Error('private failure'); },
    });
    assert.deepEqual(result, { allowed: false, code: 'capability_unavailable' });
});

test('missing identity and quota denial never grant admission', async () => {
    let invoked = false;
    assert.deepEqual(await admit({ capability: 'inference.chat' }, {
        authenticate: async () => { invoked = true; },
    }), { allowed: false, code: 'unauthorized' });
    assert.equal(invoked, false);
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: true }),
        prepare: async () => ({ valid: true }),
        resolveDeployment: async () => ({ id: 'worker' }),
        reserve: async () => ({ status: 'quota_exceeded' }),
    });
    assert.deepEqual(result, { allowed: false, code: 'quota_exceeded' });
});


test('duplicate and malformed quota results never authorize a second execution', async () => {
    for (const [result, code] of [
        [{ status: 'duplicate', reservationId: '11111111-1111-1111-1111-111111111111' }, 'conflict'],
        [{ status: 'idempotency_conflict', reservationId: '11111111-1111-1111-1111-111111111111' }, 'idempotency_conflict'],
        [{ status: 'reserved' }, 'capability_unavailable'],
        [{ reservationId: '11111111-1111-1111-1111-111111111111' }, 'capability_unavailable'],
        [{ status: 'reserved', reservationId: 'not-a-guid' }, 'capability_unavailable'],
        [{ status: 'reserved', reservationId: '00000000-0000-0000-0000-000000000000' }, 'capability_unavailable'],
        [null, 'capability_unavailable'],
    ]) {
        const actual = await admit({ credential: 'key', capability: 'inference.chat' }, {
            authenticate: async () => ({ id: 'app' }),
            authorize: async () => ({ allowed: true }),
            prepare: async () => ({ valid: true }),
            resolveDeployment: async () => ({ id: 'worker' }),
            reserve: async () => result,
        });
        assert.deepEqual(actual, { allowed: false, code });
    }
});

test('invalid or unauthorized requests never reserve quota', async () => {
    const calls = [];
    const adapters = {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: true }),
        prepare: async () => ({ valid: false }),
        resolveDeployment: async () => calls.push('deployment'),
        reserve: async () => calls.push('reserve'),
    };
    assert.deepEqual(await admit({ credential: 'key', capability: 'inference.chat' }, adapters),
        { allowed: false, code: 'invalid_request' });
    assert.deepEqual(calls, []);
});

test('cancellation after reservation never grants dispatch', async () => {
    const controller = new AbortController();
    const result = await admit({ credential: 'key', capability: 'inference.chat', signal: controller.signal }, {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: true }),
        prepare: async () => ({ valid: true }),
        resolveDeployment: async () => ({ id: 'worker' }),
        reserve: async () => { controller.abort(); return { status: 'reserved', reservationId: '11111111-1111-1111-1111-111111111111' }; },
    });
    assert.deepEqual(result, { allowed: false, code: 'capability_unavailable' });
});


test('authorization failure never exposes adapter diagnostics or prepares a body', async () => {
    let prepared = false;
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: false, code: 'private database detail' }),
        prepare: async () => { prepared = true; },
    });
    assert.deepEqual(result, { allowed: false, code: 'forbidden' });
    assert.equal(prepared, false);
});
