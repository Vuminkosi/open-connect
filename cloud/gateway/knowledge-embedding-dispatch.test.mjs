import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { createKnowledgeEmbeddingDispatcher } from './knowledge-embedding-dispatch.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const claim = { reservationId: id, claimId: id, key: Buffer.alloc(32, 4) };
const prepared = () => ({ requestId: id, tenantId: 'tenant', expiresAt: '2030-01-01T00:01:00.000Z',
    payload: Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', kind: 'knowledge.embed', taskId: id,
        tenantId: 'tenant', expiresAt: '2030-01-01T00:01:00.000Z', payload: { values: [1] } })) });
const dispatcher = () => createKnowledgeEmbeddingDispatcher({ origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token' });

test('sends a claim-bound envelope and returns owned bytes pending further checks', async () => {
    const original = https.request; let envelope; let owned; let options; let url;
    https.request = (requestUrl, requestOptions, callback) => {
        url = requestUrl;
        options = requestOptions; const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            owned = body; envelope = JSON.parse(body); written();
            const response = new EventEmitter(); response.statusCode = 200; response.headers = {}; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: id,
                kind: 'knowledge.embed', result: { value: 1 } }))); response.emit('end');
        };
        return req;
    };
    try {
        const request = prepared();
        const result = await dispatcher().executeClaimed(request, claim);
        assert.equal(result.status, 'received');
        assert.equal(options.rejectUnauthorized, true);
        assert.equal(url.href, 'https://worker.example/internal/cloud/v1/knowledge-embed/execute');
        assert.equal(envelope.schemaVersion, 'openconnect.cloud.worker.knowledge-embed/v1');
        assert.deepEqual(Buffer.from(envelope.payload, 'base64'), request.payload);
        assert.deepEqual(Buffer.from(envelope.key, 'base64'), claim.key);
        assert.ok(owned.every(value => value === 0));
        assert.equal(JSON.parse(result.bytes).taskId, id);
        result.bytes.fill(0);
    } finally { https.request = original; }
});

test('rejects mismatched claims and malformed worker responses', async () => {
    const original = https.request; let calls = 0;
    https.request = (_url, _options, callback) => {
        calls++; const req = new EventEmitter(); req.destroy = () => {};
        req.end = (_body, written) => {
            written(); const response = new EventEmitter(); response.statusCode = 200; response.headers = {}; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from('{"taskId":"wrong","taskId":"wrong"}')); response.emit('end');
        };
        return req;
    };
    try {
        const request = prepared();
        assert.equal((await dispatcher().executeClaimed(request, { ...claim, key: Buffer.alloc(31) })).status, 'upstream_unavailable');
        assert.equal(calls, 0);
        assert.equal((await dispatcher().executeClaimed(request, claim)).status, 'upstream_unavailable');
        assert.equal(calls, 1);
    } finally { https.request = original; }
});

test('rejects oversized or mismatched payloads and aborted requests before transport', async () => {
    const original = https.request; let calls = 0;
    https.request = () => { calls++; throw new Error('unexpected transport'); };
    try {
        const request = prepared();
        const stopped = AbortSignal.abort();
        assert.equal((await dispatcher().executeClaimed(request, claim, stopped)).status, 'upstream_unavailable');
        assert.equal((await dispatcher().executeClaimed({ ...request, tenantId: 'other' }, claim)).status, 'upstream_unavailable');
        assert.equal((await dispatcher().executeClaimed({ ...request, payload: Buffer.alloc(131073) }, claim)).status, 'upstream_unavailable');
        assert.equal(calls, 0);
    } finally { https.request = original; }
});

test('requires HTTPS and authenticated worker transport configuration', () => {
    for (const options of [
        { origin: 'http://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token' },
        { origin: 'https://worker.example/path', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token' },
        { origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: '', workerToken: 'token' },
        { origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token\n' },
    ]) assert.throws(() => createKnowledgeEmbeddingDispatcher(options), TypeError);
});
