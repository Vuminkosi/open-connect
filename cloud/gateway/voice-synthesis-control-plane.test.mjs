import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createVoiceSynthesisControlPlaneClient } from './voice-synthesis-control-plane.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const hash = Buffer.alloc(32).toString('base64');
const request = { requestId: id, input: { text: 'hello 🌍', voiceId: 'af_heart', locale: 'en-US', format: 'wav' } };
const deployment = { endpointReference: 'voice', region: 'region-a', contractVersion: 'v1', modelRevision: 'rev-1', modelDigest: 'sha256:' + 'a'.repeat(64) };
const proof = { fingerprintKeyId: 'key', fingerprint: hash, workerPayloadFingerprint: hash, modelId: 'kokoro', deployment };

test('voice control-plane client rejects invalid configuration and inputs before network access', async () => {
    const config = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad' };
    assert.throws(() => createVoiceSynthesisControlPlaneClient({ ...config, origin: 'http://localhost' }));
    const client = createVoiceSynthesisControlPlaneClient(config);
    assert.deepEqual(await client.reserve('credential', { requestId: id, fingerprintKeyId: 'key', fingerprint: hash, characters: 2001, expiresAt: '2030-01-01' }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.claim('credential', id, { ...proof, workerPayloadFingerprint: 'bad' }), { status: 'unavailable' });
    assert.deepEqual(await client.inspect('credential', { ...request, input: { ...request.input, text: '\ud800' } }, 'key', hash), { allowed: false, code: 'capability_unavailable' });
});

test('voice control-plane client uses mutual TLS and validates inspection, quota and claim results', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl unavailable'); return; }
    const directory = mkdtempSync(join(tmpdir(), 'oc-voice-control-')); let server; let badCount = false; let calls = 0;
    try {
        const certPath = join(directory, 'cert.pem'); const keyPath = join(directory, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            calls++; assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer fixture-credential');
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                res.setHeader('content-type', 'application/json');
                if (req.method === 'GET') return res.end(JSON.stringify({ status: 'not_found', keyId: null, expiresAt: null }));
                const value = JSON.parse(Buffer.concat(chunks).toString());
                if (req.url.endsWith('/privacy')) {
                    assert.equal(value.text, request.input.text); assert.equal(req.headers['x-cloud-request-fingerprint'], hash);
                    return res.end(JSON.stringify({ allowed: true, code: 'allowed', characters: badCount ? 8 : 7 }));
                }
                if (req.url.endsWith('/reservations')) {
                    assert.equal(value.characters, 7); assert.equal(value.fingerprint, hash);
                    return res.end(JSON.stringify({ status: 'reserved', reservationId: id }));
                }
                assert.ok(req.url.endsWith(`/${id}/claim`)); assert.equal(value.workerPayloadFingerprint, hash);
                return res.end(JSON.stringify({ status: 'claimed', claimId: id }));
            });
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const client = createVoiceSynthesisControlPlaneClient({ origin: `https://localhost:${server.address().port}`,
            ca: certificate, certificate, privateKey });
        assert.deepEqual(await client.findFingerprintKey('fixture-credential', id), { status: 'not_found' });
        assert.deepEqual(await client.inspect('fixture-credential', request, 'key', hash), { allowed: true, code: 'allowed', characters: 7 });
        const reservation = { requestId: id, fingerprintKeyId: 'key', fingerprint: hash, characters: 7, expiresAt: '2030-01-01T00:01:00.000Z' };
        assert.equal((await client.reserve('fixture-credential', reservation)).status, 'reserved');
        assert.equal((await client.claim('fixture-credential', id, proof)).status, 'claimed');
        badCount = true;
        assert.deepEqual(await client.inspect('fixture-credential', request, 'key', hash), { allowed: false, code: 'capability_unavailable' });
        assert.equal(calls, 5);
    } finally { if (server) await new Promise(resolve => server.close(resolve)); rmSync(directory, { recursive: true, force: true }); }
});
