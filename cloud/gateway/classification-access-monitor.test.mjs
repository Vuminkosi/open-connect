import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClassificationAccessMonitor } from './classification-access-monitor.mjs';

const identity = { credentialId: 'credential', tenantId: 'tenant', applicationId: 'app', environment: 'Development' };

test('active monitor aborts on revocation and stops polling after disposal', async () => {
    let allowed = true; let calls = 0;
    const controlPlane = { authorize: async () => { calls++; return { authorized: allowed, identity }; } };
    const monitor = createClassificationAccessMonitor({ controlPlane, credential: 'secret', identity, intervalMs: 10 });
    let timeout;
    try {
        assert.equal(await monitor.start(), true);
        const revoked = new Promise(resolve => monitor.signal.addEventListener('abort', resolve, { once: true }));
        allowed = false;
        await Promise.race([revoked, new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error('monitor did not abort')), 1000); })]);
        assert.equal(monitor.signal.aborted, true);
        assert.equal(await monitor.recheck(), false);
    } finally { clearTimeout(timeout); monitor.dispose(); }
    const after = calls;
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(calls, after);
});

test('identity changes and control-plane errors fail closed', async () => {
    for (const authorize of [async () => ({ authorized: true, identity: { ...identity, tenantId: 'other' } }), async () => { throw new Error('private'); }]) {
        const monitor = createClassificationAccessMonitor({ controlPlane: { authorize }, credential: 'secret', identity });
        try { assert.equal(await monitor.start(), false); assert.equal(monitor.signal.aborted, true); }
        finally { monitor.dispose(); }
    }
});

test('caller cancellation ends a stalled revalidation', async () => {
    const controller = new AbortController();
    const monitor = createClassificationAccessMonitor({ controlPlane: { authorize: () => new Promise(() => {}) },
        credential: 'secret', identity, signal: controller.signal });
    try {
        const pending = monitor.start();
        controller.abort();
        assert.equal(await pending, false);
        assert.equal(monitor.signal.aborted, true);
    } finally { monitor.dispose(); }
});
