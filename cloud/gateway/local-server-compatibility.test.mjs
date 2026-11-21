import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createLocalServer } from './local-server.mjs';

test('existing voice argument remains compatible when knowledge execution is added', async () => {
    const authorization = { isReady: async () => true };
    const voice = { probe: { probe: async () => true }, outputScreen: { isReady: async () => true } };
    const knowledge = { probe: { probe: async () => true } };
    for (const [embedding, expected] of [[undefined, ['voice.synthesize']], [knowledge, ['voice.synthesize', 'knowledge.embed']]]) {
        const server = createLocalServer(authorization, undefined, undefined, undefined, undefined, voice, embedding);
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        try {
            const response = await fetch(`http://127.0.0.1:${server.address().port}/health/ready`);
            assert.equal(response.status, 200);
            assert.deepEqual((await response.json()).capabilities, expected);
        } finally { await new Promise(resolve => server.close(resolve)); }
    }
});
