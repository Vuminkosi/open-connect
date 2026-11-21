import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createVoiceOutputScreen } from './voice-output-screen.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const fingerprint = 'a'.repeat(64);
const input = () => ({ credential: 'credential', request: { requestId: id, input: { text: 'Hello, world!', locale: 'en-US' } },
    reservationId: id, claimId: id, audio: Buffer.alloc(4800), format: 'pcm', sampleRateHz: 24000, channels: 1 });

test('output screen requires real transcription, policy approval and recognized word agreement', async () => {
    const calls = []; let transcript = 'Hello world.'; let verdict = { allowed: true, code: 'allowed' };
    const transcriber = { isReady: async () => true, transcribe: async value => { calls.push('transcribe'); assert.equal(value.requestId, id);
        return { transcript, modelFingerprint: fingerprint }; } };
    const controlPlane = { inspectOutput: async (credential, requestId, reservationId, claimId, text, model) => {
        calls.push('policy'); assert.deepEqual([credential, requestId, reservationId, claimId, text, model], ['credential', id, id, id, transcript, fingerprint]); return verdict; } };
    const screen = createVoiceOutputScreen({ transcriber, controlPlane });
    assert.equal(await screen.isReady(), true);
    assert.deepEqual(await screen.inspectAudio(input()), { allowed: true, code: 'allowed' });
    assert.deepEqual(calls, ['transcribe', 'policy']);
    transcript = 'Hello stranger';
    assert.deepEqual(await screen.inspectAudio(input()), { allowed: false, code: 'capability_unavailable' });
    verdict = { allowed: false, code: 'privacy_blocked' };
    assert.deepEqual(await screen.inspectAudio(input()), { allowed: false, code: 'privacy_blocked' });
    verdict = { allowed: false, code: 'detector_unavailable' };
    assert.deepEqual(await screen.inspectAudio(input()), { allowed: false, code: 'capability_unavailable' });
});

test('screen fails closed for unavailable, malformed and cancelled dependencies', async () => {
    let policyCalls = 0;
    const screen = createVoiceOutputScreen({ transcriber: { isReady: async () => false, transcribe: async () => null },
        controlPlane: { inspectOutput: async () => { policyCalls++; return { allowed: true, code: 'allowed' }; } } });
    assert.equal(await screen.isReady(), false);
    assert.deepEqual(await screen.inspectAudio(input()), { allowed: false, code: 'capability_unavailable' });
    assert.deepEqual(await screen.inspectAudio(input(), AbortSignal.abort()), { allowed: false, code: 'capability_unavailable' });
    const symbols = input(); symbols.request.input.text = 'hello \ud83d\udcb3';
    assert.deepEqual(await screen.inspectAudio(symbols), { allowed: false, code: 'capability_unavailable' });
    assert.equal(policyCalls, 0);
});
