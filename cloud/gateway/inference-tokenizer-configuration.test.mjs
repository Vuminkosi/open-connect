import { test } from 'node:test';
import assert from 'node:assert/strict';
import { inferenceTokenizerFromEnvironment } from './inference-tokenizer-configuration.mjs';

const config = { CLOUD_INFERENCE_TOKENIZER_ENABLED: 'true', CLOUD_INFERENCE_MODEL_PATH: '/models/model.gguf',
    CLOUD_INFERENCE_MODEL_ID: 'model', CLOUD_INFERENCE_MODEL_REVISION: 'revision',
    CLOUD_INFERENCE_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64), CLOUD_INFERENCE_MODEL_FORMAT: 'chatml',
    CLOUD_INFERENCE_MODEL_LICENSE_ACCEPTED: 'true', CLOUD_INFERENCE_MODEL_ARTIFACT_REVIEWED: 'true' };

test('tokenizer is opt-in and requires complete approved model configuration', () => {
    assert.equal(inferenceTokenizerFromEnvironment({}), undefined);
    assert.throws(() => inferenceTokenizerFromEnvironment({ CLOUD_INFERENCE_TOKENIZER_ENABLED: 'yes' }));
    for (const changes of [
        { CLOUD_INFERENCE_MODEL_PATH: 'relative' }, { CLOUD_INFERENCE_MODEL_DIGEST: 'invalid' },
        { CLOUD_INFERENCE_MODEL_LICENSE_ACCEPTED: 'false' }, { CLOUD_INFERENCE_MODEL_ARTIFACT_REVIEWED: undefined },
        { CLOUD_INFERENCE_MODEL_CONTEXT_TOKENS: '512.0' }, { CLOUD_INFERENCE_MODEL_THREADS: '0' },
    ]) assert.throws(() => inferenceTokenizerFromEnvironment({ ...config, ...changes }));
    const tokenizer = inferenceTokenizerFromEnvironment(config);
    assert.equal(typeof tokenizer.quote, 'function');
    tokenizer.dispose();
});
