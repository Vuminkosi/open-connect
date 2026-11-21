import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createLiveUIDispatcher } from './liveui-dispatch.mjs';
import { createLiveUIProbe } from './liveui-probe.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const input = { schemaVersion: 'v1', intent: 'Show tasks', context: { route: 'route.root',
    availableActions: [{ target: 'tasks.tab', action: 'activate' }] }, generation: { maxActions: 2, maxOutputTokens: 64 } };
const request = { requestId: id, input };
const payload = () => Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.liveui.worker/v1', requestId: id, modelId: 'model', input }));
const claim = () => ({ payload: payload(), key: Buffer.alloc(32), reservationId: id, claimId: id, request, modelId: 'model' });

test('LiveUI dispatcher rejects unsafe configuration, invalid claims and TLS failure', async () => {
    const options = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad', workerToken: 'token' };
    assert.throws(() => createLiveUIDispatcher({ ...options, origin: 'http://localhost' }));
    const dispatcher = createLiveUIDispatcher(options);
    assert.deepEqual(await dispatcher.executeClaimed({ ...claim(), key: Buffer.alloc(31) }), { status: 'upstream_unavailable' });
    assert.deepEqual(await dispatcher.executeClaimed({ ...claim(), modelId: 'other' }), { status: 'upstream_unavailable' });
    assert.deepEqual(await dispatcher.executeClaimed(claim()), { status: 'upstream_unavailable' });
});

test('LiveUI worker probe and claimed dispatch use mutual TLS and reject unlisted actions', async t => {
    if (spawnSync('openssl', ['version']).status !== 0) { t.skip('openssl is unavailable'); return; }
    const directory = mkdtempSync(join(tmpdir(), 'oc-liveui-tls-')); let server; let forbidden = false;
    try {
        const certPath = join(directory, 'cert.pem'); const keyPath = join(directory, 'key.pem');
        execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost',
            '-addext', 'subjectAltName=DNS:localhost', '-keyout', keyPath, '-out', certPath], { stdio: 'ignore' });
        const certificate = readFileSync(certPath); const privateKey = readFileSync(keyPath);
        server = https.createServer({ cert: certificate, key: privateKey, ca: certificate, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer fixture-token');
            if (req.url.endsWith('/ready')) { res.end(JSON.stringify({ status: 'ready', deploymentId: id })); return; }
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                const body = JSON.parse(Buffer.concat(chunks).toString());
                assert.equal(body.claimId, id); assert.deepEqual(Buffer.from(body.payload, 'base64'), payload());
                res.end(JSON.stringify({ requestId: id, schemaVersion: 'v1', provider: 'deterministic', status: 'completed',
                    plan: { schemaVersion: 'v1', actions: [{ type: 'activate', target: forbidden ? 'other' : 'tasks.tab' }] } }));
            });
        });
        await new Promise(resolve => server.listen(0, 'localhost', resolve));
        const options = { origin: `https://localhost:${server.address().port}`, ca: certificate, certificate, privateKey, workerToken: 'fixture-token' };
        assert.equal(await createLiveUIProbe({ ...options, deploymentId: id }).probe(), true);
        const dispatcher = createLiveUIDispatcher(options);
        assert.equal((await dispatcher.executeClaimed(claim())).status, 'completed');
        forbidden = true;
        assert.deepEqual(await dispatcher.executeClaimed(claim()), { status: 'upstream_unavailable' });
    } finally { if (server) await new Promise(resolve => server.close(resolve)); rmSync(directory, { recursive: true, force: true }); }
});
