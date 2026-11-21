import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { prepareVoiceSynthesisWorkerPayload, validateVoiceSynthesisWorkerResponse } from './voice-synthesis-worker-contract.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const request = () => JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-voice-synthesis-request-v1.json', import.meta.url)));
const wav = () => {
    const bytes = Buffer.alloc(46);
    bytes.write('RIFF', 0); bytes.writeUInt32LE(38, 4); bytes.write('WAVEfmt ', 8); bytes.writeUInt32LE(16, 16);
    bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(24000, 24); bytes.writeUInt32LE(48000, 28);
    bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(2, 40);
    return bytes;
};
const response = (input = request()) => ({ schemaVersion: 'openconnect.v1', taskId: input.requestId, kind: 'speech.synthesize',
    idempotentReplay: false, result: { ...input.input, sampleRateHz: 24000, channels: 1, audioBase64: wav().toString('base64') } });

test('prepare binds the validated request to the existing Kokoro worker payload', () => {
    const input = request();
    const prepared = prepareVoiceSynthesisWorkerPayload(input, now);
    assert.deepEqual(JSON.parse(prepared.payload.toString()), { schemaVersion: 'openconnect.v1', taskId: input.requestId,
        kind: 'speech.synthesize', payload: { schemaVersion: 'openconnect.v1', ...input.input } });
    assert.equal(prepared.requestId, input.requestId);
    prepared.payload.fill(0);
    input.input.format = 'mp3';
    assert.equal(prepareVoiceSynthesisWorkerPayload(input, now), null);
});

test('accept only matching, bounded canonical PCM or WAV audio and omit echoed text', () => {
    const input = request();
    const output = validateVoiceSynthesisWorkerResponse(response(input), input);
    assert.equal(output.format, 'wav');
    assert.equal(output.audio.length, 46);
    assert.equal(Object.hasOwn(output, 'text'), false);
    output.audio.fill(0);
    input.input.format = 'pcm';
    const pcm = response(input); pcm.result.audioBase64 = Buffer.from([0, 1, 2, 3]).toString('base64');
    const pcmOutput = validateVoiceSynthesisWorkerResponse(pcm, input);
    assert.equal(pcmOutput.audio.length, 4);
    pcmOutput.audio.fill(0);
    for (const mutate of [
        x => { x.taskId = 'other'; }, x => { x.result.text = 'different'; }, x => { x.result.audioBase64 = 'AA'; },
        x => { x.result.audioBase64 = 'A==='; }, x => { x.result.audioBase64 = Buffer.from([0]).toString('base64'); },
        x => { x.result.sampleRateHz = 44100; }, x => { x.result.channels = 2; },
        x => { x.idempotentReplay = true; }, x => { x.result.extra = true; },
    ]) { const value = response(input); value.result.audioBase64 = Buffer.from([0, 1]).toString('base64'); mutate(value); assert.equal(validateVoiceSynthesisWorkerResponse(value, input), null); }
    const waveInput = request(); const invalidWav = response(waveInput); const bytes = wav(); bytes.writeUInt32LE(44100, 24);
    invalidWav.result.audioBase64 = bytes.toString('base64');
    assert.equal(validateVoiceSynthesisWorkerResponse(invalidWav, waveInput), null);
    assert.equal(validateVoiceSynthesisWorkerResponse(response(), null), null);
});
