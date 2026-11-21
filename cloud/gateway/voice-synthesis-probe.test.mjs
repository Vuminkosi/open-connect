import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createVoiceSynthesisProbe } from './voice-synthesis-probe.mjs';

test('voice readiness requires mutual TLS and the expected deployment', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl unavailable'); return; }
    const directory = mkdtempSync(join(tmpdir(), 'oc-voice-probe-')); let server; let mismatch = false;
    const id = '11111111-1111-1111-1111-111111111111';
    try {
        const certPath = join(directory, 'cert.pem'); const keyPath = join(directory, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer token');
            assert.equal(req.url, '/internal/cloud/v1/voice-synthesis/ready');
            res.end(JSON.stringify({ status: 'ready', deploymentId: mismatch ? '22222222-2222-2222-2222-222222222222' : id }));
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const probe = createVoiceSynthesisProbe({ origin: `https://localhost:${server.address().port}`, ca: certificate,
            certificate, privateKey, workerToken: 'token', deploymentId: id });
        assert.equal(await probe.probe(), true);
        mismatch = true; assert.equal(await probe.probe(), false);
        assert.equal(await probe.probe(AbortSignal.abort()), false);
    } finally { if (server) await new Promise(resolve => server.close(resolve)); rmSync(directory, { recursive: true, force: true }); }
});
