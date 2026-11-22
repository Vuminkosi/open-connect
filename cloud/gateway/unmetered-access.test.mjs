import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';

test('gateway access and management-shaped requests never enter capability admission', async () => {
    let authorizations = 0;
    let reservations = 0;
    const authorization = {
        isReady: async () => true,
        authorize: async () => { authorizations++; throw new Error('unexpected authorization'); },
        reserveInference: async () => { reservations++; throw new Error('unexpected reservation'); },
    };
    const voiceExecution = {
        probe: { probe: async () => true },
        outputScreen: { isReady: async () => true },
    };
    const headers = { authorization: 'Bearer tenant-credential' };
    for (const [method, path, expected] of [
        ['GET', '/', 200],
        ['GET', '/health/live', 200],
        ['GET', '/health/ready', 200],
        ['GET', '/cloud/api/v1/tenants', 400],
        ['POST', '/cloud/api/v1/tenants', 400],
    ]) {
        const response = await handle(new Request(`http://localhost${path}`, { method, headers }), { authorization, voiceExecution });
        assert.equal(response.status, expected, `${method} ${path}`);
    }
    assert.equal(authorizations, 0);
    assert.equal(reservations, 0);
});
