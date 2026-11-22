import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseKnowledgeEmbeddingRequest, withKnowledgeEmbeddingRequest } from './knowledge-embedding-request.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const fixture = JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-knowledge-embed-request-v1.json', import.meta.url), 'utf8'));
const encode = value => Buffer.from(JSON.stringify(value));
function request(body, headers = { 'content-type': 'application/json' }) {
    const bytes = Buffer.from(body);
    return { headers: new Headers(headers), readBody: async () => ({ bytes }), bytes };
}

test('validates admission without accepting caller-controlled tenant scope', () => {
    const parsed = parseKnowledgeEmbeddingRequest(encode(fixture), now);
    assert.equal(parsed.capability, 'knowledge.embed');
    const invalid = JSON.parse(readFileSync(new URL('../../contracts/fixtures/invalid/cloud-knowledge-embed-request-v1.json', import.meta.url), 'utf8'));
    assert.throws(() => parseKnowledgeEmbeddingRequest(encode(invalid), now));
    const unicode = structuredClone(fixture);
    unicode.input.texts = ['😀'.repeat(8192)];
    assert.equal([...parseKnowledgeEmbeddingRequest(encode(unicode), now).input.texts[0]].length, 8192);
    assert.throws(() => { parsed.input.texts[0] = 'changed'; }, TypeError);
    for (const change of [
        value => { value.tenantId = 'forged'; }, value => { value.input.extra = true; },
        value => { value.input.inputKind = 'other'; }, value => { value.input.texts = []; },
        value => { value.input.texts = Array(33).fill('x'); }, value => { value.input.texts = ['\ud800']; },
        value => { value.input.texts = ['x'.repeat(8193)]; },
        value => { value.input.texts = Array(9).fill('x'.repeat(8192)); },
        value => { value.expiresAt = '2030-01-01T00:05:00.001Z'; },
        value => { value.expiresAt = '2030-02-30T00:01:00.000Z'; },
    ]) {
        const value = structuredClone(fixture); change(value);
        assert.throws(() => parseKnowledgeEmbeddingRequest(encode(value), now));
    }
    const duplicate = JSON.stringify(fixture).replace('"inputKind":', '"inputKind":"passage","inputKind":');
    assert.throws(() => parseKnowledgeEmbeddingRequest(Buffer.from(duplicate), now));
    assert.throws(() => parseKnowledgeEmbeddingRequest(Buffer.from([0xff]), now));
});

test('bounds the body and clears bytes on success, rejection and callback failure', async () => {
    for (const mode of ['success', 'reject', 'throw']) {
        const value = structuredClone(fixture);
        if (mode === 'reject') value.input.texts = [];
        const req = request(JSON.stringify(value));
        const result = withKnowledgeEmbeddingRequest(req, () => {
            if (mode === 'throw') throw new Error('private diagnostic');
            return 'done';
        }, () => now);
        if (mode === 'throw') await assert.rejects(result);
        else if (mode === 'reject') assert.deepEqual(await result, { code: 'invalid_semantic_request' });
        else assert.equal(await result, 'done');
        assert.ok(req.bytes.every(byte => byte === 0));
    }
    assert.deepEqual(await withKnowledgeEmbeddingRequest(request('{}', { 'content-type': 'text/plain' }), () => {}, () => now),
        { code: 'unsupported_media_type' });
    assert.deepEqual(await withKnowledgeEmbeddingRequest(request('{}', { 'content-type': 'application/json', 'content-length': '131073' }), () => {}, () => now),
        { code: 'payload_too_large' });
});
