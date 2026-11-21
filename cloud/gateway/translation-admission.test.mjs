import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { withTranslationAdmission } from './translation-admission.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const keyring = () => createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
const input = () => ({ credential: 'credential', identity: { tenantId: id, applicationId: id },
    body: Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.translation/v1', requestId: id, capability: 'translation.translate',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'Hello \u{1f30d}', sourceLang: 'eng_Latn', targetLang: 'fra_Latn' } })),
    deployment: { endpointReference: 'nllb' }, modelId: 'nllb' });

test('translation admission binds inspection and character reservation to one claimed payload', async () => {
    const keys = keyring(); const calls = []; let payload, key;
    const plane = {
        findFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        inspect: async (_credential, value, keyId, fingerprint) => { calls.push('inspect'); assert.equal(value.input.text, 'Hello \u{1f30d}');
            assert.equal(keyId, 'key'); assert.equal(Buffer.from(fingerprint, 'base64').length, 32); return { allowed: true, code: 'allowed', characters: 7 }; },
        reserve: async (_credential, value) => { calls.push('reserve'); assert.equal(value.characters, 7); return { status: 'reserved', reservationId: id }; },
        claim: async (_credential, _reservation, value) => { calls.push('claim'); plane.fingerprint = value.workerPayloadFingerprint; return { status: 'claimed', claimId: id }; },
    };
    const body = input();
    try {
        const result = await withTranslationAdmission(body, { controlPlane: plane, keyring: keys }, async value => {
            calls.push('dispatch'); payload = value.payload; key = value.key;
            assert.equal(createHmac('sha256', value.key).update(value.payload).digest('base64'), plane.fingerprint);
            assert.equal(JSON.parse(value.payload.toString()).input.text, 'Hello \u{1f30d}');
            return { status: 'completed' };
        });
        assert.equal(result.status, 'completed'); assert.deepEqual(calls, ['lookup', 'inspect', 'reserve', 'claim', 'dispatch']);
        assert.ok(payload.every(byte => byte === 0)); assert.ok(key.every(byte => byte === 0));
    } finally { body.body.fill(0); keys.dispose(); }
});

test('denial, count mismatch and duplicate quota never dispatch', async () => {
    const keys = keyring(); let dispatched = 0; let reserved = 0;
    const plane = { findFingerprintKey: async () => ({ status: 'not_found' }),
        inspect: async () => ({ allowed: false, code: 'privacy_blocked' }),
        reserve: async () => { reserved++; return { status: 'duplicate', reservationId: id }; },
        claim: async () => { throw new Error('must not claim'); } };
    const body = input(); const execute = async () => { dispatched++; };
    try {
        assert.equal((await withTranslationAdmission(body, { controlPlane: plane, keyring: keys }, execute)).status, 'privacy_blocked');
        assert.equal(reserved, 0);
        plane.inspect = async () => ({ allowed: true, code: 'allowed', characters: 6 });
        assert.equal((await withTranslationAdmission(body, { controlPlane: plane, keyring: keys }, execute)).status, 'unavailable');
        assert.equal(reserved, 0);
        plane.inspect = async () => ({ allowed: true, code: 'allowed', characters: 7 });
        assert.equal((await withTranslationAdmission(body, { controlPlane: plane, keyring: keys }, execute)).status, 'duplicate');
        assert.equal(dispatched, 0);
    } finally { body.body.fill(0); keys.dispose(); }
});

test('cancellation before delivery prevents a completed result escaping', async () => {
    const keys = keyring(); const controller = new AbortController(); const body = { ...input(), signal: controller.signal };
    const plane = { findFingerprintKey: async () => ({ status: 'not_found' }), inspect: async () => ({ allowed: true, code: 'allowed', characters: 7 }),
        reserve: async () => ({ status: 'reserved', reservationId: id }), claim: async () => ({ status: 'claimed', claimId: id }) };
    try {
        const result = await withTranslationAdmission(body, { controlPlane: plane, keyring: keys }, async () => {
            controller.abort(); return { status: 'completed', result: { translatedText: 'Bonjour' } };
        });
        assert.equal(result.status, 'unavailable');
    } finally { body.body.fill(0); keys.dispose(); }
});
