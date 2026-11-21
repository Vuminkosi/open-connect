import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { createVoiceSynthesisAccessMonitor } from './voice-synthesis-access-monitor.mjs';
import { createVoiceOutputScreen } from './voice-output-screen.mjs';
import { voiceSynthesisFromEnvironment } from './voice-synthesis-configuration.mjs';

const id = '11111111-1111-1111-1111-111111111111';
test('voice gateway authorizes, claims, screens and clears buffers before responding', async () => {
    const calls = []; let denied = false; let raw; let audio;
    const value = { schemaVersion: 'openconnect.cloud.voice-synthesis/v1', requestId: id, capability: 'voice.synthesize',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'hello', voiceId: 'af_heart', locale: 'en-US', format: 'pcm' } };
    const request = { url: 'http://localhost/cloud/v1/voice/synthesis', method: 'POST',
        headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); raw = Buffer.from(JSON.stringify(value)); return { bytes: raw }; } };
    const authorization = { isReady: async () => true, authorize: async () => { calls.push('authorize');
        return { authorized: true, identity: { tenantId: id, applicationId: id } }; } };
    const controlPlane = { findFingerprintKey: async () => ({ status: 'not_found' }), inspect: async () => { calls.push('input'); return { allowed: true, code: 'allowed', characters: 5 }; },
        reserve: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claim: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; }, verifyClaim: async () => true,
        inspectOutput: async () => { calls.push('output'); return denied ? { allowed: false, code: 'privacy_blocked' } : { allowed: true, code: 'allowed' }; } };
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
    const outputScreen = createVoiceOutputScreen({ controlPlane, transcriber: { isReady: async () => true,
        transcribe: async () => { calls.push('transcribe'); return { transcript: 'Hello.', modelFingerprint: 'a'.repeat(64) }; } } });
    const voiceExecution = { keyring, controlPlane, deployment: {}, modelId: 'kokoro', createAccessMonitor: createVoiceSynthesisAccessMonitor,
        outputScreen, probe: { probe: async () => true }, dispatcher: { executeClaimed: async () => {
            calls.push('dispatch'); audio = Buffer.from([1, 2]); return { status: 'completed', result: { audio, format: 'pcm', sampleRateHz: 24000,
                channels: 1, voiceId: 'af_heart', locale: 'en-US' } }; } } };
    try {
        const ready = await handle({ url: 'http://localhost/health/ready', method: 'GET', headers: new Headers() }, { authorization, voiceExecution });
        assert.equal(ready.status, 200); assert.deepEqual((await ready.json()).capabilities, ['voice.synthesize']);
        const response = await handle(request, { authorization, voiceExecution });
        assert.equal(response.status, 200); assert.equal((await response.json()).result.audioBase64, 'AQI=');
        assert.deepEqual(calls, ['authorize', 'body', 'input', 'reserve', 'claim', 'dispatch', 'transcribe', 'output']);
        assert.ok(raw.every(byte => byte === 0)); assert.ok(audio.every(byte => byte === 0));
        calls.length = 0; denied = true;
        assert.equal((await handle(request, { authorization, voiceExecution })).status, 403);
        assert.ok(audio.every(byte => byte === 0));
        calls.length = 0; authorization.authorize = async () => ({ authorized: false, code: 'unauthorized' });
        assert.equal((await handle(request, { authorization, voiceExecution })).status, 401);
        assert.deepEqual(calls, []);
    } finally { keyring.dispose(); }
});

const config = { CLOUD_VOICE_EXECUTION_ENABLED: 'true', CLOUD_VOICE_ORIGIN: 'https://worker', CLOUD_VOICE_CA_FILE: '/secrets/ca',
    CLOUD_VOICE_CERT_FILE: '/secrets/cert', CLOUD_VOICE_KEY_FILE: '/secrets/key', CLOUD_VOICE_TOKEN_FILE: '/secrets/token',
    CLOUD_VOICE_DEPLOYMENT_ID: id, CLOUD_VOICE_MODEL_ID: 'kokoro', CLOUD_VOICE_ENDPOINT_REFERENCE: 'worker', CLOUD_VOICE_REGION: 'region',
    CLOUD_VOICE_CONTRACT_VERSION: 'v1', CLOUD_VOICE_MODEL_REVISION: 'rev', CLOUD_VOICE_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64),
    CLOUD_AUTHORITY_ORIGIN: 'https://authority', CLOUD_AUTHORITY_CA_FILE: '/secrets/ca', CLOUD_AUTHORITY_CERT_FILE: '/secrets/cert',
    CLOUD_AUTHORITY_KEY_FILE: '/secrets/key', CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'key', CLOUD_FINGERPRINT_KEYS_FILE: '/secrets/fingerprint',
    CLOUD_VOICE_SCREEN_ORIGIN: 'https://screen', CLOUD_VOICE_SCREEN_CA_FILE: '/secrets/ca', CLOUD_VOICE_SCREEN_CERT_FILE: '/secrets/cert',
    CLOUD_VOICE_SCREEN_KEY_FILE: '/secrets/key', CLOUD_VOICE_SCREEN_TOKEN_FILE: '/secrets/token', CLOUD_VOICE_SCREEN_MODEL_ID: 'whisper',
    CLOUD_VOICE_SCREEN_MODEL_REVISION: 'v1', CLOUD_VOICE_SCREEN_MODEL_FINGERPRINT: 'b'.repeat(64) };
const read = path => path.endsWith('fingerprint') ? Buffer.from('key:' + Buffer.alloc(32, 1).toString('base64') + '\n') : Buffer.from('fixture');
test('voice execution is opt-in and requires a pinned deployment and output screen', () => {
    assert.equal(voiceSynthesisFromEnvironment({}, undefined), undefined);
    assert.throws(() => voiceSynthesisFromEnvironment(config, undefined, read));
    for (const change of [{ CLOUD_VOICE_ORIGIN: 'http://worker' }, { CLOUD_VOICE_DEPLOYMENT_ID: 'invalid' },
        { CLOUD_VOICE_SCREEN_MODEL_FINGERPRINT: 'invalid' }, { CLOUD_VOICE_SCREEN_ORIGIN: 'http://screen' }, { CLOUD_VOICE_KEY_FILE: 'relative' }])
        assert.throws(() => voiceSynthesisFromEnvironment({ ...config, ...change }, {}, read));
    const execution = voiceSynthesisFromEnvironment(config, {}, read);
    assert.equal(execution.modelId, 'kokoro'); execution.keyring.dispose();
});
