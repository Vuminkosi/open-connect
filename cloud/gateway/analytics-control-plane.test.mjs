import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { createAnalyticsQuotaClient } from './analytics-control-plane.mjs';
import { reserveAnalyticsRequest } from './analytics-quota.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { readFileSync } from 'node:fs';

const id = '11111111-1111-1111-1111-111111111111';
const client = () => createAnalyticsQuotaClient({ origin: 'https://control.example', ca: 'ca', certificate: 'cert', privateKey: 'key' });
const reservation = { requestId: id, fingerprintKeyId: 'active', fingerprint: Buffer.alloc(32, 7).toString('base64'), expiresAt: '2030-01-01T00:01:00Z' };

test('uses mTLS and accepts bounded quota responses while clearing the request buffer', async () => {
    const original = https.request; const calls = [];
    https.request = (url, options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            calls.push({ url: url.href, options, body: body && Buffer.from(body), owned: body });
            written?.();
            const response = new EventEmitter(); response.headers = {}; response.statusCode = 200; response.destroy = () => {};
            callback(response);
            response.emit('data', Buffer.from(JSON.stringify(options.method === 'GET'
                ? { status: 'not_found', keyId: null, expiresAt: null }
                : { status: 'reserved', reservationId: id })));
            response.emit('end');
        };
        return req;
    };
    try {
        const quota = client();
        assert.deepEqual(await quota.findAnalyticsFingerprintKey('token', id), { status: 'not_found' });
        assert.deepEqual(await quota.reserveAnalytics('token', reservation), { status: 'reserved', reservationId: id });
        assert.equal(calls[0].options.rejectUnauthorized, true);
        assert.equal(calls[0].options.cert, 'cert');
        assert.equal(calls[1].url, 'https://control.example/cloud/internal/v1/analytics/quota/reservations');
        assert.deepEqual(JSON.parse(calls[1].body), reservation);
        assert.ok(calls[1].owned.every(byte => byte === 0));
    } finally { https.request = original; }
});

test('rejects malformed and untrusted inputs and fails closed on malformed responses', async () => {
    assert.throws(() => createAnalyticsQuotaClient({ origin: 'http://control.example', ca: 'ca', certificate: 'cert', privateKey: 'key' }));
    const original = https.request; let calls = 0;
    https.request = (_url, _options, callback) => {
        calls++; const req = new EventEmitter(); req.destroy = () => {};
        req.end = () => {
            const response = new EventEmitter(); response.headers = {}; response.statusCode = 200; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from('{"status":"reserved","status":"duplicate"}')); response.emit('end');
        };
        return req;
    };
    try {
        const quota = client();
        assert.equal((await quota.reserveAnalytics('bad token', reservation)).status, 'quota_unavailable');
        assert.equal(calls, 0);
        assert.equal((await quota.reserveAnalytics('token', reservation)).status, 'quota_unavailable');
        assert.equal(calls, 1);
    } finally { https.request = original; }
});

test('maps quota decisions and rejects oversized or encoded responses', async () => {
    const original = https.request; let reply;
    https.request = (_url, _options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = () => {
            const response = new EventEmitter(); response.headers = reply.headers ?? {}; response.statusCode = reply.status;
            response.destroy = () => {}; callback(response);
            if (reply.body) response.emit('data', Buffer.from(reply.body));
            response.emit('end');
        };
        return req;
    };
    try {
        const quota = client();
        for (const [status, body, expected] of [
            [429, '{"status":"quota_exceeded","reservationId":null}', 'quota_exceeded'],
            [409, '{"status":"idempotency_conflict","reservationId":null}', 'idempotency_conflict'],
            [200, '{"status":"reserved","reservationId":"invalid"}', 'quota_unavailable'],
            [500, '{"status":"reserved","reservationId":"' + id + '"}', 'quota_unavailable'],
            [200, ' '.repeat(4097), 'quota_unavailable'],
        ]) {
            reply = { status, body };
            assert.equal((await quota.reserveAnalytics('token', reservation)).status, expected);
        }
        reply = { status: 200, headers: { 'content-encoding': 'gzip' }, body: '{}' };
        assert.equal((await quota.findAnalyticsFingerprintKey('token', id)).status, 'unavailable');
        const abort = new AbortController(); abort.abort();
        assert.equal((await quota.reserveAnalytics('token', reservation, abort.signal)).status, 'quota_unavailable');
    } finally { https.request = original; }
});

test('admission carries a tenant-bound fingerprint through the control-plane transport', async () => {
    const original = https.request; const received = [];
    https.request = (url, options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = body => {
            received.push({ url: url.pathname, authorization: options.headers.authorization, body: body && JSON.parse(body) });
            const response = new EventEmitter(); response.headers = {}; response.statusCode = 200; response.destroy = () => {};
            callback(response);
            response.emit('data', Buffer.from(JSON.stringify(options.method === 'GET'
                ? { status: 'not_found', keyId: null, expiresAt: null }
                : { status: 'reserved', reservationId: id })));
            response.emit('end');
        };
        return req;
    };
    const keyring = createFingerprintKeyring({ activeKeyId: 'active', keys: [{ id: 'active', key: Buffer.alloc(32, 3) }], maximumBodyBytes: 262144 });
    try {
        const body = readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url));
        const identity = { tenantId: id, applicationId: '22222222-2222-2222-2222-222222222222' };
        const result = await reserveAnalyticsRequest({ credential: 'token', identity, body },
            { controlPlane: client(), keyring, clock: () => Date.parse('2030-01-01T00:00:00.000Z') });
        assert.equal(result.status, 'reserved');
        assert.equal(received.length, 2);
        assert.equal(received[0].authorization, 'Bearer token');
        assert.equal(received[1].body.fingerprint, result.fingerprint);
        assert.equal(received[1].body.fingerprintKeyId, 'active');
        assert.deepEqual(Object.keys(received[1].body).sort(), ['expiresAt', 'fingerprint', 'fingerprintKeyId', 'requestId']);
    } finally { keyring.dispose(); https.request = original; }
});

