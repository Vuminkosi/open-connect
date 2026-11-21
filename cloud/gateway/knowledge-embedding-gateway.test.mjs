import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { credentialId: id, tenantId: id, applicationId: '22222222-2222-2222-2222-222222222222', environment: 'Development' };

test('embedding route requires authorization and settlement before returning a validated result', async () => {
    const calls = [];
    const authorization = { authorize: async (_credential, capability) => { calls.push(capability); return { authorized: true, identity }; } };
    const controlPlane = { matchEmbeddingDeployment: async () => true, inspectEmbedding: async () => ({ status: 'allowed' }),
        claimEmbedding: async () => ({ status: 'claimed', claimId: id }), inspectEmbeddingResponse: async () => ({ status: 'allowed' }),
        completeEmbedding: async () => { calls.push('complete'); return { status: 'completed' }; } };
    const result = { model_id: 'model', fingerprint: 'a'.repeat(64), dimension: 2, space_id: 'b'.repeat(64),
        items: [{ vector: [0.6, 0.8], truncated: false, norm: 1 }] };
    const knowledgeEmbeddingExecution = { controlPlane, probe: { probe: async () => true }, deployment: { endpointReference: 'worker' },
        reserve: async () => ({ status: 'reserved', reservationId: id, fingerprintKeyId: 'active', fingerprint: Buffer.alloc(32).toString('base64') }),
        monitorFactory: () => ({ signal: new AbortController().signal, start: async () => true, recheck: async () => true, dispose() {} }),
        dispatcher: { executeClaimed: async () => ({ status: 'received', bytes: Buffer.from(JSON.stringify({
            schemaVersion: 'openconnect.v1', taskId: id, kind: 'knowledge.embed', result })) }) } };
    const value = { schemaVersion: 'openconnect.cloud.knowledge-embed/v1', requestId: id, capability: 'knowledge.embed',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { inputKind: 'query', texts: ['forest'] } };
    const bytes = Buffer.from(JSON.stringify(value));
    const request = { method: 'POST', url: 'http://localhost/cloud/v1/knowledge/embeddings',
        headers: new Headers({ authorization: 'Bearer token', 'content-type': 'application/json' }), readBody: async () => ({ bytes }) };
    const response = await handle(request, { authorization, knowledgeEmbeddingExecution });
    assert.equal(response.status, 200);
    assert.deepEqual((await response.json()).result, result);
    assert.deepEqual(calls, ['knowledge.embed', 'complete']);
    assert.ok(bytes.every(byte => byte === 0));

    for (const [stage, expectedStatus] of [['request', 403], ['response', 403], ['completion', 503]]) {
        controlPlane.inspectEmbedding = async () => ({ status: stage === 'request' ? 'privacy_blocked' : 'allowed' });
        controlPlane.inspectEmbeddingResponse = async () => ({ status: stage === 'response' ? 'privacy_blocked' : 'allowed' });
        controlPlane.completeEmbedding = async () => ({ status: stage === 'completion' ? 'unavailable' : 'completed' });
        const retryBytes = Buffer.from(JSON.stringify(value));
        const rejected = await handle({ ...request, readBody: async () => ({ bytes: retryBytes }) }, { authorization, knowledgeEmbeddingExecution });
        assert.equal(rejected.status, expectedStatus);
        assert.equal((await rejected.json()).result, undefined);
        assert.ok(retryBytes.every(byte => byte === 0));
    }
});

test('embedding route rejects unauthorized callers before reading a body', async () => {
    let read = false;
    const response = await handle({ method: 'POST', url: 'http://localhost/cloud/v1/knowledge/embeddings',
        headers: new Headers({ authorization: 'Bearer token', 'content-type': 'application/json' }),
        readBody: async () => { read = true; throw new Error('must not read'); } },
    { authorization: { authorize: async () => ({ authorized: false, code: 'forbidden' }) } });
    assert.equal(response.status, 403);
    assert.equal(read, false);
});

test('readiness and requests fail closed while the embedding worker is unavailable', async () => {
    let ready = false; let reads = 0;
    const authorization = { isReady: async () => true, authorize: async () => ({ authorized: true, identity }) };
    const knowledgeEmbeddingExecution = { probe: { probe: async () => ready }, deployment: {},
        controlPlane: { matchEmbeddingDeployment: async () => true } };
    const dependencies = { authorization, knowledgeEmbeddingExecution };
    const health = { method: 'GET', url: 'http://localhost/health/ready', headers: new Headers() };
    assert.equal((await handle(health, dependencies)).status, 503);
    const request = { method: 'POST', url: 'http://localhost/cloud/v1/knowledge/embeddings',
        headers: new Headers({ authorization: 'Bearer token', 'content-type': 'application/json' }),
        readBody: async () => { reads++; throw new Error('unexpected body read'); } };
    assert.equal((await handle(request, dependencies)).status, 503);
    assert.equal(reads, 0);
    ready = true;
    const response = await handle(health, dependencies);
    assert.equal(response.status, 200);
    assert.deepEqual((await response.json()).capabilities, ['knowledge.embed']);
});
