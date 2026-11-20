import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInferenceDispatcher, validateInferenceWorkerResponse } from './inference-dispatch.mjs';
import { createInferenceProbe } from './inference-probe.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const response = () => ({ schemaVersion: 'openconnect.cloud.inference.result/v1', requestId: id, modelId: 'model',
    output: 'fixture output', usage: { inputTokens: 10, outputTokens: 3 } });

test('worker output and accounting are bounded before egress inspection', () => {
    assert.deepEqual(validateInferenceWorkerResponse(response(), id, 'model', 20, 5),
        { output: 'fixture output', usage: { inputTokens: 10, outputTokens: 3 } });
    for (const mutate of [
        x => { x.requestId = 'other'; }, x => { x.modelId = 'other'; }, x => { x.extra = true; },
        x => { x.usage.extra = true; }, x => { x.usage.inputTokens = -1; }, x => { x.usage.outputTokens = 6; },
        x => { x.usage.inputTokens = 19; }, x => { x.usage.outputTokens = 1.5; },
        x => { x.output = 'x'.repeat(131073); },
    ]) { const value = response(); mutate(value); assert.equal(validateInferenceWorkerResponse(value, id, 'model', 20, 5), null); }
});

test('dispatcher rejects untrusted destinations, unclaimed payloads, streaming and invalid TLS', async () => {
    const config = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad', workerToken: 'secret' };
    for (const origin of ['http://localhost', 'https://user:pass@localhost', 'https://localhost/path', 'https://localhost/?target=x'])
        assert.throws(() => createInferenceDispatcher({ ...config, origin }));
    const dispatcher = createInferenceDispatcher(config);
    const payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.inference.worker/v1', requestId: id,
        modelId: 'model', input: { messages: [{ role: 'user', content: 'fixture' }], maxTokens: 5, stream: false } }));
    const claim = { payload, key: Buffer.alloc(32), reservationId: id, claimId: id, requestId: id,
        modelId: 'model', reservedTokens: 20, maxOutputTokens: 5 };
    const controller = new AbortController(); controller.abort();
    for (const [value, signal] of [
        [claim, controller.signal], [{ ...claim, key: Buffer.alloc(31) }], [{ ...claim, claimId: 'invalid' }],
        [{ ...claim, modelId: 'other' }], [{ ...claim, maxOutputTokens: 6 }], [claim],
    ]) assert.deepEqual(await dispatcher.executeClaimed(value, signal), { status: 'upstream_unavailable' });
    payload.fill(0); claim.key.fill(0);
});

test('dispatcher sends the claimed payload over mutually authenticated TLS', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl is unavailable'); return; }
    const dir = mkdtempSync(join(tmpdir(), 'oc-inference-tls-'));
    let server;
    try {
        const certPath = join(dir, 'cert.pem'); const keyPath = join(dir, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        let received;
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            if (req.url === '/internal/cloud/v1/inference/ready') {
                if (!req.client.authorized || req.headers.authorization !== 'Bearer fixture-token') { res.writeHead(401); res.end(); return; }
                res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify({ status: 'ready', deploymentId: id })); return;
            }
            const chunks = [];
            req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                received = { authorized: req.client.authorized, path: req.url, authorization: req.headers.authorization,
                    body: JSON.parse(Buffer.concat(chunks).toString('utf8')) };
                res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(response()));
            });
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const dispatcher = createInferenceDispatcher({ origin: `https://localhost:${server.address().port}`, ca: certificate,
            certificate, privateKey, workerToken: 'fixture-token' });
        const payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.inference.worker/v1', requestId: id,
            modelId: 'model', input: { messages: [{ role: 'user', content: 'fixture' }], maxTokens: 5, stream: false } }));
        const result = await dispatcher.executeClaimed({ payload, key: Buffer.alloc(32), reservationId: id, claimId: id,
            requestId: id, modelId: 'model', reservedTokens: 20, maxOutputTokens: 5 });
        assert.equal(result.status, 'completed');
        assert.equal(received.authorized, true);
        assert.equal(received.path, '/internal/cloud/v1/inference/execute');
        assert.equal(received.authorization, 'Bearer fixture-token');
        assert.equal(Buffer.from(received.body.payload, 'base64').toString(), payload.toString());
        assert.equal(received.body.claimId, id);
        const probe = createInferenceProbe({ origin: `https://localhost:${server.address().port}`, ca: certificate,
            certificate, privateKey, workerToken: 'fixture-token', deploymentId: id });
        assert.equal(await probe.probe(), true);
        const mismatched = createInferenceProbe({ origin: `https://localhost:${server.address().port}`, ca: certificate,
            certificate, privateKey, workerToken: 'fixture-token', deploymentId: '22222222-2222-2222-2222-222222222222' });
        assert.equal(await mismatched.probe(), false);
    } finally {
        if (server) await new Promise(resolve => server.close(resolve));
        rmSync(dir, { recursive: true, force: true });
    }
});
