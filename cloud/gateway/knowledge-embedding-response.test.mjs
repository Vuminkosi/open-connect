import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { validateKnowledgeEmbeddingResponse } from './knowledge-embedding-response.mjs';

const request = { requestId: '11111111-1111-1111-1111-111111111111', input: { texts: ['first', 'second'] } };
const response = { schemaVersion: 'openconnect.v1', taskId: request.requestId, kind: 'knowledge.embed',
    result: { model_id: 'model', fingerprint: 'a'.repeat(64), dimension: 2, space_id: 'b'.repeat(64),
        items: [{ vector: [0.6, 0.8], truncated: false, norm: 1 }, { vector: [1, 0], truncated: true, norm: 1 }] } };
const encode = value => Buffer.from(JSON.stringify(value));

test('accepts only a matching bounded normalized embedding response', () => {
    assert.deepEqual(validateKnowledgeEmbeddingResponse(encode(response), request), response);
    for (const change of [
        value => { value.taskId = 'another'; }, value => { value.extra = true; },
        value => { value.result.model_id = 'private\nmodel'; }, value => { value.result.space_id = 'A'.repeat(64); },
        value => { value.result.dimension = 8193; }, value => { value.result.items.pop(); },
        value => { value.result.items[0].vector = [1]; }, value => { value.result.items[0].vector = [0, 0]; },
        value => { value.result.items[0].vector = [1, 1]; }, value => { value.result.items[0].norm = 0.2; },
        value => { value.result.items[0].truncated = 'false'; }, value => { value.result.items[0].extra = true; },
    ]) {
        const value = structuredClone(response); change(value);
        assert.equal(validateKnowledgeEmbeddingResponse(encode(value), request), null);
    }
    const duplicate = JSON.stringify(response).replace('"dimension":', '"dimension":3,"dimension":');
    assert.equal(validateKnowledgeEmbeddingResponse(Buffer.from(duplicate), request), null);
    assert.equal(validateKnowledgeEmbeddingResponse(Buffer.from([0xff]), request), null);
    assert.equal(validateKnowledgeEmbeddingResponse(Buffer.alloc(8 * 1024 * 1024 + 1), request), null);
});

test('public response fixture matches the validated worker result', () => {
    const fixture = JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-knowledge-embed-response-v1.json', import.meta.url)));
    const worker = { schemaVersion: 'openconnect.v1', taskId: fixture.requestId, kind: 'knowledge.embed', result: fixture.result };
    assert.deepEqual(validateKnowledgeEmbeddingResponse(encode(worker), { requestId: fixture.requestId, input: { texts: ['fixture'] } }), worker);
});
