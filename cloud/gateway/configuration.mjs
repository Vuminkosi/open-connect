import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { createCloudAuthorizationClient } from './control-plane.mjs';
import { createClassificationProbe } from './classification-probe.mjs';

export function authorizationFromEnvironment(environment, read = readFileSync) {
    const { CLOUD_AUTHORITY_ORIGIN: origin, CLOUD_AUTHORITY_CA_FILE: caFile,
        CLOUD_AUTHORITY_CERT_FILE: certFile, CLOUD_AUTHORITY_KEY_FILE: keyFile } = environment;
    const values = [origin, caFile, certFile, keyFile];
    if (values.every(value => !value)) return undefined;
    if (values.some(value => !value) || ![caFile, certFile, keyFile].every(isAbsolute))
        throw new Error('Cloud authority configuration is incomplete.');
    return createCloudAuthorizationClient({ origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile) });
}

export function classificationProbeFromEnvironment(environment, read = readFileSync) {
    const { CLOUD_CLASSIFICATION_ORIGIN: origin, CLOUD_CLASSIFICATION_CA_FILE: caFile,
        CLOUD_CLASSIFICATION_CERT_FILE: certFile, CLOUD_CLASSIFICATION_KEY_FILE: keyFile,
        CLOUD_CLASSIFICATION_TOKEN_FILE: tokenFile, CLOUD_CLASSIFICATION_MODEL_REVISION: modelRevision,
        CLOUD_CLASSIFICATION_ENDPOINT_REFERENCE: endpointReference, CLOUD_CLASSIFICATION_REGION: region,
        CLOUD_CLASSIFICATION_CONTRACT_VERSION: contractVersion } = environment;
    const values = [origin, caFile, certFile, keyFile, tokenFile, modelRevision, endpointReference, region, contractVersion];
    if (values.every(value => !value)) return undefined;
    if (values.some(value => !value) || ![caFile, certFile, keyFile, tokenFile].every(isAbsolute))
        throw new Error('Cloud classification probe configuration is incomplete.');
    const validToken = (value, limit) => typeof value === 'string' && value.length > 0 && value.length <= limit && /^[A-Za-z0-9._-]+$/.test(value);
    if (!validToken(endpointReference, 128) || !validToken(region, 80) || !validToken(contractVersion, 32))
        throw new Error('Cloud classification deployment configuration is invalid.');
    const token = read(tokenFile).toString('utf8').replace(/\r?\n$/, '');
    const probe = createClassificationProbe({ origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile), workerToken: token, modelRevision });
    return Object.freeze({ ...probe, deployment: Object.freeze({ endpointReference, region, contractVersion, modelRevision }) });
}
