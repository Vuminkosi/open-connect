import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { fingerprintKeyringFromEnvironment } from './configuration.mjs';
import { createAnalyticsQuotaClient } from './analytics-control-plane.mjs';
import { createAnalyticsDispatcher } from './analytics-dispatch.mjs';
import { createAnalyticsProbe } from './analytics-probe.mjs';

const token = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && /^[A-Za-z0-9_.-]+$/.test(value);

export function analyticsExecutionFromEnvironment(environment, authorization, read = readFileSync) {
    const enabled = environment.CLOUD_ANALYTICS_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || typeof authorization?.authorize !== 'function') throw new Error('Analytics execution configuration is incomplete.');
    const { CLOUD_AUTHORITY_ORIGIN: authority, CLOUD_AUTHORITY_CA_FILE: authorityCa, CLOUD_AUTHORITY_CERT_FILE: authorityCert,
        CLOUD_AUTHORITY_KEY_FILE: authorityKey, CLOUD_ANALYTICS_ORIGIN: origin, CLOUD_ANALYTICS_CA_FILE: ca,
        CLOUD_ANALYTICS_CERT_FILE: cert, CLOUD_ANALYTICS_KEY_FILE: key, CLOUD_ANALYTICS_TOKEN_FILE: tokenFile,
        CLOUD_ANALYTICS_DEPLOYMENT_ID: deploymentId, CLOUD_ANALYTICS_ENDPOINT_REFERENCE: endpointReference,
        CLOUD_ANALYTICS_REGION: region, CLOUD_ANALYTICS_CONTRACT_VERSION: contractVersion,
        CLOUD_ANALYTICS_MODEL_REVISION: modelRevision, CLOUD_ANALYTICS_MODEL_DIGEST: modelDigest } = environment;
    if (![authority, authorityCa, authorityCert, authorityKey, origin, ca, cert, key, tokenFile, deploymentId].every(Boolean) ||
        ![authorityCa, authorityCert, authorityKey, ca, cert, key, tokenFile].every(isAbsolute) ||
        !token(endpointReference, 128) || !token(region, 80) || !token(contractVersion, 32) ||
        typeof modelRevision !== 'string' || !modelRevision.trim() || modelRevision.length > 256 || !/^sha256:[0-9a-f]{64}$/.test(modelDigest ?? ''))
        throw new Error('Analytics execution configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read, 262144);
    if (!keyring) throw new Error('Analytics fingerprint key configuration is required.');
    try {
        const quota = createAnalyticsQuotaClient({ origin: authority, ca: read(authorityCa), certificate: read(authorityCert), privateKey: read(authorityKey) });
        const worker = { origin, ca: read(ca), certificate: read(cert), privateKey: read(key), workerToken: read(tokenFile).toString('utf8').replace(/\r?\n$/, '') };
        const dispatcher = createAnalyticsDispatcher(worker);
        const probe = createAnalyticsProbe({ ...worker, deploymentId });
        const deployment = Object.freeze({ endpointReference, region, contractVersion, modelRevision, modelDigest });
        const controlPlane = Object.freeze({ ...quota, authorize: (...args) => authorization.authorize(...args) });
        return Object.freeze({ keyring, dispatcher, probe, deployment, controlPlane });
    } catch (error) { keyring.dispose(); throw error; }
}
