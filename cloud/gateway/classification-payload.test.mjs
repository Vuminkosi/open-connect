import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { bindClassificationPayload } from './classification-payload.mjs';

test('payload binding uses a fresh secret and covers exact bytes', () => {
    const payload = Buffer.from('worker fixture');
    const first = bindClassificationPayload(payload);
    const second = bindClassificationPayload(payload);
    try {
        assert.equal(first.key.length, 32);
        assert.notDeepEqual(first.key, second.key);
        assert.deepEqual(first.fingerprint, createHmac('sha256', first.key).update(payload).digest());
        assert.notDeepEqual(first.fingerprint, createHmac('sha256', first.key).update(Buffer.from('worker fixture ')).digest());
        assert.throws(() => bindClassificationPayload(Buffer.alloc(0)));
        assert.throws(() => bindClassificationPayload(Buffer.alloc(131073)));
    } finally {
        first.key.fill(0); second.key.fill(0); first.fingerprint.fill(0); second.fingerprint.fill(0); payload.fill(0);
    }
});
