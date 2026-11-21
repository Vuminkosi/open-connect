import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createTranslationControlPlaneClient } from './translation-control-plane.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const hash = Buffer.alloc(32).toString('base64');
const request = () => ({ schemaVersion: 'openconnect.cloud.translation/v1', requestId: id, capability: 'translation.translate',
    expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'hello', sourceLang: 'eng_Latn', targetLang: 'fra_Latn' } });
const reservation = () => ({ requestId: id, fingerprintKeyId: 'key', fingerprint: hash, characters: 5, expiresAt: new Date(Date.now() + 60000).toISOString() });
const deployment = () => ({ deploymentId: id, endpointReference: 'nllb', region: 'local', contractVersion: 'v1', modelId: 'nllb', modelRevision: 'r1',
    modelDigest: 'sha256:' + 'a'.repeat(64), imageDigest: 'sha256:' + 'b'.repeat(64), tokenizerHash: 'c'.repeat(64) });
const proof = () => ({ fingerprintKeyId: 'key', fingerprint: hash, workerPayloadFingerprint: hash, deployment: deployment(), modelId: 'nllb', input: request().input });

test('translation control client fails closed on invalid settings and inputs', async () => {
    const config = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad' };
    assert.throws(() => createTranslationControlPlaneClient({ ...config, origin: 'http://localhost' }));
    const client = createTranslationControlPlaneClient(config);
    assert.deepEqual(await client.findFingerprintKey('credential', 'bad'), { status: 'unavailable' });
    assert.equal(await client.matchDeployment('credential', { ...deployment(), tokenizerHash: 'bad' }, request().input), false);
    assert.deepEqual(await client.claim('credential', id, { ...proof(), modelId: 'wrong' }), { status: 'unavailable' });
    assert.deepEqual(await client.reserve('credential', { ...reservation(), characters: 5001 }), { status: 'quota_unavailable' });
    assert.deepEqual(await client.inspect('credential', { ...request(), input: { ...request().input, text: '\ud800' } }, 'key', hash),
        { allowed: false, code: 'capability_unavailable' });
});

test('translation control client validates privacy and quota responses over mutual TLS',
    { skip: !process.env.OC_NLLB_TEST_TLS_DIR }, async () => {
        const root = process.env.OC_NLLB_TEST_TLS_DIR;
        const serverCert = readFileSync(join(root, 'authority', 'authority.crt'));
        const serverKey = readFileSync(join(root, 'authority', 'authority.key'));
        const clientCert = readFileSync(join(root, 'gateway', 'gateway.crt'));
        const clientKey = readFileSync(join(root, 'gateway', 'gateway.key'));
        let calls = 0; let blocked = false; let badCount = false;
        const server = https.createServer({ cert: serverCert, key: serverKey, ca: clientCert, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            calls++; assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer fixture-credential');
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                res.setHeader('content-type', 'application/json');
                if (req.method === 'GET') {
                    assert.equal(req.url, `/cloud/internal/v1/translation/quota/fingerprint-key/${id}`);
                    return res.end(JSON.stringify({ status: 'not_found', keyId: null, expiresAt: null }));
                }
                const value = JSON.parse(Buffer.concat(chunks));
                if (req.url.endsWith('/deployment-match')) {
                    assert.equal(value.deploymentId, id); assert.equal(value.sourceLang, 'eng_Latn'); assert.equal(value.targetLang, 'fra_Latn');
                    res.end(JSON.stringify({ matched: true }));
                } else if (req.url.endsWith('/claim')) {
                    assert.equal(value.deployment.sourceLang, 'eng_Latn'); assert.equal(value.deployment.targetLang, 'fra_Latn');
                    assert.equal(value.workerPayloadFingerprint, hash);
                    res.end(JSON.stringify({ status: 'claimed', claimId: id }));
                } else if (req.url.endsWith('/privacy')) {
                    assert.deepEqual(value, request().input); assert.equal(req.headers['x-cloud-request-fingerprint'], hash);
                    if (blocked) res.statusCode = 403;
                    res.end(JSON.stringify({ allowed: !blocked, code: blocked ? 'privacy_blocked' : 'allowed', characters: badCount ? 4 : 5 }));
                } else {
                    assert.equal(req.url, '/cloud/internal/v1/translation/quota/reservations');
                    assert.equal(value.characters, 5); assert.equal(value.fingerprint, hash);
                    res.end(JSON.stringify({ status: 'reserved', reservationId: id }));
                }
                for (const chunk of chunks) chunk.fill(0);
            });
        });
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        try {
            const client = createTranslationControlPlaneClient({ origin: `https://127.0.0.1:${server.address().port}`,
                ca: serverCert, certificate: clientCert, privateKey: clientKey });
            assert.equal(await client.matchDeployment('fixture-credential', deployment(), request().input), true);
            assert.deepEqual(await client.findFingerprintKey('fixture-credential', id), { status: 'not_found' });
            assert.deepEqual(await client.inspect('fixture-credential', request(), 'key', hash), { allowed: true, code: 'allowed', characters: 5 });
            assert.equal((await client.reserve('fixture-credential', reservation())).status, 'reserved');
            assert.equal((await client.claim('fixture-credential', id, proof())).status, 'claimed');
            blocked = true;
            assert.deepEqual(await client.inspect('fixture-credential', request(), 'key', hash), { allowed: false, code: 'privacy_blocked' });
            badCount = true;
            assert.deepEqual(await client.inspect('fixture-credential', request(), 'key', hash), { allowed: false, code: 'capability_unavailable' });
            assert.equal(calls, 7);
        } finally { await new Promise(resolve => server.close(resolve)); }
    });
