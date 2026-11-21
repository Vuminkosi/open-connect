import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createVoiceAudioTranscriber } from './voice-audio-transcriber.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const fingerprint = 'a'.repeat(64);

test('transcriber requires mutual TLS, pinned model identity and matching audio digest', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl unavailable'); return; }
    const directory = mkdtempSync(join(tmpdir(), 'oc-audio-screen-tls-')); let server; let wrongDigest = false; let wrongModel = false;
    try {
        const certPath = join(directory, 'cert.pem'); const keyPath = join(directory, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        const identity = () => ({ modelId: 'whisper', modelRevision: 'v1', modelFingerprint: wrongModel ? 'b'.repeat(64) : fingerprint });
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer token');
            res.setHeader('content-type', 'application/json');
            if (req.url.endsWith('/ready')) return res.end(JSON.stringify({ ready: true, ...identity(), maxAudioBytes: 5760044, maxDurationMs: 120000, sampleRatesHz: [24000] }));
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                const body = JSON.parse(Buffer.concat(chunks)); const audio = Buffer.from(body.input.audioBase64, 'base64');
                assert.equal(body.input.sampleRateHz, 24000); assert.equal(body.input.durationMs, 100);
                res.end(JSON.stringify({ schemaVersion: 'openconnect.audio-inspection.transcript/v1', requestId: body.requestId,
                    ...identity(), audioSha256: wrongDigest ? '0'.repeat(64) : createHash('sha256').update(audio).digest('hex'),
                    transcript: 'hello', locale: body.input.locale, durationMs: 100 }));
            });
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const options = { origin: `https://localhost:${server.address().port}`, ca: certificate, certificate, privateKey,
            workerToken: 'token', modelId: 'whisper', modelRevision: 'v1', modelFingerprint: fingerprint };
        assert.throws(() => createVoiceAudioTranscriber({ ...options, origin: 'http://localhost' }));
        const client = createVoiceAudioTranscriber(options);
        const request = { requestId: id, audio: Buffer.alloc(4800), format: 'pcm', locale: 'en-US' };
        assert.equal(await client.isReady(), true);
        assert.deepEqual(await client.transcribe(request), { transcript: 'hello' });
        wrongDigest = true; assert.equal(await client.transcribe(request), null);
        wrongModel = true; assert.equal(await client.isReady(), false);
        assert.equal(await client.transcribe(request), null);
        assert.equal(await client.transcribe(request, AbortSignal.abort()), null);
    } finally { if (server) await new Promise(resolve => server.close(resolve)); rmSync(directory, { recursive: true, force: true }); }
});
