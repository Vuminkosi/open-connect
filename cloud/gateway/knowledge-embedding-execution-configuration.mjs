import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { fingerprintKeyringFromEnvironment } from './configuration.mjs';
import { createKnowledgeEmbeddingControlPlane } from './knowledge-embedding-control-plane.mjs';
import { createKnowledgeEmbeddingDispatcher } from './knowledge-embedding-dispatch.mjs';
import { createKnowledgeEmbeddingProbe } from './knowledge-embedding-probe.mjs';
import { createKnowledgeEmbeddingAccessMonitor } from './knowledge-embedding-access-monitor.mjs';
import { reserveKnowledgeEmbeddingRequest } from './knowledge-embedding-quota.mjs';

const token = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && /^[A-Za-z0-9_.-]+$/.test(value);

export function knowledgeEmbeddingExecutionFromEnvironment(environment, authorization, read = readFileSync) {
    const enabled = environment.CLOUD_KNOWLEDGE_EMBEDDING_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || typeof authorization?.authorize !== 'function') throw new Error('Knowledge embedding execution configuration is incomplete.');
    const { CLOUD_AUTHORITY_ORIGIN: authority, CLOUD_AUTHORITY_CA_FILE: authorityCa, CLOUD_AUTHORITY_CERT_FILE: authorityCert,
        CLOUD_AUTHORITY_KEY_FILE: authorityKey, CLOUD_KNOWLEDGE_EMBEDDING_ORIGIN: origin, CLOUD_KNOWLEDGE_EMBEDDING_CA_FILE: ca,
        CLOUD_KNOWLEDGE_EMBEDDING_CERT_FILE: cert, CLOUD_KNOWLEDGE_EMBEDDING_KEY_FILE: key, CLOUD_KNOWLEDGE_EMBEDDING_TOKEN_FILE: tokenFile,
        CLOUD_KNOWLEDGE_EMBEDDING_DEPLOYMENT_ID: deploymentId, CLOUD_KNOWLEDGE_EMBEDDING_ENDPOINT_REFERENCE: endpointReference,
        CLOUD_KNOWLEDGE_EMBEDDING_REGION: region, CLOUD_KNOWLEDGE_EMBEDDING_CONTRACT_VERSION: contractVersion,
        CLOUD_KNOWLEDGE_EMBEDDING_MODEL_REVISION: modelRevision, CLOUD_KNOWLEDGE_EMBEDDING_MODEL_DIGEST: modelDigest } = environment;
    if (![authority, authorityCa, authorityCert, authorityKey, origin, ca, cert, key, tokenFile, deploymentId].every(Boolean) ||
        ![authorityCa, authorityCert, authorityKey, ca, cert, key, tokenFile].every(isAbsolute) ||
        !token(endpointReference, 128) || !token(region, 80) || !token(contractVersion, 32) ||
        typeof modelRevision !== 'string' || !modelRevision.trim() || modelRevision.length > 256 || !/^sha256:[0-9a-f]{64}$/.test(modelDigest ?? ''))
        throw new Error('Knowledge embedding execution configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read, 131072);
    if (!keyring) throw new Error('Knowledge embedding fingerprint key configuration is required.');
    try {
        const quota = createKnowledgeEmbeddingControlPlane({ origin: authority, ca: read(authorityCa), certificate: read(authorityCert), privateKey: read(authorityKey) });
        const worker = { origin, ca: read(ca), certificate: read(cert), privateKey: read(key), workerToken: read(tokenFile).toString('utf8').replace(/\r?\n$/, '') };
        const dispatcher = createKnowledgeEmbeddingDispatcher(worker);
        const probe = createKnowledgeEmbeddingProbe({ ...worker, deploymentId });
        const deployment = Object.freeze({ endpointReference, region, contractVersion, modelRevision, modelDigest });
        const controlPlane = Object.freeze({ ...quota, authorize: (...args) => authorization.authorize(...args) });
        const reserve = request => reserveKnowledgeEmbeddingRequest(request, { controlPlane, keyring });
        return Object.freeze({ keyring, dispatcher, probe, deployment, controlPlane, reserve, monitorFactory: createKnowledgeEmbeddingAccessMonitor });
    } catch (error) { keyring.dispose(); throw error; }
}
