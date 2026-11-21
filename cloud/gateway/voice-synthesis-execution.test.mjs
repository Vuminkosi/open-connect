import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { executeVoiceSynthesis } from './voice-synthesis-execution.mjs';
import { createVoiceOutputScreen } from './voice-output-screen.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const input = () => ({ credential: 'credential', identity: { tenantId: id, applicationId: id },
    body: Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.voice-synthesis/v1', requestId: id, capability: 'voice.synthesize',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { text: 'hello', voiceId: 'af_heart', locale: 'en-US', format: 'pcm' } })),
    deployment: { endpointReference: 'voice' }, modelId: 'kokoro' });
const plane = () => ({ findFingerprintKey: async () => ({ status: 'not_found' }), inspect: async () => ({ allowed: true, code: 'allowed', characters: 5 }),
    reserve: async () => ({ status: 'reserved', reservationId: id }), claim: async () => ({ status: 'claimed', claimId: id }) });
const keys = () => createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
const monitor = (checks = [true, true, true]) => {
    const controller = new AbortController(); let index = 0;
    return { signal: controller.signal, start: async () => checks[index++], recheck: async () => checks[index++], dispose: () => controller.abort() };
};

test('speech audio requires current access and an allowed output screen', async () => {
    const keyring = keys(); const request = input(); const audio = Buffer.from([1, 2]); const calls = [];
    try {
        const result = await executeVoiceSynthesis(request, { controlPlane: plane(), keyring,
            createAccessMonitor: () => monitor(), dispatcher: { executeClaimed: async () => { calls.push('worker'); return { status: 'completed', result: { audio, format: 'pcm', sampleRateHz: 24000, channels: 1 } }; } },
            outputScreen: { isReady: async () => true, inspectAudio: async value => { calls.push('screen'); assert.equal(value.audio, audio); return { allowed: true, code: 'allowed' }; } } });
        assert.equal(result.status, 'completed'); assert.deepEqual(calls, ['worker', 'screen']); assert.deepEqual(audio, Buffer.from([1, 2]));
    } finally { audio.fill(0); request.body.fill(0); keyring.dispose(); }
});

test('speech audio is erased when access or output screening fails', async () => {
    for (const outcome of ['revoked', 'blocked', 'screen-error']) {
        const keyring = keys(); const request = input(); const audio = Buffer.from([1, 2]); let screenCalls = 0;
        try {
            const result = await executeVoiceSynthesis(request, { controlPlane: plane(), keyring,
                createAccessMonitor: () => monitor(outcome === 'revoked' ? [true, false] : [true, true, true]),
                dispatcher: { executeClaimed: async () => ({ status: 'completed', result: { audio, format: 'pcm', sampleRateHz: 24000, channels: 1 } }) },
                outputScreen: { isReady: async () => true, inspectAudio: async () => { screenCalls++; if (outcome === 'screen-error') throw new Error('unavailable'); return { allowed: false, code: 'privacy_blocked' }; } } });
            assert.equal(result.status, outcome === 'blocked' ? 'privacy_blocked' : 'unavailable');
            assert.deepEqual(audio, Buffer.alloc(2)); assert.equal(screenCalls, outcome === 'revoked' ? 0 : 1);
        } finally { request.body.fill(0); keyring.dispose(); }
    }
});

test('missing output screen cannot dispatch', async () => {
    let dispatched = false;
    const result = await executeVoiceSynthesis(input(), { dispatcher: { executeClaimed: async () => { dispatched = true; } } });
    assert.equal(result.status, 'unavailable'); assert.equal(dispatched, false);
});

test('unavailable audio screening prevents quota reservation', async () => {
    const keyring = keys(); const request = input(); let reserved = false;
    const controlPlane = plane(); controlPlane.reserve = async () => { reserved = true; return { status: 'reserved', reservationId: id }; };
    try {
        const result = await executeVoiceSynthesis(request, { controlPlane, keyring, createAccessMonitor: () => monitor(),
            dispatcher: { executeClaimed: async () => { throw new Error('must not dispatch'); } },
            outputScreen: { isReady: async () => false, inspectAudio: async () => ({ allowed: true, code: 'allowed' }) } });
        assert.equal(result.status, 'unavailable'); assert.equal(reserved, false);
    } finally { request.body.fill(0); keyring.dispose(); }
});

test('cancellation clears audio even if the output screen never responds', async () => {
    const keyring = keys(); const request = input(); const audio = Buffer.from([1, 2]);
    const cancelled = new AbortController(); request.signal = cancelled.signal;
    try {
        const result = await executeVoiceSynthesis(request, { controlPlane: plane(), keyring,
            createAccessMonitor: ({ signal }) => ({ signal, start: async () => true, recheck: async () => true, dispose() {} }),
            dispatcher: { executeClaimed: async () => ({ status: 'completed', result: { audio, format: 'pcm', sampleRateHz: 24000, channels: 1 } }) },
            outputScreen: { isReady: async () => true, inspectAudio: async () => { cancelled.abort(); return new Promise(() => {}); } } });
        assert.equal(result.status, 'unavailable');
        assert.deepEqual(audio, Buffer.alloc(2));
    } finally { request.body.fill(0); keyring.dispose(); }
});

test('speech admission, output transcription and policy execute before audio release', async () => {
    const keyring = keys(); const request = input(); const audio = Buffer.from([1, 2]); const calls = [];
    const outputScreen = createVoiceOutputScreen({
        transcriber: { isReady: async () => true, transcribe: async value => {
            calls.push('transcribe'); assert.equal(value.audio, audio); return { transcript: 'Hello!', modelFingerprint: 'a'.repeat(64) }; } },
        controlPlane: { inspectOutput: async () => { calls.push('policy'); return { allowed: true, code: 'allowed' }; } },
    });
    try {
        const result = await executeVoiceSynthesis(request, { controlPlane: plane(), keyring, createAccessMonitor: () => monitor(),
            dispatcher: { executeClaimed: async () => { calls.push('worker'); return { status: 'completed', result: { audio, format: 'pcm', sampleRateHz: 24000, channels: 1 } }; } }, outputScreen });
        assert.equal(result.status, 'completed');
        assert.deepEqual(calls, ['worker', 'transcribe', 'policy']);
    } finally { audio.fill(0); request.body.fill(0); keyring.dispose(); }
});
