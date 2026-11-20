import { createLocalInferenceTokenizer } from './inference-tokenizer.mjs';

// Only the opt-in gateway deployment can select a pre-provisioned model.
export function inferenceTokenizerFromEnvironment(environment) {
    const enabled = environment.CLOUD_INFERENCE_TOKENIZER_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true') throw new Error('Invalid Cloud inference tokenizer configuration.');
    const integer = (name, fallback) => {
        const value = environment[name] ?? fallback;
        return typeof value === 'string' && /^[1-9][0-9]*$/.test(value) ? Number(value) : NaN;
    };
    return createLocalInferenceTokenizer({ python: '/app/workers/inference/.venv/bin/python',
        pythonPath: '/app/workers/inference/src', model: {
            path: environment.CLOUD_INFERENCE_MODEL_PATH,
            id: environment.CLOUD_INFERENCE_MODEL_ID,
            revision: environment.CLOUD_INFERENCE_MODEL_REVISION,
            digest: environment.CLOUD_INFERENCE_MODEL_DIGEST,
            format: environment.CLOUD_INFERENCE_MODEL_FORMAT,
            licenseAccepted: environment.CLOUD_INFERENCE_MODEL_LICENSE_ACCEPTED === 'true',
            artifactReviewed: environment.CLOUD_INFERENCE_MODEL_ARTIFACT_REVIEWED === 'true',
            contextTokens: integer('CLOUD_INFERENCE_MODEL_CONTEXT_TOKENS', '4096'),
            threads: integer('CLOUD_INFERENCE_MODEL_THREADS', '2'),
        } });
}
