import { test } from 'node:test';
import assert from 'node:assert/strict';
import { inferenceExecutionFromEnvironment } from './inference-execution-configuration.mjs';

const config = { CLOUD_INFERENCE_EXECUTION_ENABLED: 'true', CLOUD_INFERENCE_ORIGIN: 'https://worker',
    CLOUD_INFERENCE_CA_FILE: '/secrets/ca', CLOUD_INFERENCE_CERT_FILE: '/secrets/cert', CLOUD_INFERENCE_KEY_FILE: '/secrets/key',
    CLOUD_INFERENCE_TOKEN_FILE: '/secrets/token', CLOUD_INFERENCE_DEPLOYMENT_ID: '11111111-1111-1111-1111-111111111111',
    CLOUD_INFERENCE_ENDPOINT_REFERENCE: 'worker', CLOUD_INFERENCE_REGION: 'region', CLOUD_INFERENCE_CONTRACT_VERSION: 'openconnect.v1',
    CLOUD_INFERENCE_MODEL_REVISION: 'revision', CLOUD_INFERENCE_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64),
    CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'key', CLOUD_FINGERPRINT_KEYS_FILE: '/secrets/fingerprint' };
const read = path => path.endsWith('fingerprint') ? Buffer.from('key:' + Buffer.alloc(32, 1).toString('base64') + '\n') : Buffer.from('fixture');
test('inference execution is opt-in and requires tokenizer, authority, deployment, TLS and fingerprint material', () => {
    assert.equal(inferenceExecutionFromEnvironment({}, undefined, undefined), undefined);
    const tokenizer = { quote() {}, ready() {} };
    assert.throws(() => inferenceExecutionFromEnvironment(config, undefined, tokenizer, read));
    assert.throws(() => inferenceExecutionFromEnvironment(config, {}, undefined, read));
    for (const change of [{ CLOUD_INFERENCE_ORIGIN: 'http://worker' }, { CLOUD_INFERENCE_DEPLOYMENT_ID: 'invalid' },
        { CLOUD_INFERENCE_MODEL_DIGEST: 'invalid' }, { CLOUD_INFERENCE_KEY_FILE: 'relative' }, { CLOUD_INFERENCE_REGION: 'not a region' }])
        assert.throws(() => inferenceExecutionFromEnvironment({ ...config, ...change }, {}, tokenizer, read));
    const execution = inferenceExecutionFromEnvironment(config, {}, tokenizer, read);
    assert.equal(execution.deployment.modelRevision, 'revision');
    execution.keyring.dispose();
});
