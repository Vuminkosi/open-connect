import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { createKnowledgeEmbeddingProbe } from './knowledge-embedding-probe.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const configuration = { origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token', deploymentId: id };

test('requires the configured worker deployment identity and trusted TLS', async () => {
    const original = https.request; let resultId = id; let options; let url;
    https.request = (requestUrl, requestOptions, callback) => {
        url = requestUrl;
        options = requestOptions; const req = new EventEmitter(); req.destroy = () => {};
        req.end = () => {
            const response = new EventEmitter(); response.statusCode = 200; response.headers = {}; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(JSON.stringify({ ready: true, deploymentId: resultId }))); response.emit('end');
        };
        return req;
    };
    try {
        const probe = createKnowledgeEmbeddingProbe(configuration);
        assert.equal(await probe.probe(), true);
        assert.equal(options.rejectUnauthorized, true);
        assert.equal(url.href, 'https://worker.example/internal/cloud/v1/knowledge-embed/ready');
        resultId = '22222222-2222-2222-2222-222222222222';
        assert.equal(await probe.probe(), false);
        assert.throws(() => createKnowledgeEmbeddingProbe({ ...configuration, origin: 'http://worker.example' }));
    } finally { https.request = original; }
});

test('fails closed for malformed, compressed, oversized or unauthorized readiness responses', async () => {
    const original = https.request; let status = 200; let headers = {}; let body = '';
    https.request = (_url, _options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = () => {
            const response = new EventEmitter(); response.statusCode = status; response.headers = headers; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(body)); response.emit('end');
        };
        return req;
    };
    try {
        const probe = createKnowledgeEmbeddingProbe(configuration);
        body = `{"ready":true,"ready":true,"deploymentId":"${id}"}`;
        assert.equal(await probe.probe(), false);
        body = JSON.stringify({ ready: true, deploymentId: id, extra: true });
        assert.equal(await probe.probe(), false);
        body = 'x'.repeat(4097);
        assert.equal(await probe.probe(), false);
        body = JSON.stringify({ ready: true, deploymentId: id });
        headers = { 'content-encoding': 'gzip' };
        assert.equal(await probe.probe(), false);
        headers = {}; status = 401;
        assert.equal(await probe.probe(), false);
        assert.equal(await probe.probe(AbortSignal.abort()), false);
    } finally { https.request = original; }
});
