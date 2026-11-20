import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { executeClassification } from './classification-execution.mjs';
import { withClassificationRequest } from './classification-request.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const identity = { credentialId: id, tenantId: '22222222-2222-2222-2222-222222222222', applicationId: '33333333-3333-3333-3333-333333333333', environment: 'Development' };
const envelope = () => ({ schemaVersion: 'openconnect.cloud.classification/v2', requestId: id,
    capability: 'classification.classify', expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'test fixture' } });

function fixture() {
    const calls = []; let captured; let workerFingerprint;
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 7) }] });
    const controlPlane = {
        authorize: async () => { calls.push('reauthorize'); return { authorized: true, identity }; },
        matchClassificationDeployment: async () => { calls.push('deployment'); return true; },
        findClassificationFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        reserveClassification: async (_, proof) => { calls.push('reserve'); assert.equal(proof.fingerprintKeyId, 'key'); return { status: 'reserved', reservationId: id }; },
        claimClassification: async (_, reservation, proof) => { calls.push('claim'); assert.equal(reservation, id); workerFingerprint = proof.workerPayloadFingerprint; return { status: 'claimed', claimId: id }; },
        completeClassification: async () => { calls.push('complete'); return { status: 'completed' }; },
    };
    const dispatcher = { executeClaimed: async (prepared, claim) => {
        calls.push('dispatch'); captured = { payload: prepared.payload, key: claim.key };
        assert.equal(createHmac('sha256', claim.key).update(prepared.payload).digest('base64'), workerFingerprint);
        assert.equal(JSON.parse(prepared.payload.toString()).input.text, 'test fixture');
        return { status: 'completed', result: { labels: ['safe'] } };
    } };
    async function run(signal) {
        const raw = Buffer.from(JSON.stringify(envelope()));
        const request = { headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes: raw }) };
        const result = await withClassificationRequest(request, (value, body) => executeClassification(
            { credential: 'credential', identity, value, body, deployment: { endpointReference: 'worker' }, signal },
            { controlPlane, keyring, dispatcher }));
        assert.ok(raw.every(byte => byte === 0));
        return result;
    }
    return { calls, controlPlane, dispatcher, keyring, run, captured: () => captured };
}

test('reserve, claim, dispatch and settle in order before returning a result', async () => {
    const f = fixture();
    try {
        assert.deepEqual(await f.run(), { status: 'completed', result: { labels: ['safe'] } });
        assert.deepEqual(f.calls, ['lookup', 'reserve', 'claim', 'reauthorize', 'deployment', 'dispatch', 'reauthorize', 'deployment', 'complete', 'reauthorize', 'deployment']);
        assert.ok(f.captured().payload.every(byte => byte === 0));
        assert.ok(f.captured().key.every(byte => byte === 0));
    } finally { f.keyring.dispose(); }
});

test('duplicates, denied claims and ambiguous dispatch or settlement never produce a result', async () => {
    for (const phase of ['duplicate', 'claim', 'dispatch', 'completion', 'cancel']) {
        const f = fixture(); const controller = new AbortController();
        if (phase === 'duplicate') f.controlPlane.reserveClassification = async () => ({ status: 'duplicate', reservationId: id });
        if (phase === 'claim') f.controlPlane.claimClassification = async () => ({ status: 'unavailable' });
        if (phase === 'dispatch') f.dispatcher.executeClaimed = async () => ({ status: 'upstream_unavailable' });
        if (phase === 'completion') f.controlPlane.completeClassification = async () => ({ status: 'unavailable' });
        if (phase === 'cancel') f.controlPlane.claimClassification = async () => { controller.abort(); return { status: 'claimed', claimId: id }; };
        try {
            assert.equal((await f.run(controller.signal)).status, phase === 'duplicate' ? 'duplicate' : 'unavailable');
            if (phase === 'duplicate' || phase === 'claim' || phase === 'cancel') assert.ok(!f.calls.includes('dispatch'));
            if (phase === 'dispatch') assert.ok(!f.calls.includes('complete'));
        } finally { f.keyring.dispose(); }
    }
});

test('revocation during worker execution aborts the request and leaves settlement untouched', async () => {
    const f = fixture(); let checks = 0; let aborted = false;
    f.controlPlane.authorize = async () => ({ authorized: ++checks === 1, identity });
    f.dispatcher.executeClaimed = async (_, __, signal) => {
        await new Promise(resolve => signal.addEventListener('abort', () => { aborted = true; resolve(); }, { once: true }));
        return { status: 'upstream_unavailable' };
    };
    let timeout;
    try {
        assert.deepEqual(await Promise.race([f.run(), new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error('revocation timeout')), 3000); })]), { status: 'unavailable' });
        assert.equal(aborted, true);
        assert.ok(!f.calls.includes('complete'));
    } finally { clearTimeout(timeout); f.keyring.dispose(); }
});


test('deployment revocation during worker execution aborts without settlement', async () => {
    const f = fixture(); let checks = 0; let aborted = false;
    f.controlPlane.matchClassificationDeployment = async () => ++checks === 1;
    f.dispatcher.executeClaimed = async (_, __, signal) => {
        await new Promise(resolve => signal.addEventListener('abort', () => { aborted = true; resolve(); }, { once: true }));
        return { status: 'upstream_unavailable' };
    };
    let timeout;
    try {
        assert.deepEqual(await Promise.race([f.run(), new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error('revocation timeout')), 3000); })]), { status: 'unavailable' });
        assert.equal(aborted, true);
        assert.ok(!f.calls.includes('complete'));
    } finally { clearTimeout(timeout); f.keyring.dispose(); }
});
