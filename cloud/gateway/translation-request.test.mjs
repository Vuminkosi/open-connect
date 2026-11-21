import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseTranslationRequest, translationInspectionFields, withTranslationRequest } from './translation-request.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const fixture = name => readFileSync(new URL(`../../contracts/fixtures/${name}/cloud-translation-request-v1.json`, import.meta.url));
const valid = () => JSON.parse(fixture('valid').toString());
const parse = value => parseTranslationRequest(Buffer.from(JSON.stringify(value)), now);

test('translation input validates bounded Unicode text, language identifiers and deadline', () => {
    const request = parseTranslationRequest(fixture('valid'), now);
    assert.ok(Object.isFrozen(request.input));
    assert.deepEqual(translationInspectionFields(request), ['Hello world.', 'eng_Latn', 'fra_Latn']);
    assert.throws(() => parseTranslationRequest(fixture('invalid'), now));
    for (const mutate of [
        x => { x.input.text = ' '.repeat(3); }, x => { x.input.text = 'x'.repeat(5001); }, x => { x.input.text = '\ud800'; },
        x => { x.input.sourceLang = 'en'; }, x => { x.input.targetLang = x.input.sourceLang; },
        x => { x.input.inputRef = 'opaque'; }, x => { x.input.extra = true; },
        x => { x.expiresAt = '2030-01-01T00:00:00.000Z'; }, x => { x.expiresAt = '2030-01-01T00:05:00.001Z'; },
        x => { x.capability = 'inference.chat'; }, x => { x.requestId = '00000000-0000-0000-0000-000000000000'; },
    ]) { const value = valid(); mutate(value); assert.throws(() => parse(value)); }
    const max = valid(); max.input.text = '😀'.repeat(5000); assert.equal([...parse(max).input.text].length, 5000);
    assert.throws(() => parseTranslationRequest(Buffer.from('{"a":1,"a":2}'), now));
    assert.throws(() => parseTranslationRequest(Uint8Array.from([0xff]), now));
});

test('translation boundary rejects unsupported bodies and wipes input after failure', async () => {
    const bytes = Buffer.from(fixture('valid'));
    const request = { headers: new Headers({ 'content-type': 'application/json' }), readBody: async () => ({ bytes }) };
    await assert.rejects(withTranslationRequest(request, async value => {
        assert.equal(value.input.text, 'Hello world.'); throw new Error('fixture failure');
    }, () => now));
    assert.ok(bytes.every(byte => byte === 0));
    let read = false;
    const encoded = { headers: new Headers({ 'content-type': 'application/json', 'content-encoding': 'gzip' }), readBody: async () => { read = true; } };
    assert.equal((await withTranslationRequest(encoded, () => {}, () => now)).code, 'unsupported_media_type');
    assert.equal(read, false);
    const oversized = { headers: new Headers({ 'content-type': 'application/json', 'content-length': '65537' }) };
    assert.equal((await withTranslationRequest(oversized, () => {}, () => now)).code, 'payload_too_large');
});
