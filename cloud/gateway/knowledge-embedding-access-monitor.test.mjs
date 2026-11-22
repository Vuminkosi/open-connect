import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createKnowledgeEmbeddingAccessMonitor } from './knowledge-embedding-access-monitor.mjs';

const identity = { credentialId: 'credential', tenantId: 'tenant', applicationId: 'application', environment: 'Development' };
const deployment = { endpointReference: 'analytics' };

test('rechecks scoped authorization and deployment and aborts on revocation', async () => {
    let authorized = true; let matched = true;
    const calls = [];
    const controlPlane = {
        authorize: async (_credential, capability) => { calls.push(capability); return { authorized, identity }; },
        matchEmbeddingDeployment: async () => matched,
    };
    const monitor = createKnowledgeEmbeddingAccessMonitor({ controlPlane, credential: 'token', identity, deployment });
    try {
        assert.equal(await monitor.start(), true);
        assert.equal(await monitor.recheck(), true);
        matched = false;
        assert.equal(await monitor.recheck(), false);
        assert.equal(monitor.signal.aborted, true);
        assert.deepEqual(calls, ['knowledge.embed', 'knowledge.embed', 'knowledge.embed']);
        authorized = false;
    } finally { monitor.dispose(); }
});

test('rejects changed identities and unavailable control plane', async () => {
    for (const authorize of [async () => ({ authorized: true, identity: { ...identity, tenantId: 'other' } }),
        async () => { throw new Error('unavailable'); }]) {
        const monitor = createKnowledgeEmbeddingAccessMonitor({ credential: 'token', identity, deployment,
            controlPlane: { authorize, matchEmbeddingDeployment: async () => true } });
        try { assert.equal(await monitor.start(), false); assert.equal(monitor.signal.aborted, true); }
        finally { monitor.dispose(); }
    }
});
