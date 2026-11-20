import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createInferenceProbe } from './inference-probe.mjs';

const options = { origin: 'https://localhost', ca: 'invalid', certificate: 'invalid', privateKey: 'invalid', workerToken: 'token',
    deploymentId: '11111111-1111-1111-1111-111111111111' };
test('inference probe rejects unsafe configuration and fails closed on invalid TLS or cancellation', async () => {
    for (const origin of ['http://localhost', 'https://user:pass@localhost', 'https://localhost/path'])
        assert.throws(() => createInferenceProbe({ ...options, origin }));
    assert.throws(() => createInferenceProbe({ ...options, deploymentId: 'invalid' }));
    const probe = createInferenceProbe(options);
    const cancelled = new AbortController(); cancelled.abort();
    assert.equal(await probe.probe(cancelled.signal), false);
    assert.equal(await probe.probe(), false);
});
