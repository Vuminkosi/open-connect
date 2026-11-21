import { test } from 'node:test';
import assert from 'node:assert/strict';
import { prepareKnowledgeEmbeddingPayload } from './knowledge-embedding-payload.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const identity = { tenantId: '00000000-0000-0000-0000-000000000001', applicationId: '22222222-2222-2222-2222-222222222222' };
const request = { schemaVersion: 'openconnect.cloud.knowledge-embed/v1', requestId: '11111111-1111-1111-1111-111111111111',
    capability: 'knowledge.embed', expiresAt: '2030-01-01T00:01:00.000Z', input: { inputKind: 'query', texts: ['private text'] } };
const body = value => Buffer.from(JSON.stringify(value));

test('binds authenticated tenant and validated fields to a worker envelope', () => {
    const original = body(request);
    const prepared = prepareKnowledgeEmbeddingPayload(original, identity, now);
    assert.ok(prepared);
    const value = JSON.parse(prepared.payload.toString('utf8'));
    assert.deepEqual(Object.keys(value), ['schemaVersion', 'taskId', 'kind', 'tenantId', 'issuedAt', 'expiresAt', 'payload']);
    assert.equal(value.tenantId, '00000000000000000000000001');
    assert.equal(value.taskId, request.requestId);
    assert.equal(value.kind, 'knowledge.embed');
    assert.deepEqual(value.payload, request.input);
    prepared.payload.fill(0);
    assert.ok(original.some(byte => byte !== 0));
});

test('rejects tenant injection, invalid identities and expired requests', () => {
    assert.equal(prepareKnowledgeEmbeddingPayload(body({ ...request, tenantId: 'forged' }), identity, now), null);
    assert.equal(prepareKnowledgeEmbeddingPayload(body(request), { ...identity, tenantId: 'invalid' }, now), null);
    assert.equal(prepareKnowledgeEmbeddingPayload(body(request), { ...identity, applicationId: 'invalid' }, now), null);
    assert.equal(prepareKnowledgeEmbeddingPayload(body(request), identity, now + 60000), null);
});
