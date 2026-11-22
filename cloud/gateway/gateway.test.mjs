import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';
import { createLocalServer } from './local-server.mjs';
import http from 'node:http';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { createHmac } from 'node:crypto';
import { createInferenceAccessMonitor } from './inference-access-monitor.mjs';

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

test('classification readiness requires both private control plane and cloud-enabled worker', async () => {
    let control = true; let worker = true; let requiredCloud = false;
    const authorization = { isReady: async () => control };
    const classificationProbe = { probe: async (_, __, requireCloud) => { requiredCloud = requireCloud; return { ready: worker }; } };
    const classificationExecution = {};
    const request = new Request('http://localhost/health/ready');
    const options = { authorization, classificationProbe, classificationExecution };
    const ready = await handle(request, options);
    assert.equal(ready.status, 200);
    assert.deepEqual(await ready.json(), { status: 'ready', capabilities: ['classification.classify'] });
    assert.equal(requiredCloud, true);
    control = false;
    assert.equal((await handle(request, options)).status, 503);
    control = true; worker = false;
    assert.equal((await handle(request, options)).status, 503);
    classificationProbe.probe = async () => { throw new Error('private diagnostic'); };
    const failure = await handle(request, options);
    assert.equal(failure.status, 503);
    assert.doesNotMatch(await failure.text(), /private diagnostic/);
});

