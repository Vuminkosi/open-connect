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
        CLOUD_CLASSIFICATION_TOKEN_FILE: tokenFile, CLOUD_CLASSIFICATION_MODEL_REVISION: modelRevision } = environment;
    const values = [origin, caFile, certFile, keyFile, tokenFile, modelRevision];
    if (values.every(value => !value)) return undefined;
    if (values.some(value => !value) || ![caFile, certFile, keyFile, tokenFile].every(isAbsolute))
        throw new Error('Cloud classification probe configuration is incomplete.');
    const token = read(tokenFile).toString('utf8').replace(/\r?\n$/, '');
    return createClassificationProbe({ origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile), workerToken: token, modelRevision });
}
