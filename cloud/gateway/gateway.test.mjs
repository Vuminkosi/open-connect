import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';
import { createLocalServer } from './local-server.mjs';
import http from 'node:http';

test('liveness responds independently of Cloud dependencies', async () => {
    const result = handle(new Request('http://localhost/health/live'));
    assert.equal(result.status, 200);
    assert.equal((await result.json()).status, 'live');
});

test('local adapter denies TRACE without crashing the server', async () => {
    const server = createLocalServer();
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const status = await new Promise((resolve, reject) => {
            const req = http.request({ hostname: '127.0.0.1', port: server.address().port, path: '/cloud/v1/inference/chat', method: 'TRACE' }, res => {
                res.resume();
                res.on('end', () => resolve(res.statusCode));
            });
            req.on('error', reject);
            req.end();
        });
        assert.equal(status, 503);
    } finally {
        await new Promise(resolve => server.close(resolve));
    }
});

test('capability calls fail closed until admission is available', async () => {
    const result = handle(new Request('http://localhost/cloud/v1/inference/chat', { method: 'POST' }));
    assert.equal(result.status, 503);
    assert.equal(result.headers.get('cache-control'), 'no-store');
    assert.equal(result.headers.get('content-type'), 'application/problem+json');
    const problem = await result.json();
    assert.equal(problem.code, 'capability_unavailable');
    assert.match(problem.traceId, /^[0-9a-f-]{36}$/);
    assert.equal(problem.traceId, result.headers.get('x-trace-id'));
});