test('inference readiness includes the verified local tokenizer and worker', async () => {
    let modelReady = false;
    const authorization = { isReady: async () => true };
    const inferenceExecution = { probe: { probe: async () => true }, tokenizer: { ready: async () => modelReady } };
    const request = new Request('http://localhost/health/ready');
    assert.equal((await handle(request, { authorization, inferenceExecution })).status, 503);
    modelReady = true;
    const response = await handle(request, { authorization, inferenceExecution });
    assert.equal(response.status, 200); assert.deepEqual((await response.json()).capabilities, ['inference.chat']);
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
    assert.match(problem.traceId, /^[0-9a-f]{32}$/);
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

test('classification readiness is checked only after authorization and never enables dispatch', async () => {
    const calls = [];
    const request = { url: 'http://localhost/cloud/v1/classification', method: 'POST', headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); return { bytes: Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.classification/v2', requestId: '11111111-1111-1111-1111-111111111111', capability: 'classification.classify', expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'test' } })) }; } };
    const classificationProbe = { probe: async () => { calls.push('probe'); return { ready: true, labelSets: ['v1'] }; } };
    const deny = { authorize: async () => { calls.push('authorize'); return { authorized: false, code: 'unauthorized' }; } };
    assert.equal((await handle(request, { authorization: deny, classificationProbe })).status, 401);
    assert.deepEqual(calls, ['authorize']);
    calls.length = 0;
    const allow = { authorize: async () => { calls.push('authorize'); return { authorized: true }; },
        matchClassificationDeployment: async () => { calls.push('inventory'); return true; } };
    const missing = { authorize: allow.authorize, matchClassificationDeployment: async () => { calls.push('inventory'); return false; } };
    assert.equal((await handle(request, { authorization: missing, classificationProbe })).status, 503);
    assert.deepEqual(calls, ['authorize', 'inventory']);
    calls.length = 0;
    const unready = { probe: async () => { calls.push('probe'); return { ready: false }; } };
    assert.equal((await handle(request, { authorization: allow, classificationProbe: unready })).status, 503);
    assert.deepEqual(calls, ['authorize', 'inventory', 'probe']);
    calls.length = 0;
    assert.equal((await handle(request, { authorization: allow, classificationProbe })).status, 503);
    assert.deepEqual(calls, ['authorize', 'inventory', 'probe', 'body']);
    calls.length = 0;
    const inference = { url: 'http://localhost/cloud/v1/inference/chat', method: 'POST', headers: request.headers };
    assert.equal((await handle(inference, { authorization: allow, classificationProbe })).status, 503);
    assert.deepEqual(calls, ['authorize']);
});

test('classification invokes the independent privacy boundary before any execution', async () => {
    const calls = [];
    const body = { schemaVersion: 'openconnect.cloud.classification/v2', requestId: '11111111-1111-1111-1111-111111111111', expiresAt: new Date(Date.now() + 60000).toISOString(),
        capability: 'classification.classify', input: { text: 'private input' } };
    const request = { url: 'http://localhost/cloud/v1/classification', method: 'POST', headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); return { bytes: Buffer.from(JSON.stringify(body)) }; } };
    const authorization = { authorize: async () => { calls.push('authorize'); return { authorized: true }; },
        matchClassificationDeployment: async () => { calls.push('inventory'); return true; },
        inspectClassification: async (credential, requestId, content) => { calls.push('privacy'); assert.equal(content, 'private input'); return { allowed: false, code: 'privacy_blocked' }; } };
    const result = await handle(request, { authorization, classificationProbe: { probe: async () => { calls.push('probe'); return { ready: true, labelSets: ['v1'] }; } } });
    assert.equal(result.status, 403);
    assert.equal((await result.json()).code, 'privacy_blocked');
    assert.deepEqual(calls, ['authorize', 'inventory', 'probe', 'body', 'privacy']);
    body.input = { inputRef: 'opaque-handle' }; calls.length = 0;
    assert.equal((await handle(request, { authorization, classificationProbe: { probe: async () => ({ ready: true, labelSets: ['v1'] }) } })).status, 503);
    assert.deepEqual(calls, ['authorize', 'inventory', 'body']);
});

test('enabled classification runs only after authorization, regional readiness and privacy, and returns settled results', async () => {
    const calls = []; let raw;
    const id = '11111111-1111-1111-1111-111111111111';
    const value = { schemaVersion: 'openconnect.cloud.classification/v2', requestId: id,
        expiresAt: new Date(Date.now() + 60000).toISOString(), capability: 'classification.classify', input: { text: 'test input' } };
    const request = { url: 'http://localhost/cloud/v1/classification', method: 'POST',
        headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); raw = Buffer.from(JSON.stringify(value)); return { bytes: raw }; } };
    const authorization = {
        authorize: async () => { calls.push('authorize'); return { authorized: true, identity: { credentialId: id, tenantId: id, applicationId: id, environment: 'Development' } }; },
        matchClassificationDeployment: async () => { calls.push('inventory'); return true; },
        inspectClassification: async () => { calls.push('privacy'); return { allowed: true }; },
        findClassificationFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        reserveClassification: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claimClassification: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; },
        completeClassification: async () => { calls.push('complete'); return { status: 'completed' }; },
    };
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
    const dispatcher = { executeClaimed: async () => { calls.push('dispatch'); return { status: 'completed', result: {
        labels: ['safe'], scores: { safe: 0.9 }, abstained: false, modelRevision: 'revision-1', labelSetVersion: 'v1' } }; } };
    const classificationProbe = { deployment: { endpointReference: 'worker' }, probe: async () => { calls.push('probe'); return { ready: true, labelSets: ['v1'] }; } };
    try {
        const result = await handle(request, { authorization, classificationProbe, classificationExecution: { keyring, dispatcher } });
        assert.equal(result.status, 200);
        const response = await result.json();
        assert.equal(response.schemaVersion, 'openconnect.cloud.classification.result/v1');
        assert.equal(response.requestId, id);
        assert.equal(response.traceId, result.headers.get('x-trace-id'));
        assert.deepEqual(calls, ['authorize', 'inventory', 'probe', 'body', 'privacy', 'lookup', 'reserve', 'claim', 'authorize', 'inventory', 'dispatch', 'authorize', 'inventory', 'complete', 'authorize', 'inventory']);
        assert.ok(raw.every(byte => byte === 0));
        calls.length = 0;
        value.configuration = { labelSetVersion: 'v2' };
        assert.equal((await handle(request, { authorization, classificationProbe, classificationExecution: { keyring, dispatcher } })).status, 503);
        assert.ok(!calls.includes('privacy') && !calls.includes('reserve'));
        delete value.configuration;
        calls.length = 0;
        authorization.inspectClassification = async () => { calls.push('privacy'); return { allowed: false, code: 'privacy_blocked' }; };
        assert.equal((await handle(request, { authorization, classificationProbe, classificationExecution: { keyring, dispatcher } })).status, 403);
        assert.ok(!calls.includes('reserve'));
        calls.length = 0;
        authorization.inspectClassification = async () => ({ allowed: true });
        authorization.reserveClassification = async () => ({ status: 'duplicate', reservationId: id });
        const duplicate = await handle(request, { authorization, classificationProbe, classificationExecution: { keyring, dispatcher } });
        assert.equal(duplicate.status, 409);
        assert.equal((await duplicate.json()).code, 'conflict');
        assert.ok(!calls.includes('dispatch'));
    } finally { keyring.dispose(); }
});

