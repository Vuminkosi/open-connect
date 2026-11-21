import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createLiveUIAccessMonitor } from './liveui-access-monitor.mjs';

const id = '11111111-1111-1111-1111-111111111111';
test('claim access monitor aborts when current policy is revoked', async () => {
    let allowed = true;
    const monitor = createLiveUIAccessMonitor({ controlPlane: { verifyClaim: async () => allowed },
        credential: 'key', reservationId: id, claimId: id, intervalMs: 10 });
    try {
        assert.equal(await monitor.start(), true);
        allowed = false;
        await new Promise(resolve => monitor.signal.addEventListener('abort', resolve, { once: true }));
        assert.equal(await monitor.recheck(), false);
    } finally { monitor.dispose(); }
});

test('caller cancellation interrupts a stalled claim check', async () => {
    const controller = new AbortController();
    const monitor = createLiveUIAccessMonitor({ controlPlane: { verifyClaim: () => new Promise(() => {}) },
        credential: 'key', reservationId: id, claimId: id, signal: controller.signal });
    try {
        const pending = monitor.start(); controller.abort();
        assert.equal(await pending, false);
        assert.equal(monitor.signal.aborted, true);
    } finally { monitor.dispose(); }
});
