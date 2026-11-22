import { test } from 'node:test';
import assert from 'node:assert/strict';
import { analyticsExecutionFromEnvironment } from './analytics-execution-configuration.mjs';

test('analytics execution is opt-in and requires complete private deployment configuration', () => {
    assert.equal(analyticsExecutionFromEnvironment({}, {}), undefined);
    assert.throws(() => analyticsExecutionFromEnvironment({ CLOUD_ANALYTICS_EXECUTION_ENABLED: 'true' }, {}));
    const base = process.platform === 'win32' ? 'C:\\secrets\\' : '/secrets/';
    const env = { CLOUD_ANALYTICS_EXECUTION_ENABLED: 'true', CLOUD_AUTHORITY_ORIGIN: 'https://authority.example',
        CLOUD_AUTHORITY_CA_FILE: base + 'ca', CLOUD_AUTHORITY_CERT_FILE: base + 'cert', CLOUD_AUTHORITY_KEY_FILE: base + 'key',
        CLOUD_ANALYTICS_ORIGIN: 'https://worker.example', CLOUD_ANALYTICS_CA_FILE: base + 'ca', CLOUD_ANALYTICS_CERT_FILE: base + 'cert',
        CLOUD_ANALYTICS_KEY_FILE: base + 'key', CLOUD_ANALYTICS_TOKEN_FILE: base + 'token',
        CLOUD_ANALYTICS_DEPLOYMENT_ID: '11111111-1111-1111-1111-111111111111', CLOUD_ANALYTICS_ENDPOINT_REFERENCE: 'analytics',
        CLOUD_ANALYTICS_REGION: 'region-a', CLOUD_ANALYTICS_CONTRACT_VERSION: 'openconnect.v1', CLOUD_ANALYTICS_MODEL_REVISION: 'rev-1',
        CLOUD_ANALYTICS_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64), CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: base + 'fingerprints' };
    const read = path => Buffer.from(path.endsWith('fingerprints') ? `active:${Buffer.alloc(32, 1).toString('base64')}\n` : path.endsWith('token') ? 'secret' : 'material');
    const execution = analyticsExecutionFromEnvironment(env, { authorize: async () => ({ authorized: false }) }, read);
    assert.equal(typeof execution.probe.probe, 'function');
    assert.equal(typeof execution.controlPlane.claimAnalytics, 'function');
    execution.keyring.dispose();
    assert.throws(() => analyticsExecutionFromEnvironment({ ...env, CLOUD_ANALYTICS_DEPLOYMENT_ID: 'invalid' }, { authorize() {} }, read));
});
