import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';
import { createLocalServer } from './local-server.mjs';
import http from 'node:http';

test('liveness responds independently of Cloud dependencies', async () => {
    const result = await handle(new Request('http://localhost/health/live'));
    assert.equal(result.status, 200);
    assert.equal((await result.json()).status, 'live');
});

test('readiness fails closed even when authorization is configured', async () => {
    let calls = 0;
    const authorization = { authorize: async () => { calls++; return { authorized: true }; } };
    for (const options of [{}, { authorization }]) {
        const result = await handle(new Request('http://localhost/health/ready'), options);
        assert.equal(result.status, 503);
        assert.equal(result.headers.get('cache-control'), 'no-store');
        assert.equal((await result.json()).code, 'capability_unavailable');
    }
    assert.equal(calls, 0);
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
    const result = await handle(new Request('http://localhost/cloud/v1/inference/chat', { method: 'POST' }));
    assert.equal(result.status, 503);
    assert.equal(result.headers.get('cache-control'), 'no-store');
    assert.equal(result.headers.get('content-type'), 'application/problem+json');
    const problem = await result.json();
    assert.equal(problem.code, 'capability_unavailable');
    assert.match(problem.traceId, /^[0-9a-f-]{36}$/);
    assert.equal(problem.traceId, result.headers.get('x-trace-id'));
});


test('gateway authorizes mapped routes before touching a request body', async () => {
    const seen = [];
    const request = { url: 'http://localhost/cloud/v1/inference/chat', method: 'POST',
        headers: new Headers({ authorization: 'Bearer server-key' }),
        get body() { throw new Error('request body must not be read'); } };
    const authorization = { authorize: async (credential, capability) => {
        seen.push([credential, capability]);
        return { authorized: true, identity: { tenantId: 'private' } };
    } };
    const result = await handle(request, { authorization });
    assert.equal(result.status, 503);
    assert.equal((await result.json()).code, 'capability_unavailable');
    assert.deepEqual(seen, [['server-key', 'inference.chat']]);
});

test('gateway denies missing, rejected, and malformed authorization safely', async () => {
    let calls = 0;
    const authorization = { authorize: async () => { calls++; return { authorized: false, code: 'unauthorized' }; } };
    const target = 'http://localhost/cloud/v1/classification';
    for (const header of [undefined, 'Basic key', 'Bearer key, Bearer other', 'Bearer ']) {
        const request = new Request(target, { method: 'POST', headers: header ? { authorization: header } : {} });
        assert.equal((await handle(request, { authorization })).status, 401);
    }
    assert.equal(calls, 0);
    const rejected = await handle(new Request(target, { method: 'POST', headers: { authorization: 'Bearer key' } }), { authorization });
    assert.equal(rejected.status, 401);
    assert.equal(calls, 1);
    const failure = await handle(new Request(target, { method: 'POST', headers: { authorization: 'Bearer key' } }),
        { authorization: { authorize: async () => ({ authorized: false, code: '__proto__' }) } });
    assert.equal(failure.status, 503);
    assert.equal((await failure.json()).code, 'capability_unavailable');
});

test('local server ignores duplicate authorization headers', async () => {
    let calls = 0;
    const server = createLocalServer({ authorize: async () => { calls++; return { authorized: true }; } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const status = await new Promise((resolve, reject) => {
            const req = http.request({ hostname: '127.0.0.1', port: server.address().port, path: '/cloud/v1/inference/chat',
                method: 'POST', headers: ['Host', 'localhost', 'Content-Length', '12', 'Authorization', 'Bearer first', 'Authorization', 'Bearer second'] }, res => {
                res.resume(); res.on('end', () => resolve(res.statusCode));
            });
            req.on('error', reject); req.end('private body');
        });
        assert.equal(status, 401);
        assert.equal(calls, 0);
    } finally { await new Promise(resolve => server.close(resolve)); }
});
