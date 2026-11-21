import { test } from 'node:test';
import assert from 'node:assert/strict';
import { authorizationFromEnvironment, classificationProbeFromEnvironment, classificationExecutionFromEnvironment, fingerprintKeyringFromEnvironment } from './configuration.mjs';

test('fingerprint keys load only from an absolute secret file and support rotation', () => {
    assert.equal(fingerprintKeyringFromEnvironment({}), undefined);
    assert.throws(() => fingerprintKeyringFromEnvironment({ CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active' }));
    assert.throws(() => fingerprintKeyringFromEnvironment({ CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: 'relative' }));
    const path = process.platform === 'win32' ? 'C:\\secrets\\keys' : '/secrets/keys';
    const env = { CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: path };
    const bytes = Buffer.from(`active:${Buffer.alloc(32, 1).toString('base64')}\nold:${Buffer.alloc(32, 2).toString('base64')}\n`);
    const ring = fingerprintKeyringFromEnvironment(env, value => { assert.equal(value, path); return bytes; });
    assert.equal(ring.selectKeyId({ status: 'not_found' }), 'active');
    assert.equal(ring.selectKeyId({ status: 'found', keyId: 'old' }), 'old');
    assert.ok(bytes.every(x => x === 0));
    ring.dispose();
    for (const contents of ['active:not-base64!', `active:${Buffer.alloc(31).toString('base64')}`, `active:${Buffer.alloc(32).toString('base64')}\nactive:${Buffer.alloc(32).toString('base64')}`])
        assert.throws(() => fingerprintKeyringFromEnvironment(env, () => Buffer.from(contents)));
});

test('fingerprint configuration preserves the default limit and permits a bounded larger deployment', () => {
    const path = process.platform === 'win32' ? 'C:\\secrets\\keys' : '/secrets/keys';
    const env = { CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: path };
    const read = () => Buffer.from(`active:${Buffer.alloc(32, 1).toString('base64')}\n`);
    const input = { tenantId: '11111111-1111-1111-1111-111111111111', applicationId: '22222222-2222-2222-2222-222222222222',
        capability: 'analytics.analyze', method: 'POST', route: '/cloud/v1/analytics', mediaType: 'application/json', body: Buffer.alloc(65537) };
    const standard = fingerprintKeyringFromEnvironment(env, read);
    const expanded = fingerprintKeyringFromEnvironment(env, read, 262144);
    try {
        assert.throws(() => standard.compute('active', input));
        assert.equal(expanded.compute('active', input).length, 32);
        assert.throws(() => fingerprintKeyringFromEnvironment(env, read, 262145));
    } finally { standard.dispose(); expanded.dispose(); }
});

test('authorization is disabled unless all private configuration is supplied', () => {
    assert.equal(authorizationFromEnvironment({}), undefined);
    assert.throws(() => authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example' }));
    assert.throws(() => authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example', CLOUD_AUTHORITY_CA_FILE: 'relative', CLOUD_AUTHORITY_CERT_FILE: '/cert', CLOUD_AUTHORITY_KEY_FILE: '/key' }));
});

test('classification probe requires complete deployment-owned file configuration', () => {
    assert.equal(classificationProbeFromEnvironment({}), undefined);
    assert.throws(() => classificationProbeFromEnvironment({ CLOUD_CLASSIFICATION_ORIGIN: 'https://worker.example' }));
    const env = { CLOUD_CLASSIFICATION_ORIGIN: 'https://worker.example', CLOUD_CLASSIFICATION_CA_FILE: '/ca',
        CLOUD_CLASSIFICATION_CERT_FILE: '/cert', CLOUD_CLASSIFICATION_KEY_FILE: '/key',
        CLOUD_CLASSIFICATION_TOKEN_FILE: '/token', CLOUD_CLASSIFICATION_MODEL_REVISION: 'v1', CLOUD_CLASSIFICATION_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64),
        CLOUD_CLASSIFICATION_ENDPOINT_REFERENCE: 'classification-a', CLOUD_CLASSIFICATION_REGION: 'region-a', CLOUD_CLASSIFICATION_CONTRACT_VERSION: 'openconnect.v1' };
    const paths = [];
    const probe = classificationProbeFromEnvironment(env, path => { paths.push(path); return Buffer.from(path === '/token' ? 'secret\n' : 'material'); });
    assert.equal(typeof probe.probe, 'function');
    assert.deepEqual(probe.deployment, { endpointReference: 'classification-a', region: 'region-a', contractVersion: 'openconnect.v1', modelRevision: 'v1', modelDigest: 'sha256:' + 'a'.repeat(64) });
    assert.deepEqual(paths, ['/token', '/ca', '/cert', '/key']);
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_TOKEN_FILE: 'relative' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_ENDPOINT_REFERENCE: 'https://other.example' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_REGION: '' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_MODEL_DIGEST: '' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_MODEL_DIGEST: 'sha256:invalid' }));
});

test('configured client reads only the supplied credential files', () => {
    const paths = [];
    const result = authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example', CLOUD_AUTHORITY_CA_FILE: '/ca', CLOUD_AUTHORITY_CERT_FILE: '/cert', CLOUD_AUTHORITY_KEY_FILE: '/key' }, path => { paths.push(path); return 'test'; });
    assert.equal(typeof result.authorize, 'function');
    assert.deepEqual(paths, ['/ca', '/cert', '/key']);
});

test('execution is opt-in and requires authority, worker and retained fingerprint keys', () => {
    assert.equal(classificationExecutionFromEnvironment({}, {}, {}), undefined);
    assert.throws(() => classificationExecutionFromEnvironment({ CLOUD_CLASSIFICATION_EXECUTION_ENABLED: 'yes' }, {}, {}));
    assert.throws(() => classificationExecutionFromEnvironment({ CLOUD_CLASSIFICATION_EXECUTION_ENABLED: 'true' }, null, {}));
    const base = process.platform === 'win32' ? 'C:\\secrets\\' : '/secrets/';
    const env = { CLOUD_CLASSIFICATION_EXECUTION_ENABLED: 'true', CLOUD_CLASSIFICATION_ORIGIN: 'https://worker.example',
        CLOUD_CLASSIFICATION_DEPLOYMENT_ID: '11111111-1111-1111-1111-111111111111',
        CLOUD_CLASSIFICATION_CA_FILE: base + 'ca', CLOUD_CLASSIFICATION_CERT_FILE: base + 'cert', CLOUD_CLASSIFICATION_KEY_FILE: base + 'key',
        CLOUD_CLASSIFICATION_TOKEN_FILE: base + 'token', CLOUD_CLASSIFICATION_MODEL_REVISION: 'revision-1', CLOUD_CLASSIFICATION_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64),
        CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: base + 'fingerprints' };
    const read = path => Buffer.from(path.endsWith('fingerprints') ? `active:${Buffer.alloc(32, 1).toString('base64')}\n` : path.endsWith('token') ? 'secret' : 'material');
    const execution = classificationExecutionFromEnvironment(env, {}, {}, read);
    assert.equal(typeof execution.dispatcher.executeClaimed, 'function');
    assert.equal(execution.deploymentId, env.CLOUD_CLASSIFICATION_DEPLOYMENT_ID);
    execution.keyring.dispose();
    assert.throws(() => classificationExecutionFromEnvironment({ ...env, CLOUD_FINGERPRINT_KEYS_FILE: '' }, {}, {}, read));
    assert.throws(() => classificationExecutionFromEnvironment({ ...env, CLOUD_CLASSIFICATION_DEPLOYMENT_ID: '' }, {}, {}, read));
    assert.throws(() => classificationExecutionFromEnvironment({ ...env, CLOUD_CLASSIFICATION_DEPLOYMENT_ID: '00000000-0000-0000-0000-000000000000' }, {}, {}, read));
});