test('inference route gates execution on admission, claim access and output privacy', async () => {
    const calls = []; let raw; let blockOutput = false; let denyInput = false;
    const id = '11111111-1111-1111-1111-111111111111';
    const value = { schemaVersion: 'openconnect.cloud.inference/v1', requestId: id, expiresAt: new Date(Date.now() + 60000).toISOString(),
        capability: 'inference.chat', modelId: 'model', input: { messages: [{ role: 'user', content: 'fixture' }], maxTokens: 5, stream: false } };
    const request = { url: 'http://localhost/cloud/v1/inference/chat', method: 'POST',
        headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); raw = Buffer.from(JSON.stringify(value)); return { bytes: raw }; } };
    const authorization = {
        authorize: async () => { calls.push('authorize'); return { authorized: true, identity: { credentialId: id, tenantId: id, applicationId: id, environment: 'Development' } }; },
        findInferenceFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        inspectInference: async () => { calls.push('input'); return denyInput ? { allowed: false, code: 'privacy_blocked' } : { allowed: true }; },
        reserveInference: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claimInference: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; },
        verifyInferenceClaim: async () => { calls.push('access'); return true; },
        inspectInferenceOutput: async () => { calls.push('output'); return blockOutput ? { allowed: false, code: 'privacy_blocked' } : { allowed: true, code: 'allowed' }; },
    };
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
    const inferenceExecution = { keyring, deployment: { endpointReference: 'worker' },
        probe: { probe: async () => { calls.push('probe'); return true; } }, createAccessMonitor: createInferenceAccessMonitor,
        quote: async ({ payload, key }) => ({ requestId: id, modelId: 'model', maxOutputTokens: 5, inputTokenUpperBound: 3,
            workerPayloadFingerprint: createHmac('sha256', key).update(payload).digest('base64') }),
        dispatcher: { executeClaimed: async () => { calls.push('dispatch'); return { status: 'completed', result: { output: 'safe', usage: { inputTokens: 3, outputTokens: 1 } } }; } } };
    try {
        const response = await handle(request, { authorization, inferenceExecution });
        assert.equal(response.status, 200); assert.equal((await response.json()).output, 'safe');
        assert.deepEqual(calls, ['authorize', 'probe', 'body', 'lookup', 'input', 'reserve', 'claim', 'access', 'dispatch', 'access', 'output', 'access']);
        assert.ok(raw.every(byte => byte === 0));
        calls.length = 0; blockOutput = true;
        assert.equal((await handle(request, { authorization, inferenceExecution })).status, 403);
        assert.ok(calls.includes('output'));
        calls.length = 0; blockOutput = false; denyInput = true;
        assert.equal((await handle(request, { authorization, inferenceExecution })).status, 403);
        assert.ok(!calls.includes('reserve') && !calls.includes('dispatch'));
        calls.length = 0; denyInput = false; value.input.stream = true;
        assert.equal((await handle(request, { authorization, inferenceExecution })).status, 503);
        assert.ok(!calls.includes('reserve') && !calls.includes('dispatch'));
    } finally { keyring.dispose(); }
});

test('trace context is shared across gateway and private checks without carrying request content', async () => {
    const traceId = '1234567890abcdef1234567890abcdef';
    const incoming = `00-${traceId}-0123456789abcdef-01`;
    const body = { schemaVersion: 'openconnect.cloud.classification/v2', requestId: '11111111-1111-1111-1111-111111111111', expiresAt: new Date(Date.now() + 60000).toISOString(),
        capability: 'classification.classify', input: { text: 'sensitive fixture' } };
    const propagated = [];
    const request = { url: 'http://localhost/cloud/v1/classification', method: 'POST',
        headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json', traceparent: incoming }),
        readBody: async () => ({ bytes: Buffer.from(JSON.stringify(body)) }) };
    const authorization = { authorize: async (credential, capability, signal, traceparent) => { propagated.push(traceparent); return { authorized: true }; },
        matchClassificationDeployment: async (credential, deployment, signal, traceparent) => { propagated.push(traceparent); return true; },
        inspectClassification: async (credential, requestId, content, signal, traceparent) => { propagated.push(traceparent); return { allowed: false, code: 'privacy_blocked' }; } };
    const result = await handle(request, { authorization, classificationProbe: { probe: async (signal, traceparent) => { propagated.push(traceparent); return { ready: true, labelSets: ['v1'] }; } } });
    assert.equal((await result.json()).traceId, traceId);
    assert.equal(result.headers.get('x-trace-id'), traceId);
    assert.equal(new Set(propagated).size, 1);
    assert.match(propagated[0], new RegExp(`^00-${traceId}-[0-9a-f]{16}-01$`));
    assert.notEqual(propagated[0], incoming);
    assert.doesNotMatch(propagated[0], /sensitive/);
});

