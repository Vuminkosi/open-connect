import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { fingerprintKeyringFromEnvironment } from './configuration.mjs';
import { createInferenceDispatcher } from './inference-dispatch.mjs';
import { createInferenceProbe } from './inference-probe.mjs';
import { createInferenceAccessMonitor } from './inference-access-monitor.mjs';

export function inferenceExecutionFromEnvironment(environment, authorization, tokenizer, read = readFileSync) {
    const enabled = environment.CLOUD_INFERENCE_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || !authorization || !tokenizer || typeof tokenizer.quote !== 'function' || typeof tokenizer.ready !== 'function')
        throw new Error('Cloud inference execution configuration is incomplete.');
    const { CLOUD_INFERENCE_ORIGIN: origin, CLOUD_INFERENCE_CA_FILE: caFile, CLOUD_INFERENCE_CERT_FILE: certFile,
        CLOUD_INFERENCE_KEY_FILE: keyFile, CLOUD_INFERENCE_TOKEN_FILE: tokenFile, CLOUD_INFERENCE_DEPLOYMENT_ID: deploymentId,
        CLOUD_INFERENCE_ENDPOINT_REFERENCE: endpointReference, CLOUD_INFERENCE_REGION: region,
        CLOUD_INFERENCE_CONTRACT_VERSION: contractVersion, CLOUD_INFERENCE_MODEL_REVISION: modelRevision,
        CLOUD_INFERENCE_MODEL_DIGEST: modelDigest } = environment;
    const token = (value, limit) => typeof value === 'string' && value.length > 0 && value.length <= limit && /^[A-Za-z0-9._-]+$/.test(value);
    if (![origin, caFile, certFile, keyFile, tokenFile, deploymentId, modelRevision, modelDigest].every(Boolean) ||
        ![caFile, certFile, keyFile, tokenFile].every(isAbsolute) || !token(endpointReference, 128) || !token(region, 80) ||
        !token(contractVersion, 32) || typeof modelRevision !== 'string' || !modelRevision.trim() || modelRevision.length > 256 ||
        !/^sha256:[0-9a-f]{64}$/.test(modelDigest)) throw new Error('Cloud inference execution configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read);
    if (!keyring) throw new Error('Cloud fingerprint key configuration is required.');
    try {
        const options = { origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile),
            workerToken: read(tokenFile).toString('utf8').replace(/\r?\n$/, '') };
        const probe = createInferenceProbe({ ...options, deploymentId });
        const dispatcher = createInferenceDispatcher(options);
        return Object.freeze({ keyring, dispatcher, probe, quote: tokenizer.quote, tokenizer, createAccessMonitor: createInferenceAccessMonitor,
            deployment: Object.freeze({ endpointReference, region, contractVersion, modelRevision, modelDigest }) });
    } catch (error) { keyring.dispose(); throw error; }
}
