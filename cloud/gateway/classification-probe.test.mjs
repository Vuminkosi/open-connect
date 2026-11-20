import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClassificationProbe } from './classification-probe.mjs';

const options = { origin: 'https://localhost', ca: 'ca', certificate: 'cert', privateKey: 'key', workerToken: 'token', modelRevision: 'revision' };

test('probe accepts only deployment-owned HTTPS origins and bounded credentials', () => {
    for (const origin of ['http://localhost', 'https://user:pass@localhost', 'https://localhost/path', 'https://localhost/?url=x'])
        assert.throws(() => createClassificationProbe({ ...options, origin }));
    assert.throws(() => createClassificationProbe({ ...options, workerToken: 'token\r\ninjected' }));
    assert.throws(() => createClassificationProbe({ ...options, modelRevision: '' }));
});

test('probe fails closed on abort or invalid TLS without disclosing errors', async () => {
    const controller = new AbortController(); controller.abort();
    const probe = createClassificationProbe(options);
    assert.deepEqual(await probe.probe(controller.signal), { ready: false, code: 'capability_unavailable' });
    assert.deepEqual(await probe.probe(), { ready: false, code: 'capability_unavailable' });
});