test('classification deadline aborts privacy admission and never returns its late decision', async () => {
    const body = { schemaVersion: 'openconnect.cloud.classification/v2', requestId: '11111111-1111-1111-1111-111111111111',
        capability: 'classification.classify', expiresAt: new Date(Date.now() + 100).toISOString(), input: { text: 'private input' } };
    let observedSignal;
    const request = { url: 'http://localhost/cloud/v1/classification', method: 'POST', headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => ({ bytes: Buffer.from(JSON.stringify(body)) }) };
    const authorization = { authorize: async () => ({ authorized: true }), matchClassificationDeployment: async () => true,
        inspectClassification: async (credential, requestId, content, signal) => { observedSignal = signal; await new Promise(resolve => setTimeout(resolve, 160)); return { allowed: true }; } };
    const response = await handle(request, { authorization, classificationProbe: { probe: async () => ({ ready: true, labelSets: ['v1'] }) } });
    assert.equal(response.status, 504);
    assert.equal((await response.json()).code, 'upstream_timeout');
    assert.equal(observedSignal.aborted, true);
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

test('problem responses include accurate retryable flags and standard error codes', async () => {
    const cases = [
        ['unauthorized', 401, 'unauthorized', false],
        ['forbidden', 403, 'forbidden', false],
        ['capability_not_entitled', 403, 'capability_not_entitled', false],
        ['capability_disabled', 403, 'capability_disabled', false],
        ['dependency_not_enabled', 403, 'dependency_not_enabled', false],
        ['privacy_blocked', 403, 'privacy_blocked', false],
        ['invalid_request', 400, 'invalid_request', false],
        ['invalid_semantic_request', 422, 'invalid_request', false],
        ['payload_too_large', 413, 'invalid_request', false],
        ['unsupported_media_type', 415, 'invalid_request', false],
        ['conflict', 409, 'conflict', false],
        ['duplicate', 409, 'conflict', false],
        ['idempotency_conflict', 409, 'idempotency_conflict', false],
        ['quota_exceeded', 429, 'quota_exceeded', true],
        ['rate_limited', 429, 'rate_limited', true],
        ['capability_unavailable', 503, 'capability_unavailable', true],
        ['detector_unavailable', 503, 'detector_unavailable', true],
        ['upstream_unavailable', 503, 'upstream_unavailable', true],
        ['upstream_timeout', 504, 'upstream_timeout', true],
    ];
    for (const [code, expectedStatus, expectedPublicCode, expectedRetryable] of cases) {
        const response = await handle(
            new Request('http://localhost/cloud/v1/inference/chat', { method: 'POST', headers: { authorization: 'Bearer key' } }),
            { authorization: { authorize: async () => ({ authorized: false, code }) } },
        );
        assert.equal(response.status, expectedStatus, code);
        assert.equal(response.headers.get('content-type'), 'application/problem+json', code);
        assert.equal(response.headers.get('cache-control'), 'no-store', code);
        const body = await response.json();
        assert.equal(body.type, 'about:blank', code);
        assert.equal(body.status, expectedStatus, code);
        assert.equal(body.code, expectedPublicCode, code);
        assert.equal(body.retryable, expectedRetryable, code);
        assert.match(body.traceId, /^[0-9a-f]{32}$/, code);
        assert.equal(body.traceId, response.headers.get('x-trace-id'), code);
    }
});

test('GET /cloud/v1/health/ready uses the same fail-closed readiness handler without leaking backend URLs', async () => {
    const unconfigured = await handle(new Request('http://localhost/cloud/v1/health/ready'));
    assert.equal(unconfigured.status, 503);
    assert.equal(unconfigured.headers.get('cache-control'), 'no-store');
    const unconfiguredBody = await unconfigured.json();
    assert.equal(unconfiguredBody.code, 'capability_unavailable');
    assert.equal(unconfiguredBody.retryable, true);

    const authorization = { isReady: async () => true };
    const inferenceExecution = { probe: { probe: async () => true }, tokenizer: { ready: async () => true } };
    const ready = await handle(new Request('http://localhost/cloud/v1/health/ready'), { authorization, inferenceExecution });
    assert.equal(ready.status, 200);
    assert.deepEqual(await ready.json(), { status: 'ready', capabilities: ['inference.chat'] });

    const failing = await handle(new Request('http://localhost/cloud/v1/health/ready'), {
        authorization,
        inferenceExecution: {
            probe: { probe: async () => { throw new Error('https://internal-worker.private:9443 failed'); } },
            tokenizer: { ready: async () => true },
        },
    });
    assert.equal(failing.status, 503);
    const failingText = await failing.text();
    assert.doesNotMatch(failingText, /internal-worker\.private/);
    const failingBody = JSON.parse(failingText);
    assert.equal(failingBody.code, 'capability_unavailable');
    assert.equal(failingBody.retryable, true);
});

test('local server configures bounded headersTimeout and extended requestTimeout and includes retryable on malformed requests', async () => {
    const server = createLocalServer();
    assert.equal(server.headersTimeout, 10_000);
    assert.equal(server.requestTimeout, 310_000);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const res = await new Promise((resolve, reject) => {
            const req = http.request({ hostname: '127.0.0.1', port: server.address().port, path: 'http://[::1', method: 'GET' }, response => {
                const chunks = [];
                response.on('data', chunk => chunks.push(chunk));
                response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(chunks).toString('utf8') }));
            });
            req.on('error', reject);
            req.end();
        });
        assert.equal(res.status, 400);
        assert.equal(res.headers['content-type'], 'application/problem+json');
        const problem = JSON.parse(res.body);
        assert.equal(problem.code, 'invalid_request');
        assert.equal(problem.retryable, false);
        assert.match(problem.traceId, /^[0-9a-f]{32}$/);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('GET /cloud/v1/capabilities fails closed without authorization and rejects unauthenticated requests', async () => {
    const unconfigured = await handle(new Request('http://localhost/cloud/v1/capabilities'));
    assert.equal(unconfigured.status, 503);
    assert.equal(unconfigured.headers.get('cache-control'), 'no-store');
    assert.equal(unconfigured.headers.get('content-type'), 'application/problem+json');
    const unconfiguredBody = await unconfigured.json();
    assert.equal(unconfiguredBody.code, 'capability_unavailable');
    assert.equal(unconfiguredBody.retryable, true);

    let authorizeCalls = 0;
    const authorization = {
        authorize: async () => { authorizeCalls++; return { authorized: false, code: 'unauthorized' }; },
    };
    for (const header of [undefined, 'Basic secret', 'Bearer ', 'Bearer a, Bearer b']) {
        const res = await handle(
            new Request('http://localhost/cloud/v1/capabilities', { headers: header ? { authorization: header } : {} }),
            { authorization },
        );
        assert.equal(res.status, 401);
        assert.equal(res.headers.get('cache-control'), 'no-store');
        assert.equal(res.headers.get('content-type'), 'application/problem+json');
        const body = await res.json();
        assert.equal(body.code, 'unauthorized');
        assert.equal(body.retryable, false);
        assert.match(body.traceId, /^[0-9a-f]{32}$/);
        assert.equal(body.traceId, res.headers.get('x-trace-id'));
    }
    assert.equal(authorizeCalls, 0);

    const rejected = await handle(
        new Request('http://localhost/cloud/v1/capabilities', { headers: { authorization: 'Bearer invalid-key' } }),
        { authorization },
    );
    assert.equal(rejected.status, 401);
    const rejectedBody = await rejected.json();
    assert.equal(rejectedBody.code, 'unauthorized');
    assert.equal(rejectedBody.retryable, false);
    assert.equal(authorizeCalls, 1);

    const errored = await handle(
        new Request('http://localhost/cloud/v1/capabilities', { headers: { authorization: 'Bearer key' } }),
        { authorization: { authorize: async () => { throw new Error('https://internal-auth.private:8443 failed'); } } },
    );
    assert.equal(errored.status, 503);
    const erroredText = await errored.text();
    assert.doesNotMatch(erroredText, /internal-auth\.private/);
    assert.equal(JSON.parse(erroredText).code, 'capability_unavailable');
});

test('GET /cloud/v1/capabilities returns sanitized capability statuses for authenticated callers without leaking internals', async () => {
    const traceId = 'abcdef0123456789abcdef0123456789';
    const seen = [];
    const authorization = {
        authorize: async (credential, capability, signal, traceparent) => {
            seen.push({ credential, capability, traceparent });
            return { authorized: true, identity: { tenantId: '11111111-1111-1111-1111-111111111111' } };
        },
    };
    const sensitiveConfig = {
        authorization,
        inferenceExecution: {
            deployment: { endpointReference: 'https://internal-inference.private:9443', deploymentId: 'dep-inf-secret-01' },
            secretToken: 'top-secret-inference-key',
        },
        voiceExecution: {
            deployment: { endpointReference: 'https://internal-voice.private:9444', deploymentId: 'dep-voice-secret-02' },
        },
        // classificationExecution without classificationProbe must stay unavailable
        classificationExecution: { deploymentId: 'dep-cls-secret-03' },
    };

    const response = await handle(
        new Request('http://localhost/cloud/v1/capabilities', {
            headers: {
                authorization: 'Bearer tenant-credential',
                traceparent: `00-${traceId}-0123456789abcdef-01`,
            },
        }),
        sensitiveConfig,
    );
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal(response.headers.get('x-trace-id'), traceId);
    assert.equal(seen.length, 1);
    assert.equal(seen[0].credential, 'tenant-credential');
    assert.equal(seen[0].capability, 'capabilities.read');
    assert.match(seen[0].traceparent, new RegExp(`^00-${traceId}-[0-9a-f]{16}-01$`));

    const text = await response.text();
    assert.doesNotMatch(text, /internal-inference\.private/);
    assert.doesNotMatch(text, /internal-voice\.private/);
    assert.doesNotMatch(text, /dep-inf-secret-01/);
    assert.doesNotMatch(text, /dep-voice-secret-02/);
    assert.doesNotMatch(text, /dep-cls-secret-03/);
    assert.doesNotMatch(text, /top-secret-inference-key/);
    assert.doesNotMatch(text, /tenant-credential/);

    const body = JSON.parse(text);
    assert.equal(body.traceId, traceId);
    assert.deepEqual(body.capabilities, [
        { id: 'inference.chat', status: 'available', streamingSupported: false },
        { id: 'inference.liveui', status: 'unavailable', streamingSupported: false },
        { id: 'classification.classify', status: 'unavailable', streamingSupported: false },
        { id: 'voice.synthesize', status: 'available', streamingSupported: false },
        { id: 'analytics.analyze', status: 'unavailable', streamingSupported: false },
        { id: 'knowledge.embed', status: 'unavailable', streamingSupported: false },
        { id: 'translation.translate', status: 'unavailable', streamingSupported: false },
        { id: 'security.inspect', status: 'unavailable', streamingSupported: false },
        { id: 'speech.transcribe', status: 'unavailable', streamingSupported: false },
        { id: 'voice.realtime', status: 'unavailable', streamingSupported: false },
        { id: 'navigator.execute', status: 'unavailable', streamingSupported: false },
        { id: 'agents.execute', status: 'unavailable', streamingSupported: false },
        { id: 'outbox.publish', status: 'unavailable', streamingSupported: false },
    ]);

    const allWired = await handle(
        new Request('http://localhost/cloud/v1/capabilities', { headers: { authorization: 'Bearer tenant-credential' } }),
        {
            authorization,
            inferenceExecution: {},
            liveUIExecution: {},
            classificationProbe: {},
            classificationExecution: {},
            voiceExecution: {},
            analyticsExecution: {},
            knowledgeEmbeddingExecution: {},
        },
    );
    assert.equal(allWired.status, 200);
    const allWiredBody = await allWired.json();
    const byId = new Map(allWiredBody.capabilities.map(item => [item.id, item]));
    for (const id of ['inference.chat', 'inference.liveui', 'classification.classify', 'voice.synthesize', 'analytics.analyze', 'knowledge.embed']) {
        assert.equal(byId.get(id)?.status, 'available', id);
        assert.equal(byId.get(id)?.streamingSupported, false, id);
    }
    for (const id of ['translation.translate', 'security.inspect', 'speech.transcribe', 'voice.realtime', 'navigator.execute', 'agents.execute', 'outbox.publish']) {
        assert.equal(byId.get(id)?.status, 'unavailable', id);
        assert.equal(byId.get(id)?.streamingSupported, false, id);
    }
});
