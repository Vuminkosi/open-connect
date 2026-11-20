import { test } from 'node:test';
import assert from 'node:assert/strict';
import { admit } from './admission.mjs';

test('admission checks identity, policy, deployment, and quota in order', async () => {
    const calls = [];
    const adapters = {
        authenticate: async () => { calls.push('authenticate'); return { id: 'app' }; },
        authorize: async () => { calls.push('authorize'); return { allowed: true }; },
        resolveDeployment: async () => { calls.push('deployment'); return { id: 'worker' }; },
        reserve: async () => { calls.push('reserve'); return { id: 'reservation' }; },
    };
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, adapters);
    assert.equal(result.allowed, true);
    assert.deepEqual(calls, ['authenticate', 'authorize', 'deployment', 'reserve']);
});

test('authorization denial never resolves a worker or reserves quota', async () => {
    const calls = [];
    const result = await admit({ credential: 'key', capability: 'inference.chat' }, {
        authenticate: async () => ({ id: 'app' }),
        authorize: async () => ({ allowed: false, code: 'capability_disabled' }),
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
        resolveDeployment: async () => ({ id: 'worker' }),
        reserve: async () => null,
    });
    assert.deepEqual(result, { allowed: false, code: 'quota_exceeded' });
});
