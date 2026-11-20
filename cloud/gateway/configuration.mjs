import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { createCloudAuthorizationClient } from './control-plane.mjs';

export function authorizationFromEnvironment(environment, read = readFileSync) {
    const { CLOUD_AUTHORITY_ORIGIN: origin, CLOUD_AUTHORITY_CA_FILE: caFile,
        CLOUD_AUTHORITY_CERT_FILE: certFile, CLOUD_AUTHORITY_KEY_FILE: keyFile } = environment;
    const values = [origin, caFile, certFile, keyFile];
    if (values.every(value => !value)) return undefined;
    if (values.some(value => !value) || ![caFile, certFile, keyFile].every(isAbsolute))
        throw new Error('Cloud authority configuration is incomplete.');
    return createCloudAuthorizationClient({ origin, ca: read(caFile), certificate: read(certFile), privateKey: read(keyFile) });
}
