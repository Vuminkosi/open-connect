import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';

test('liveness responds independently of Cloud dependencies', async () => {
    const result = handle(new Request('http://localhost/health/live'));
    assert.equal(result.status, 200);
    assert.equal((await result.json()).status, 'live');
});

test('capability calls fail closed until admission is available', async () => {
    const result = handle(new Request('http://localhost/cloud/v1/inference/chat', { method: 'POST' }));
    assert.equal(result.status, 503);
    assert.equal(result.headers.get('cache-control'), 'no-store');
    assert.equal((await result.json()).code, 'capability_unavailable');
});
