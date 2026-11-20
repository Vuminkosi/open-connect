import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { readFileSync } from 'node:fs';
import { validateClassificationRequest } from './classification-request.mjs';
import { createLocalServer } from './local-server.mjs';

const envelope = () => ({ schemaVersion: 'openconnect.cloud.classification/v2', requestId: '11111111-1111-1111-1111-111111111111',
    capability: 'classification.classify', expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'hello' } });

function request(text, headers = { 'content-type': 'application/json' }) {
    let buffer;
    return { headers: new Headers(headers), readBody: async () => { buffer = Buffer.from(text); return { bytes: buffer }; },
        get buffer() { return buffer; } };
}

test('shared contract fixtures match gateway validation', async () => {
    const valid = readFileSync(new URL('../../contracts/fixtures/valid/cloud-classification-request.json', import.meta.url), 'utf8');
    const invalid = readFileSync(new URL('../../contracts/fixtures/invalid/cloud-classification-request.json', import.meta.url), 'utf8');
    const now = () => Date.parse('2030-01-01T00:00:00.000Z');
    assert.equal(await validateClassificationRequest(request(valid), now), null);
    assert.equal(await validateClassificationRequest(request(invalid), now), 'invalid_semantic_request');
    const v2 = readFileSync(new URL('../../contracts/fixtures/valid/cloud-classification-request-v2.json', import.meta.url), 'utf8');
    const invalidV2 = readFileSync(new URL('../../contracts/fixtures/invalid/cloud-classification-request-v2.json', import.meta.url), 'utf8');
    assert.equal(await validateClassificationRequest(request(v2), now), null);
    assert.equal(await validateClassificationRequest(request(invalidV2), now), 'invalid_semantic_request');
});

test('classification requires a canonical deadline within five minutes', async () => {
    const base = Date.parse('2030-01-01T00:00:00.000Z');
    const now = () => base;
    for (const [expiry, expected] of [
        ['2030-01-01T00:00:00.001Z', null], ['2030-01-01T00:05:00.000Z', null],
        ['2030-01-01T00:00:00.000Z', 'invalid_semantic_request'], ['2030-01-01T00:05:00.001Z', 'invalid_semantic_request'],
        ['2030-02-30T00:01:00.000Z', 'invalid_semantic_request'], ['2030-01-01T00:01:00Z', 'invalid_semantic_request'],
        ['2030-01-01T00:01:00.000+00:00', 'invalid_semantic_request'], [null, 'invalid_semantic_request'],
    ]) {
        assert.equal(await validateClassificationRequest(request(JSON.stringify({ ...envelope(), expiresAt: expiry })), now), expected);
    }
    const missing = envelope(); delete missing.expiresAt;
    assert.equal(await validateClassificationRequest(request(JSON.stringify(missing)), now), 'invalid_semantic_request');
});

test('classification envelope enforces worker-derived input and bounds', async () => {
    const valid = envelope();
    valid.configuration = { labelSetVersion: 'v1', localeHint: 'en', threshold: 0.5, maxLabels: 3 };
    const good = request(JSON.stringify(valid));
    assert.equal(await validateClassificationRequest(good), null);
    assert.ok(good.buffer.every(value => value === 0));
    const handle = envelope(); handle.input = { inputRef: 'opaque-handle_123' };
    assert.equal(await validateClassificationRequest(request(JSON.stringify(handle))), null);
    for (const change of [
        x => { x.tenantId = 'other'; }, x => { x.input = {}; }, x => { x.input.inputRef = 'also'; },
        x => { x.input.text = ''; }, x => { x.input.text = 'x'.repeat(20001); },
        x => { x.input = { inputRef: 'https://evil.example' }; },
        x => { x.configuration = { threshold: 1.1 }; }, x => { x.configuration = { maxLabels: 0 }; },
        x => { x.configuration = { maxLabels: 1.5 }; }, x => { x.schemaVersion = 'other'; },
        x => { x.requestId = '00000000-0000-0000-0000-000000000000'; },
    ]) {
        const value = envelope(); change(value);
        assert.equal(await validateClassificationRequest(request(JSON.stringify(value))), 'invalid_semantic_request');
    }
});

test('classification rejects duplicate keys, invalid JSON, media and encodings', async () => {
    const json = JSON.stringify(envelope());
    assert.equal(await validateClassificationRequest(request(json.replace('"input":', '"input":{},"input":'))), 'invalid_request');
    assert.equal(await validateClassificationRequest(request('{')), 'invalid_request');
    assert.equal(await validateClassificationRequest(request(json, { 'content-type': 'text/plain' })), 'unsupported_media_type');
    assert.equal(await validateClassificationRequest(request(json, { 'content-type': 'application/json', 'content-encoding': 'gzip' })), 'unsupported_media_type');
    assert.equal(await validateClassificationRequest(request(json, { 'content-type': 'application/json', 'content-length': '65537' })), 'payload_too_large');
    assert.equal(await validateClassificationRequest({ headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes: Buffer.alloc(65537) }) }), 'payload_too_large');
    assert.equal(await validateClassificationRequest({ headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes: 'not bytes' }) }), 'invalid_request');
});

test('local gateway bounds chunked bodies after authorization and readiness', async () => {
    const server = createLocalServer({ authorize: async () => ({ authorized: true }), matchClassificationDeployment: async () => true }, { probe: async () => ({ ready: true }) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    async function send(chunks) {
        return new Promise((resolve, reject) => {
            const req = http.request({ host: '127.0.0.1', port: server.address().port, path: '/cloud/v1/classification', method: 'POST',
                headers: { authorization: 'Bearer key', 'content-type': 'application/json', 'transfer-encoding': 'chunked' } }, res => {
                const data = []; res.on('data', chunk => data.push(chunk)); res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(data).toString() }));
            });
            req.on('error', reject); for (const chunk of chunks) req.write(chunk); req.end();
        });
    }
    try {
        const valid = await send([JSON.stringify(envelope())]);
        assert.equal(valid.status, 503);
        assert.doesNotMatch(valid.body, /hello/);
        const malformed = await send(['{']);
        assert.equal(malformed.status, 400);
        const tooLarge = await send([Buffer.alloc(40000, 120), Buffer.alloc(30000, 120)]);
        assert.equal(tooLarge.status, 413);
    } finally { await new Promise(resolve => server.close(resolve)); }
});
