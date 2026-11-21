import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { prepareTranslationWorkerPayload, validateTranslationWorkerResponse } from './translation-worker-contract.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const request = () => JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-translation-request-v1.json', import.meta.url)));
const deployment = () => ({ modelRevision: 'revision-1', tokenizerHash: 'a'.repeat(64) });
const response = (input = request()) => ({ schemaVersion: 'openconnect.v1', taskId: input.requestId, status: 'completed', error: null,
    result: { translatedText: 'Bonjour.', sourceLang: input.input.sourceLang, targetLang: input.input.targetLang,
        ...deployment(), engine: 'real', engineWarning: null } });

test('prepare exact NLLB input from the validated cloud request', () => {
    const input = request();
    const prepared = prepareTranslationWorkerPayload(input, now);
    assert.deepEqual(JSON.parse(prepared.payload), { schemaVersion: 'openconnect.v1', taskId: input.requestId, input: input.input });
    assert.equal(prepared.requestId, input.requestId);
    prepared.payload.fill(0);
    input.input.inputRef = 'unsupported';
    assert.equal(prepareTranslationWorkerPayload(input, now), null);
    assert.equal(prepareTranslationWorkerPayload(null, now), null);
});

test('accept only real output matching request and pinned model provenance', () => {
    const input = request();
    assert.deepEqual(validateTranslationWorkerResponse(response(input), input, deployment()), {
        translatedText: 'Bonjour.', sourceLang: 'eng_Latn', targetLang: 'fra_Latn', ...deployment(),
    });
    for (const mutate of [
        x => { x.taskId = 'other'; }, x => { x.status = 'failed'; }, x => { x.error = {}; },
        x => { x.result.sourceLang = 'deu_Latn'; }, x => { x.result.targetLang = 'deu_Latn'; },
        x => { x.result.modelRevision = 'other'; }, x => { x.result.tokenizerHash = 'b'.repeat(64); },
        x => { x.result.engine = 'fake'; }, x => { x.result.engineWarning = 'warning'; },
        x => { x.result.translatedText = ' '; }, x => { x.result.translatedText = '\ud800'; },
        x => { x.result.translatedText = 'a'.repeat(20001); }, x => { x.result.extra = true; },
        x => { delete x.error; }, x => { x.extra = true; },
    ]) { const value = response(input); mutate(value); assert.equal(validateTranslationWorkerResponse(value, input, deployment()), null); }
    assert.equal(validateTranslationWorkerResponse(response(input), input, { ...deployment(), tokenizerHash: 'A'.repeat(64) }), null);
    assert.equal(validateTranslationWorkerResponse(response(input), input, null), null);
    const malformedRequest = { requestId: input.requestId, input: {} };
    const malformedResponse = response(input); malformedResponse.result.sourceLang = undefined; malformedResponse.result.targetLang = undefined;
    assert.equal(validateTranslationWorkerResponse(malformedResponse, malformedRequest, deployment()), null);
});
