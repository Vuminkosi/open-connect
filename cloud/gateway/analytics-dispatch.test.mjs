import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { createAnalyticsDispatcher } from './analytics-dispatch.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const claim = { reservationId: id, claimId: id, key: Buffer.alloc(32, 4) };
const prepared = () => ({ requestId: id, tenantId: 'tenant', expiresAt: '2030-01-01T00:01:00.000Z',
    payload: Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', kind: 'analytics.analyze', taskId: id,
        tenantId: 'tenant', expiresAt: '2030-01-01T00:01:00.000Z', payload: { values: [1] } })) });
const dispatcher = () => createAnalyticsDispatcher({ origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token' });

test('sends a claim-bound envelope and returns owned bytes pending further checks', async () => {
    const original = https.request; let envelope; let owned; let options;
    https.request = (_url, requestOptions, callback) => {
        options = requestOptions; const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            owned = body; envelope = JSON.parse(body); written();
            const response = new EventEmitter(); response.statusCode = 200; response.headers = {}; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: id,
                kind: 'analytics.analyze', result: { value: 1 } }))); response.emit('end');
        };
        return req;
    };
    try {
        const request = prepared();
        const result = await dispatcher().executeClaimed(request, claim);
        assert.equal(result.status, 'received');
        assert.equal(options.rejectUnauthorized, true);
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
