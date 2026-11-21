import { test } from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createTranslationDispatcher } from './translation-dispatch.mjs';
import { prepareTranslationWorkerPayload } from './translation-worker-contract.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const request = () => ({ schemaVersion: 'openconnect.cloud.translation/v1', requestId: id, capability: 'translation.translate',
    expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'Hello.', sourceLang: 'eng_Latn', targetLang: 'fra_Latn' } });
const deployment = () => ({ modelRevision: 'revision-1', tokenizerHash: 'a'.repeat(64) });
const claim = () => { const value = request(); return { request: value, payload: prepareTranslationWorkerPayload(value).payload,
    key: Buffer.alloc(32, 1), reservationId: id, claimId: id, deployment: deployment() }; };

test('translation dispatch rejects unsafe configuration and unbound payloads before sending', async () => {
    const config = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad', workerToken: 'token' };
    assert.throws(() => createTranslationDispatcher({ ...config, origin: 'http://localhost' }));
    assert.throws(() => createTranslationDispatcher({ ...config, workerToken: 'bad token' }));
    const dispatcher = createTranslationDispatcher(config);
    const value = claim();
    try {
        assert.deepEqual(await dispatcher.executeClaimed(null), { status: 'upstream_unavailable' });
        assert.deepEqual(await dispatcher.executeClaimed({ ...value, key: Buffer.alloc(31) }), { status: 'upstream_unavailable' });
        assert.deepEqual(await dispatcher.executeClaimed({ ...value, deployment: null }), { status: 'upstream_unavailable' });
        assert.deepEqual(await dispatcher.executeClaimed({ ...value, request: { ...value.request, input: { ...value.request.input, text: 'changed' } } }),
            { status: 'upstream_unavailable' });
        assert.deepEqual(await dispatcher.executeClaimed(value, AbortSignal.abort()), { status: 'upstream_unavailable' });
    } finally { value.payload.fill(0); value.key.fill(0); }
});

test('translation dispatch uses mTLS, binds the envelope and rejects untrusted responses without retry',
    { skip: !process.env.OC_NLLB_TEST_TLS_DIR }, async () => {
        const root = process.env.OC_NLLB_TEST_TLS_DIR;
        const serverCert = readFileSync(join(root, 'authority', 'authority.crt'));
        const serverKey = readFileSync(join(root, 'authority', 'authority.key'));
        const clientCert = readFileSync(join(root, 'gateway', 'gateway.crt'));
        const clientKey = readFileSync(join(root, 'gateway', 'gateway.key'));
        let calls = 0; let invalid = false;
        const server = https.createServer({ cert: serverCert, key: serverKey, ca: clientCert, requestCert: true, rejectUnauthorized: true }, (req, res) => {
            calls++; assert.equal(req.client.authorized, true); assert.equal(req.headers.authorization, 'Bearer fixture-token');
            assert.equal(req.url, '/internal/cloud/v1/translation/execute');
            const chunks = []; req.on('data', chunk => chunks.push(chunk));
            req.on('end', () => {
                const envelope = JSON.parse(Buffer.concat(chunks));
                const payload = JSON.parse(Buffer.from(envelope.payload, 'base64'));
                assert.equal(envelope.schemaVersion, 'openconnect.cloud.worker.translation/v1');
                assert.equal(envelope.reservationId, id); assert.equal(envelope.claimId, id);
                assert.deepEqual(Buffer.from(envelope.key, 'base64'), Buffer.alloc(32, 1));
                res.setHeader('content-type', 'application/json');
                res.end(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: payload.taskId, status: 'completed', error: null,
                    result: { translatedText: 'Bonjour.', sourceLang: payload.input.sourceLang, targetLang: payload.input.targetLang,
                        ...deployment(), engine: invalid ? 'fake' : 'real', engineWarning: null } }));
                for (const chunk of chunks) chunk.fill(0);
            });
        });
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        try {
            const dispatcher = createTranslationDispatcher({ origin: `https://127.0.0.1:${server.address().port}`, ca: serverCert,
                certificate: clientCert, privateKey: clientKey, workerToken: 'fixture-token' });
            const value = claim();
            try {
                const completed = await dispatcher.executeClaimed(value);
                assert.equal(completed.status, 'completed'); assert.equal(completed.result.translatedText, 'Bonjour.');
                invalid = true;
                assert.deepEqual(await dispatcher.executeClaimed(value), { status: 'upstream_unavailable' });
                assert.equal(calls, 2);
            } finally { value.payload.fill(0); value.key.fill(0); }
        } finally { await new Promise(resolve => server.close(resolve)); }
    });
