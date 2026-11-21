import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAnalyticsAccessMonitor } from './analytics-access-monitor.mjs';

const identity = { credentialId: 'credential', tenantId: 'tenant', applicationId: 'application', environment: 'Development' };
const deployment = { endpointReference: 'analytics' };

test('rechecks scoped authorization and deployment and aborts on revocation', async () => {
    let authorized = true; let matched = true;
    const calls = [];
    const controlPlane = {
        authorize: async (_credential, capability) => { calls.push(capability); return { authorized, identity }; },
        matchAnalyticsDeployment: async () => matched,
    };
    const monitor = createAnalyticsAccessMonitor({ controlPlane, credential: 'token', identity, deployment });
    try {
        assert.equal(await monitor.start(), true);
        assert.equal(await monitor.recheck(), true);
        matched = false;
        assert.equal(await monitor.recheck(), false);
        assert.equal(monitor.signal.aborted, true);
        assert.deepEqual(calls, ['analytics.analyze', 'analytics.analyze', 'analytics.analyze']);
        authorized = false;
    } finally { monitor.dispose(); }
});

test('rejects changed identities and unavailable control plane', async () => {
    for (const authorize of [async () => ({ authorized: true, identity: { ...identity, tenantId: 'other' } }),
        async () => { throw new Error('unavailable'); }]) {
        const monitor = createAnalyticsAccessMonitor({ credential: 'token', identity, deployment,
            controlPlane: { authorize, matchAnalyticsDeployment: async () => true } });
        try { assert.equal(await monitor.start(), false); assert.equal(monitor.signal.aborted, true); }
        finally { monitor.dispose(); }
    }
});
