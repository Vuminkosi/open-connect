import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClassificationDispatcher, validateClassificationWorkerResponse } from './classification-dispatch.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const good = () => ({ schemaVersion: 'openconnect.v1', taskId: id, status: 'completed', error: null,
    result: { labels: ['safe'], scores: { safe: 0.9 }, abstained: false, modelRevision: 'r1', labelSetVersion: 'v1', engine: 'real', engineWarning: null, grantsTools: false } });

test('worker output is bounded to the pinned model and versioned label vocabulary', () => {
    assert.deepEqual(validateClassificationWorkerResponse(good(), id, 'r1', 'v1'),
        { labels: ['safe'], scores: { safe: 0.9 }, abstained: false, modelRevision: 'r1', labelSetVersion: 'v1' });
    for (const mutate of [
        x => { x.result.engine = 'fake'; }, x => { x.result.modelRevision = 'other'; },
        x => { x.result.grantsTools = true; }, x => { x.result.labels = ['private text']; },
        x => { x.result.scores.safe = 1.01; }, x => { x.result.scores.extra = 0.5; },
        x => { x.result.labels.push('safe'); }, x => { x.result.scores = {}; },
        x => { x.taskId = 'other'; }, x => { x.extra = 'private'; },
    ]) { const response = good(); mutate(response); assert.equal(validateClassificationWorkerResponse(response, id, 'r1', 'v1'), null); }
});

test('dispatcher rejects unsafe destinations, requests, cancellation and invalid TLS', async () => {
    const options = { origin: 'https://localhost', ca: 'bad', certificate: 'bad', privateKey: 'bad', workerToken: 'secret', modelRevision: 'r1' };
    for (const origin of ['http://localhost', 'https://user:pass@localhost', 'https://localhost/path', 'https://localhost/?target=x'])
        assert.throws(() => createClassificationDispatcher({ ...options, origin }));
    const dispatcher = createClassificationDispatcher(options);
    const request = { requestId: id, input: { text: 'private fixture' } };
    const controller = new AbortController(); controller.abort();
    assert.deepEqual(await dispatcher.execute(request, controller.signal), { status: 'upstream_unavailable' });
    assert.deepEqual(await dispatcher.execute({ ...request, input: { inputRef: 'opaque' } }), { status: 'upstream_unavailable' });
    assert.deepEqual(await dispatcher.execute({ ...request, configuration: { labelSetVersion: 'unknown' } }), { status: 'upstream_unavailable' });
    assert.deepEqual(await dispatcher.execute(request), { status: 'upstream_unavailable' });
});
