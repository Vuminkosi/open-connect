import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { withInferenceAdmission } from './inference-admission.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { tenantId: '22222222-2222-2222-2222-222222222222', applicationId: '33333333-3333-3333-3333-333333333333' };
const envelope = () => ({ schemaVersion: 'openconnect.cloud.inference/v1', requestId: id, capability: 'inference.chat',
    expiresAt: new Date(Date.now() + 60000).toISOString(), modelId: 'registry/model-a',
    input: { messages: [{ role: 'user', content: 'fixture text' }], maxTokens: 20, stream: false } });
function fixture() {
    const calls = []; let observedMessages, claimProof, quoteBuffers, dispatchBuffers, privacyFingerprint;
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    const controlPlane = {
        findInferenceFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        inspectInference: async (_, __, messages, keyId, fingerprint) => { calls.push('privacy'); observedMessages = messages;
            assert.equal(keyId, 'key'); privacyFingerprint = fingerprint; return { allowed: true, code: 'allowed' }; },
        reserveInference: async (_, input) => { calls.push('reserve'); assert.equal(input.fingerprint, privacyFingerprint);
            assert.equal(input.reservedTokens, 27); return { status: 'reserved', reservationId: id }; },
        claimInference: async (_, reservationId, proof) => { calls.push('claim'); assert.equal(reservationId, id);
            assert.equal(proof.fingerprint, privacyFingerprint); claimProof = proof.workerPayloadFingerprint; return { status: 'claimed', claimId: id }; },
    };
    const quote = async ({ payload, key }) => {
        calls.push('quote'); quoteBuffers = { payload, key };
        const request = JSON.parse(payload.toString('utf8'));
        assert.deepEqual(request.input.messages, observedMessages);
        return { requestId: request.requestId, modelId: request.modelId, maxOutputTokens: request.input.maxTokens,
            inputTokenUpperBound: 7, workerPayloadFingerprint: createHmac('sha256', key).update(payload).digest('base64') };
    };
    const dispatch = async ({ payload, key, reservedTokens }) => {
        calls.push('dispatch'); dispatchBuffers = { payload, key };
        assert.equal(createHmac('sha256', key).update(payload).digest('base64'), claimProof);
        assert.deepEqual(JSON.parse(payload.toString('utf8')).input.messages, observedMessages);
        assert.equal(reservedTokens, 27);
        return { status: 'test_dispatched' };
    };
    const run = (body = Buffer.from(JSON.stringify(envelope())), extra = {}) => withInferenceAdmission(
        { credential: 'credential', identity, body, deployment: { endpointReference: 'configured-worker' }, ...extra },
        { controlPlane, keyring, quote: extra.quote ?? quote }, extra.dispatch ?? dispatch);
    return { calls, controlPlane, keyring, run, quoteBuffers: () => quoteBuffers, dispatchBuffers: () => dispatchBuffers };
}

test('inspects the same messages before quoting, reserving and claiming the bound worker payload', async () => {
    const f = fixture();
    try {
        assert.deepEqual(await f.run(), { status: 'test_dispatched' });
        assert.deepEqual(f.calls, ['lookup', 'privacy', 'quote', 'reserve', 'claim', 'dispatch']);
        for (const bytes of Object.values(f.quoteBuffers())) assert.ok(bytes.every(value => value === 0));
        for (const bytes of Object.values(f.dispatchBuffers())) assert.ok(bytes.every(value => value === 0));
    } finally { f.keyring.dispose(); }
});

test('invalid requests, privacy denial and invalid quotes never reserve or claim', async () => {
    for (const scenario of ['duplicate-json', 'unknown-field', 'privacy', 'quote-proof', 'quote-bound']) {
        const f = fixture();
        try {
            let body = Buffer.from(JSON.stringify(envelope())); let quote;
            if (scenario === 'duplicate-json') body = Buffer.from('{"schemaVersion":"x","schemaVersion":"y"}');
            if (scenario === 'unknown-field') body = Buffer.from(JSON.stringify({ ...envelope(), tenantId: 'forged' }));
            if (scenario === 'privacy') f.controlPlane.inspectInference = async () => ({ allowed: false, code: 'privacy_blocked' });
            if (scenario === 'quote-proof' || scenario === 'quote-bound') quote = async ({ payload, key }) => {
                const request = JSON.parse(payload.toString());
                return { requestId: request.requestId, modelId: request.modelId, maxOutputTokens: request.input.maxTokens,
                    inputTokenUpperBound: scenario === 'quote-bound' ? -1 : 7,
                    workerPayloadFingerprint: scenario === 'quote-proof' ? 'invalid' : createHmac('sha256', key).update(payload).digest('base64') };
            };
            assert.equal((await f.run(body, { quote })).status, scenario === 'privacy' ? 'privacy_blocked' : 'unavailable');
            assert.ok(!f.calls.includes('reserve')); assert.ok(!f.calls.includes('claim')); assert.ok(!f.calls.includes('dispatch'));
        } finally { f.keyring.dispose(); }
    }
});

test('snapshots caller bytes before asynchronous work', async () => {
    const f = fixture(); const body = Buffer.from(JSON.stringify(envelope()));
    f.controlPlane.findInferenceFingerprintKey = async () => { body.fill(0); return { status: 'not_found' }; };
    try { assert.deepEqual(await f.run(body), { status: 'test_dispatched' }); }
    finally { f.keyring.dispose(); }
});

test('duplicate reservation and failed claims do not dispatch', async () => {
    for (const stage of ['duplicate', 'claim']) {
        const f = fixture();
        if (stage === 'duplicate') f.controlPlane.reserveInference = async () => ({ status: 'duplicate', reservationId: id });
        else f.controlPlane.claimInference = async () => ({ status: 'unavailable' });
        try { assert.equal((await f.run()).status, stage === 'duplicate' ? 'duplicate' : 'unavailable'); assert.ok(!f.calls.includes('dispatch')); }
        finally { f.keyring.dispose(); }
    }
});


test('cancellation during claim or dispatch never returns an execution result', async () => {
    for (const stage of ['claim', 'dispatch']) {
        const f = fixture(); const controller = new AbortController();
        if (stage === 'claim') f.controlPlane.claimInference = async () => { controller.abort(); return { status: 'claimed', claimId: id }; };
        const dispatch = stage === 'dispatch' ? async () => { controller.abort(); return { status: 'test_dispatched' }; } : undefined;
        try { assert.equal((await f.run(Buffer.from(JSON.stringify(envelope())), { signal: controller.signal, dispatch })).status, 'unavailable'); }
        finally { f.keyring.dispose(); }
    }
});
