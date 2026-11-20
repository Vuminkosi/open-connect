import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createFingerprintKeyring } from './request-fingerprint.mjs';

const request = () => ({ tenantId: '11111111-1111-1111-1111-111111111111', applicationId: '22222222-2222-2222-2222-222222222222',
    capability: 'inference.chat', method: 'POST', route: '/cloud/v1/inference/chat', mediaType: 'application/json', body: Buffer.from('{"message":"hello"}') });

test('fingerprint matches the .NET vector and binds each request field', () => {
    const key = Buffer.from(Array.from({ length: 32 }, (_, i) => i));
    const ring = createFingerprintKeyring({ activeKeyId: 'key_1', keys: [{ id: 'key_1', key }, { id: 'key_2', key }] });
    const input = request();
    const result = ring.compute('key_1', input);
    assert.equal(ring.selectKeyId({ status: 'not_found' }), 'key_1');
    assert.equal(ring.selectKeyId({ status: 'found', keyId: 'key_2' }), 'key_2');
    assert.throws(() => ring.selectKeyId({ status: 'found', keyId: 'retired_missing' }));
    assert.throws(() => ring.selectKeyId({ status: 'legacy_unverifiable' }));
    assert.throws(() => ring.selectKeyId({ status: 'unavailable' }));
    assert.equal(result.toString('hex').toUpperCase(), '4CBC6A790B4D72FB808E5FDD040E8BD7AFCFD1066AA6B07281D84FD6DE979C85');
    for (const change of [
        { tenantId: '33333333-3333-3333-3333-333333333333' }, { applicationId: '44444444-4444-4444-4444-444444444444' },
        { capability: 'other' }, { method: 'GET' }, { route: '/other' }, { mediaType: 'text/plain' }, { body: Buffer.from('changed') },
    ]) assert.notDeepEqual(ring.compute('key_1', { ...input, ...change }), result);
    assert.notDeepEqual(ring.compute('key_2', input), result);
    key.fill(0);
    assert.deepEqual(ring.compute('key_1', input), result);
    ring.dispose();
    assert.throws(() => ring.compute('key_1', input));
});

test('fingerprint keyring rejects weak, duplicate, missing and invalid keys', () => {
    const key = Buffer.alloc(32, 1);
    for (const config of [
        { activeKeyId: 'key', keys: [] }, { activeKeyId: 'missing', keys: [{ id: 'key', key }] },
        { activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(31) }] },
        { activeKeyId: 'key', keys: [{ id: 'key', key }, { id: 'key', key }] },
        { activeKeyId: 'bad key', keys: [{ id: 'bad key', key }] },
    ]) assert.throws(() => createFingerprintKeyring(config));
    const ring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key }] });
    assert.throws(() => ring.compute('missing', request()));
    assert.throws(() => ring.compute('key', { ...request(), body: Buffer.alloc(65537) }));
    ring.dispose();
});
