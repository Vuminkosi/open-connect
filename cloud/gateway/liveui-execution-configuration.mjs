import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { fingerprintKeyringFromEnvironment } from './configuration.mjs';
import { createLiveUIControlPlaneClient } from './liveui-control-plane.mjs';
import { createLiveUIDispatcher } from './liveui-dispatch.mjs';
import { createLiveUIProbe } from './liveui-probe.mjs';
import { createLiveUIAccessMonitor } from './liveui-access-monitor.mjs';

export function liveUIExecutionFromEnvironment(environment, authorization, read = readFileSync) {
    const enabled = environment.CLOUD_LIVEUI_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || !authorization) throw new Error('Cloud LiveUI configuration is incomplete.');
    const { CLOUD_LIVEUI_ORIGIN: origin, CLOUD_LIVEUI_CA_FILE: caFile, CLOUD_LIVEUI_CERT_FILE: certFile,
        CLOUD_LIVEUI_KEY_FILE: keyFile, CLOUD_LIVEUI_TOKEN_FILE: tokenFile, CLOUD_LIVEUI_DEPLOYMENT_ID: deploymentId,
        CLOUD_LIVEUI_ENDPOINT_REFERENCE: endpointReference, CLOUD_LIVEUI_REGION: region, CLOUD_LIVEUI_CONTRACT_VERSION: contractVersion,
        CLOUD_LIVEUI_MODEL_ID: modelId, CLOUD_LIVEUI_MODEL_REVISION: modelRevision, CLOUD_LIVEUI_MODEL_DIGEST: modelDigest,
        CLOUD_AUTHORITY_ORIGIN: authorityOrigin, CLOUD_AUTHORITY_CA_FILE: authorityCa, CLOUD_AUTHORITY_CERT_FILE: authorityCert,
        CLOUD_AUTHORITY_KEY_FILE: authorityKey } = environment;
    const token = (value, limit) => typeof value === 'string' && value.length > 0 && value.length <= limit && /^[A-Za-z0-9._-]+$/.test(value);
    if (![origin, caFile, certFile, keyFile, tokenFile, deploymentId, modelId, modelRevision, modelDigest,
        authorityOrigin, authorityCa, authorityCert, authorityKey].every(Boolean) ||
        ![caFile, certFile, keyFile, tokenFile, authorityCa, authorityCert, authorityKey].every(isAbsolute) ||
        !token(endpointReference, 128) || !token(region, 80) || !token(contractVersion, 32) ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId) || !modelRevision.trim() || modelRevision.length > 256 ||
        !/^sha256:[0-9a-f]{64}$/.test(modelDigest)) throw new Error('Cloud LiveUI configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read);
    if (!keyring) throw new Error('Cloud fingerprint key configuration is required.');
    try {
        const options = { origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile),
            workerToken: read(tokenFile).toString('utf8').replace(/\r?\n$/, '') };
        return Object.freeze({ keyring, modelId, probe: createLiveUIProbe({ ...options, deploymentId }),
            dispatcher: createLiveUIDispatcher(options), createAccessMonitor: createLiveUIAccessMonitor,
            controlPlane: createLiveUIControlPlaneClient({ origin: authorityOrigin, ca: read(authorityCa),
                certificate: read(authorityCert), privateKey: read(authorityKey) }),
            deployment: Object.freeze({ endpointReference, region, contractVersion, modelRevision, modelDigest }) });
    } catch (error) { keyring.dispose(); throw error; }
}
