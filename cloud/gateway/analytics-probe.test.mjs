import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { createAnalyticsProbe } from './analytics-probe.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const configuration = { origin: 'https://worker.example', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token', deploymentId: id };

test('requires the configured worker deployment identity and trusted TLS', async () => {
    const original = https.request; let resultId = id; let options;
    https.request = (_url, requestOptions, callback) => {
        options = requestOptions; const req = new EventEmitter(); req.destroy = () => {};
        req.end = () => {
            const response = new EventEmitter(); response.statusCode = 200; response.headers = {}; response.destroy = () => {};
            callback(response); response.emit('data', Buffer.from(JSON.stringify({ ready: true, deploymentId: resultId }))); response.emit('end');
        };
        return req;
    };
    try {
        const probe = createAnalyticsProbe(configuration);
        assert.equal(await probe.probe(), true);
        assert.equal(options.rejectUnauthorized, true);
        resultId = '22222222-2222-2222-2222-222222222222';
        assert.equal(await probe.probe(), false);
        assert.throws(() => createAnalyticsProbe({ ...configuration, origin: 'http://worker.example' }));
    } finally { https.request = original; }
});
