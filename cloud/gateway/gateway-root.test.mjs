import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';

test('root identifies the API without asserting dependency readiness', async () => {
    const response = await handle(new Request('http://localhost/'));
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.deepEqual(await response.json(), { service: 'OpenConnect Cloud Gateway', description: 'Cloud capability API',
        readiness: '/health/ready', liveness: '/health/live' });
    assert.equal((await handle(new Request('http://localhost/health/ready'))).status, 503);
});
