import { test } from 'node:test';
import assert from 'node:assert/strict';
import { authorizationFromEnvironment } from './configuration.mjs';

test('authorization is disabled unless all private configuration is supplied', () => {
    assert.equal(authorizationFromEnvironment({}), undefined);
    assert.throws(() => authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example' }));
    assert.throws(() => authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example', CLOUD_AUTHORITY_CA_FILE: 'relative', CLOUD_AUTHORITY_CERT_FILE: '/cert', CLOUD_AUTHORITY_KEY_FILE: '/key' }));
});

test('configured client reads only the supplied credential files', () => {
    const paths = [];
    const result = authorizationFromEnvironment({ CLOUD_AUTHORITY_ORIGIN: 'https://authority.example', CLOUD_AUTHORITY_CA_FILE: '/ca', CLOUD_AUTHORITY_CERT_FILE: '/cert', CLOUD_AUTHORITY_KEY_FILE: '/key' }, path => { paths.push(path); return 'test'; });
    assert.equal(typeof result.authorize, 'function');
    assert.deepEqual(paths, ['/ca', '/cert', '/key']);
});
