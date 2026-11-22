import { test } from 'node:test';
import assert from 'node:assert/strict';
import { knowledgeEmbeddingExecutionFromEnvironment } from './knowledge-embedding-execution-configuration.mjs';

test('knowledge embedding execution is opt-in and requires complete private deployment configuration', () => {
    assert.equal(knowledgeEmbeddingExecutionFromEnvironment({}, {}), undefined);
    assert.throws(() => knowledgeEmbeddingExecutionFromEnvironment({ CLOUD_KNOWLEDGE_EMBEDDING_EXECUTION_ENABLED: 'true' }, {}));
    const base = process.platform === 'win32' ? 'C:\\secrets\\' : '/secrets/';
    const env = { CLOUD_KNOWLEDGE_EMBEDDING_EXECUTION_ENABLED: 'true', CLOUD_AUTHORITY_ORIGIN: 'https://authority.example',
        CLOUD_AUTHORITY_CA_FILE: base + 'ca', CLOUD_AUTHORITY_CERT_FILE: base + 'cert', CLOUD_AUTHORITY_KEY_FILE: base + 'key',
        CLOUD_KNOWLEDGE_EMBEDDING_ORIGIN: 'https://worker.example', CLOUD_KNOWLEDGE_EMBEDDING_CA_FILE: base + 'ca', CLOUD_KNOWLEDGE_EMBEDDING_CERT_FILE: base + 'cert',
        CLOUD_KNOWLEDGE_EMBEDDING_KEY_FILE: base + 'key', CLOUD_KNOWLEDGE_EMBEDDING_TOKEN_FILE: base + 'token',
        CLOUD_KNOWLEDGE_EMBEDDING_DEPLOYMENT_ID: '11111111-1111-1111-1111-111111111111', CLOUD_KNOWLEDGE_EMBEDDING_ENDPOINT_REFERENCE: 'analytics',
        CLOUD_KNOWLEDGE_EMBEDDING_REGION: 'region-a', CLOUD_KNOWLEDGE_EMBEDDING_CONTRACT_VERSION: 'openconnect.v1', CLOUD_KNOWLEDGE_EMBEDDING_MODEL_REVISION: 'rev-1',
        CLOUD_KNOWLEDGE_EMBEDDING_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64), CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'active', CLOUD_FINGERPRINT_KEYS_FILE: base + 'fingerprints' };
    const read = path => Buffer.from(path.endsWith('fingerprints') ? `active:${Buffer.alloc(32, 1).toString('base64')}\n` : path.endsWith('token') ? 'secret' : 'material');
    const execution = knowledgeEmbeddingExecutionFromEnvironment(env, { authorize: async () => ({ authorized: false }) }, read);
    assert.equal(typeof execution.probe.probe, 'function');
    assert.equal(typeof execution.controlPlane.claimEmbedding, 'function');
    execution.keyring.dispose();
    assert.throws(() => knowledgeEmbeddingExecutionFromEnvironment({ ...env, CLOUD_KNOWLEDGE_EMBEDDING_DEPLOYMENT_ID: 'invalid' }, { authorize() {} }, read));
});
