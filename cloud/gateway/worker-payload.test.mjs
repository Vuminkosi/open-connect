import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { bindWorkerPayload, cloudTenantToHostId } from './worker-payload.mjs';

test('maps Cloud tenant identifiers to the host representation', () => {
    assert.equal(cloudTenantToHostId('00000000-0000-0000-0000-000000000001'), '00000000000000000000000001');
    assert.equal(cloudTenantToHostId('ffffffff-ffff-ffff-ffff-ffffffffffff'), '7ZZZZZZZZZZZZZZZZZZZZZZZZZ');
    assert.throws(() => cloudTenantToHostId('00000000-0000-0000-0000-000000000000'));
    assert.throws(() => cloudTenantToHostId('invalid'));
});

test('binds a bounded payload to a fresh key', () => {
    const payload = Buffer.from('fixture');
    const first = bindWorkerPayload(payload);
    const second = bindWorkerPayload(payload);
    try {
        assert.deepEqual(first.fingerprint, createHmac('sha256', first.key).update(payload).digest());
        assert.notDeepEqual(first.key, second.key);
        assert.throws(() => bindWorkerPayload(Buffer.alloc(0)));
        assert.throws(() => bindWorkerPayload(Buffer.alloc(131073)));
    } finally { first.key.fill(0); first.fingerprint.fill(0); second.key.fill(0); second.fingerprint.fill(0); }
});