test('privacy inspection sends the exact bounded request and fingerprint binding', async () => {
    const original = https.request; let captured;
    https.request = (url, options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            captured = { path: url.pathname, headers: options.headers, body: Buffer.from(body), owned: body };
            written?.();
            const response = new EventEmitter(); response.headers = {}; response.statusCode = 200; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from('{"allowed":true,"code":"allowed"}')); response.emit('end');
        };
        return req;
    };
    try {
        const body = readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url));
        const requestId = JSON.parse(body).requestId;
        const fingerprint = Buffer.alloc(32, 7).toString('base64');
        assert.deepEqual(await client().inspectAnalytics('token', { requestId, fingerprintKeyId: 'active', fingerprint, body }), { status: 'allowed' });
        assert.equal(captured.path, '/cloud/internal/v1/analytics/privacy');
        assert.equal(captured.headers['x-cloud-request-fingerprint'], fingerprint);
        assert.deepEqual(captured.body, body);
        assert.ok(captured.owned.every(byte => byte === 0));
        assert.deepEqual(await client().inspectAnalytics('token', { requestId: '22222222-2222-2222-2222-222222222222', fingerprintKeyId: 'active', fingerprint, body }), { status: 'unavailable' });
    } finally { https.request = original; }
});

test('claim submission binds both fingerprints and a deployment', async () => {
    const original = https.request; let captured; let status = 200;
    https.request = (url, options, callback) => {
        const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            captured = { path: url.pathname, body: JSON.parse(body) }; written?.();
            const response = new EventEmitter(); response.headers = {}; response.statusCode = status; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(JSON.stringify(status === 200
                ? { status: 'claimed', claimId: id } : { status: 'already_claimed', claimId: null }))); response.emit('end');
        };
        return req;
    };
    try {
        const fingerprint = Buffer.alloc(32, 4).toString('base64');
        const input = { fingerprintKeyId: 'active', fingerprint, workerPayloadFingerprint: fingerprint,
            deployment: { endpointReference: 'analytics', region: 'region-a', contractVersion: 'openconnect.v1',
                modelRevision: 'rev-1', modelDigest: 'sha256:' + 'a'.repeat(64) } };
        assert.deepEqual(await client().claimAnalytics('token', id, input), { status: 'claimed', claimId: id });
        assert.equal(captured.path, `/cloud/internal/v1/analytics/quota/reservations/${id}/claim`);
        assert.equal(captured.body.workerPayloadFingerprint, fingerprint);
        assert.equal(captured.body.endpointReference, 'analytics');
        status = 409;
        assert.equal((await client().claimAnalytics('token', id, input)).status, 'already_claimed');
        input.workerPayloadFingerprint = 'bad';
        assert.equal((await client().claimAnalytics('token', id, input)).status, 'unavailable');
    } finally { https.request = original; }
});

test('response privacy uses a separate endpoint and rejects mismatched task ids', async () => {
    const original = https.request; let path;
    https.request = (url, _options, callback) => {
        path = url.pathname; const req = new EventEmitter(); req.destroy = () => {};
        req.end = (_body, written) => {
            written(); const response = new EventEmitter(); response.headers = {}; response.statusCode = 403; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from('{"allowed":false,"code":"privacy_blocked"}')); response.emit('end');
        };
        return req;
    };
    try {
        const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: id, kind: 'analytics.analyze', result: {} }));
        const input = { requestId: id, fingerprintKeyId: 'active', fingerprint: Buffer.alloc(32).toString('base64'), body };
        assert.equal((await client().inspectAnalyticsResponse('token', input)).status, 'privacy_blocked');
        assert.equal(path, '/cloud/internal/v1/analytics/privacy/response');
        input.requestId = '22222222-2222-2222-2222-222222222222';
        assert.equal((await client().inspectAnalyticsResponse('token', input)).status, 'unavailable');
    } finally { https.request = original; }
});

test('completion submits bounded compute usage and accepts only a confirmed terminal status', async () => {
    const original = https.request; let path; let sent;
    https.request = (url, _options, callback) => {
        path = url.pathname; const req = new EventEmitter(); req.destroy = () => {};
        req.end = (body, written) => {
            sent = JSON.parse(body); written(); const response = new EventEmitter(); response.headers = {}; response.statusCode = 200; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from('{"status":"completed"}')); response.emit('end');
        };
        return req;
    };
    try {
        assert.deepEqual(await client().completeAnalytics('token', id, id, 14), { status: 'completed' });
        assert.equal(path, `/cloud/internal/v1/analytics/quota/reservations/${id}/claims/${id}/completion`);
        assert.deepEqual(sent, { computeMilliseconds: 14 });
        assert.equal((await client().completeAnalytics('token', id, 'invalid', 14)).status, 'unavailable');
        assert.equal((await client().completeAnalytics('token', id, id, 120001)).status, 'unavailable');
    } finally { https.request = original; }
});
