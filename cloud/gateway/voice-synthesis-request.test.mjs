import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseVoiceSynthesisRequest, voiceSynthesisInspectionFields, withVoiceSynthesisRequest } from './voice-synthesis-request.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const fixture = name => readFileSync(new URL(`../../contracts/fixtures/${name}/cloud-voice-synthesis-request-v1.json`, import.meta.url));
const valid = () => JSON.parse(fixture('valid').toString());
const parse = value => parseVoiceSynthesisRequest(Buffer.from(JSON.stringify(value)), now);

test('voice requests reflect Kokoro limits and expose all worker-visible text', () => {
    const request = parseVoiceSynthesisRequest(fixture('valid'), now);
    assert.ok(Object.isFrozen(request.input));
    assert.deepEqual(voiceSynthesisInspectionFields(request), ['Hello world.', 'af_heart', 'en-US', 'wav']);
    assert.throws(() => parseVoiceSynthesisRequest(fixture('invalid'), now));
    for (const mutate of [
        x => { x.input.text = ' '.repeat(3); }, x => { x.input.text = 'x'.repeat(2001); }, x => { x.input.text = '\ud800'; },
        x => { x.input.voiceId = 'unsupported'; }, x => { x.input.locale = ''; },
        x => { x.input.format = 'mp3'; }, x => { x.input.extra = true; },
        x => { x.expiresAt = '2030-01-01T00:00:00.000Z'; }, x => { x.expiresAt = '2030-01-01T00:05:00.001Z'; },
        x => { x.capability = 'inference.chat'; }, x => { x.requestId = '00000000-0000-0000-0000-000000000000'; },
    ]) { const value = valid(); mutate(value); assert.throws(() => parse(value)); }
    assert.throws(() => parseVoiceSynthesisRequest(Buffer.from('{"a":1,"a":2}'), now));
    assert.throws(() => parseVoiceSynthesisRequest(Uint8Array.from([0xff]), now));
});

test('voice body boundary rejects invalid media and wipes bytes even on processing failure', async () => {
    const bytes = Buffer.from(fixture('valid'));
    const request = { headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes }) };
    await assert.rejects(withVoiceSynthesisRequest(request, async value => {
        assert.equal(value.input.text, 'Hello world.');
        throw new Error('fixture failure');
    }, () => now));
    assert.ok(bytes.every(byte => byte === 0));
    let read = false;
    const encoded = { headers: new Headers({ 'content-type': 'application/json', 'content-encoding': 'gzip' }), readBody: async () => { read = true; } };
    assert.equal((await withVoiceSynthesisRequest(encoded, () => {}, () => now)).code, 'unsupported_media_type');
    assert.equal(read, false);
    const oversized = { headers: new Headers({ 'content-type': 'application/json', 'content-length': '65537' }) };
    assert.equal((await withVoiceSynthesisRequest(oversized, () => {}, () => now)).code, 'payload_too_large');
});
