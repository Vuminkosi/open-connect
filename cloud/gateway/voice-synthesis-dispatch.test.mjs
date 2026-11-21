import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createVoiceSynthesisDispatcher } from './voice-synthesis-dispatch.mjs';
import { prepareVoiceSynthesisWorkerPayload } from './voice-synthesis-worker-contract.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const request = () => ({ schemaVersion: 'openconnect.cloud.voice-synthesis/v1', requestId: id, capability: 'voice.synthesize',
    expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'hello', voiceId: 'af_heart', locale: 'en-US', format: 'pcm' } });
const claim = () => { const value = request(); return { request: value, payload: prepareVoiceSynthesisWorkerPayload(value).payload,
    key: Buffer.alloc(32, 1), reservationId: id, claimId: id }; };

test('speech dispatcher rejects unsafe configuration and mismatched payloads', async () => {
    const config = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad', workerToken: 'token' };
    assert.throws(() => createVoiceSynthesisDispatcher({ ...config, origin: 'http://localhost' }));
    const dispatcher = createVoiceSynthesisDispatcher(config);
    assert.deepEqual(await dispatcher.executeClaimed(null), { status: 'upstream_unavailable' });
    const value = claim();
    try {
        assert.deepEqual(await dispatcher.executeClaimed({ ...value, key: Buffer.alloc(31) }), { status: 'upstream_unavailable' });
        const other = { ...value.request, input: { ...value.request.input, text: 'changed' } };
        assert.deepEqual(await dispatcher.executeClaimed({ ...value, request: other }), { status: 'upstream_unavailable' });
        assert.deepEqual(await dispatcher.executeClaimed(value), { status: 'upstream_unavailable' });
    } finally { value.payload.fill(0); value.key.fill(0); }
});

test('speech dispatch uses mutual TLS, validates worker audio and does not retry failures', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl unavailable'); return; }
    const directory = mkdtempSync(join(tmpdir(), 'oc-voice-tls-')); let server; let calls = 0; let invalid = false;
    try {
        const certPath = join(directory, 'cert.pem'); const keyPath = join(directory, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            calls++; assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer fixture-token');
            assert.equal(req.url, '/internal/cloud/v1/voice-synthesis/execute');
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                const envelope = JSON.parse(Buffer.concat(chunks).toString());
                const payload = JSON.parse(Buffer.from(envelope.payload, 'base64').toString());
                assert.equal(envelope.schemaVersion, 'openconnect.cloud.worker.voice-synthesis/v1');
                assert.equal(envelope.reservationId, id); assert.equal(envelope.claimId, id);
                assert.equal(Buffer.from(envelope.key, 'base64').length, 32);
                res.setHeader('content-type', 'application/json');
                res.end(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: payload.taskId, kind: 'speech.synthesize', idempotentReplay: false,
                    result: { text: invalid ? 'changed' : payload.payload.text, voiceId: 'af_heart', locale: 'en-US', format: 'pcm',
                        sampleRateHz: 24000, channels: 1, audioBase64: 'AAA=' } }));
            });
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const dispatcher = createVoiceSynthesisDispatcher({ origin: `https://localhost:${server.address().port}`, ca: certificate,
            certificate, privateKey, workerToken: 'fixture-token' });
        const value = claim();
        try {
            const completed = await dispatcher.executeClaimed(value);
            assert.equal(completed.status, 'completed'); assert.deepEqual(completed.result.audio, Buffer.from([0, 0]));
            assert.equal(Object.hasOwn(completed.result, 'text'), false); completed.result.audio.fill(0);
            invalid = true;
            assert.deepEqual(await dispatcher.executeClaimed(value), { status: 'upstream_unavailable' });
            assert.equal(calls, 2);
            const cancelled = AbortSignal.abort();
            assert.deepEqual(await dispatcher.executeClaimed(value, cancelled), { status: 'upstream_unavailable' });
            assert.equal(calls, 2);
        } finally { value.payload.fill(0); value.key.fill(0); }
    } finally { if (server) await new Promise(resolve => server.close(resolve)); rmSync(directory, { recursive: true, force: true }); }
});
