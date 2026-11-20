import { test } from 'node:test';
import assert from 'node:assert/strict';
import { authorizationFromEnvironment, classificationProbeFromEnvironment } from './configuration.mjs';

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
        CLOUD_CLASSIFICATION_TOKEN_FILE: '/token', CLOUD_CLASSIFICATION_MODEL_REVISION: 'v1',
        CLOUD_CLASSIFICATION_ENDPOINT_REFERENCE: 'classification-a', CLOUD_CLASSIFICATION_REGION: 'region-a', CLOUD_CLASSIFICATION_CONTRACT_VERSION: 'openconnect.v1' };
    const paths = [];
    const probe = classificationProbeFromEnvironment(env, path => { paths.push(path); return Buffer.from(path === '/token' ? 'secret\n' : 'material'); });
    assert.equal(typeof probe.probe, 'function');
    assert.deepEqual(probe.deployment, { endpointReference: 'classification-a', region: 'region-a', contractVersion: 'openconnect.v1', modelRevision: 'v1' });
    assert.deepEqual(paths, ['/token', '/ca', '/cert', '/key']);
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_TOKEN_FILE: 'relative' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_ENDPOINT_REFERENCE: 'https://other.example' }));
    assert.throws(() => classificationProbeFromEnvironment({ ...env, CLOUD_CLASSIFICATION_REGION: '' }));
});

test('configured client reads only the supplied credential files', () => {
    const paths = [];
    const result = authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example', CLOUD_AUTHORITY_CA_FILE: '/ca', CLOUD_AUTHORITY_CERT_FILE: '/cert', CLOUD_AUTHORITY_KEY_FILE: '/key' }, path => { paths.push(path); return 'test'; });
    assert.equal(typeof result.authorize, 'function');
    assert.deepEqual(paths, ['/ca', '/cert', '/key']);
});
