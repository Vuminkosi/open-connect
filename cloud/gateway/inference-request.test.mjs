import { test } from 'node:test';
import assert from 'node:assert/strict';
import { withInferenceRequest } from './inference-request.mjs';

const valid = () => ({ schemaVersion: 'openconnect.cloud.inference/v1', requestId: '11111111-1111-1111-1111-111111111111',
    capability: 'inference.chat', expiresAt: new Date(Date.now() + 60000).toISOString(), modelId: 'model',
    input: { messages: [{ role: 'user', content: 'fixture' }], maxTokens: 5, stream: false } });
function request(text, contentType = 'application/json') {
    const bytes = Buffer.from(text);
    return { bytes, value: { headers: new Headers({ 'content-type': contentType }), readBody: async () => ({ bytes }) } };
}
test('inference request validation clears body after callback and rejects invalid input', async () => {
    const input = request(JSON.stringify(valid()));
    const result = await withInferenceRequest(input.value, (value, bytes) => {
        assert.equal(value.modelId, 'model'); assert.ok(bytes.some(x => x)); return 'accepted';
    });
    assert.equal(result, 'accepted'); assert.ok(input.bytes.every(x => x === 0));
    for (const text of ['not-json', '{"input":{},"input":{}}', JSON.stringify({ ...valid(), capability: 'other' })]) {
        const item = request(text); assert.deepEqual(await withInferenceRequest(item.value, () => assert.fail()), { code: 'invalid_semantic_request' });
        assert.ok(item.bytes.every(x => x === 0));
    }
    assert.deepEqual(await withInferenceRequest(request('{}', 'text/plain').value, () => assert.fail()), { code: 'unsupported_media_type' });
    assert.deepEqual(await withInferenceRequest(request('x'.repeat(65537)).value, () => assert.fail()), { code: 'payload_too_large' });
});
