import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { cloudTenantToHostId, prepareAnalyticsWorkerPayload, bindAnalyticsWorkerPayload } from './analytics-payload.mjs';
import { createHmac } from 'node:crypto';

const now = Date.parse('2030-01-01T00:00:00.000Z');

test('binds the exact worker payload using a fresh claim key', () => {
    const bytes = Buffer.from('private fixture');
    const first = bindAnalyticsWorkerPayload(bytes);
    const second = bindAnalyticsWorkerPayload(bytes);
    assert.deepEqual(first.fingerprint, createHmac('sha256', first.key).update(bytes).digest());
    assert.notDeepEqual(first.key, second.key);
    assert.throws(() => bindAnalyticsWorkerPayload(Buffer.alloc(0)));
    first.key.fill(0); first.fingerprint.fill(0); second.key.fill(0); second.fingerprint.fill(0);
});
const body = () => Buffer.from(readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url)));
const identity = { tenantId: '00000000-0000-0000-0000-000000000001', applicationId: '22222222-2222-2222-2222-222222222222' };

test('tenant mapping agrees with .NET network-order vectors', () => {
    assert.equal(cloudTenantToHostId(identity.tenantId), '00000000000000000000000001');
    assert.equal(cloudTenantToHostId('ffffffff-ffff-ffff-ffff-ffffffffffff'), '7ZZZZZZZZZZZZZZZZZZZZZZZZZ');
    assert.throws(() => cloudTenantToHostId('00000000-0000-0000-0000-000000000000'));
});

test('maps only the authenticated tenant and validated analytics fields into worker bytes', () => {
    const original = body();
    const result = prepareAnalyticsWorkerPayload(original, identity, now);
    assert.ok(result);
    const value = JSON.parse(result.payload.toString('utf8'));
    assert.deepEqual(Object.keys(value), ['schemaVersion', 'taskId', 'kind', 'tenantId', 'issuedAt', 'expiresAt', 'payload']);
    assert.equal(value.schemaVersion, 'openconnect.v1');
    assert.equal(value.kind, 'analytics.analyze');
    assert.equal(value.tenantId, '00000000000000000000000001');
    assert.equal(value.taskId, '11111111-1111-1111-1111-111111111111');
    assert.equal(value.issuedAt, '2030-01-01T00:00:00.000Z');
    assert.deepEqual(value.payload.values, [1, 2, null, 4]);
    result.payload.fill(0);
    assert.ok(result.payload.every(byte => byte === 0));
    assert.ok(original.some(byte => byte !== 0));
});

test('rejects untrusted tenant injection, invalid identities and expired requests', () => {
    const source = JSON.parse(body().toString('utf8'));
    source.tenantId = 'attacker';
    assert.equal(prepareAnalyticsWorkerPayload(Buffer.from(JSON.stringify(source)), identity, now), null);
    assert.equal(prepareAnalyticsWorkerPayload(body(), { ...identity, tenantId: 'invalid' }, now), null);
    assert.equal(prepareAnalyticsWorkerPayload(body(), { ...identity, applicationId: 'invalid' }, now), null);
    assert.equal(prepareAnalyticsWorkerPayload(body(), identity, now + 60000), null);
});
