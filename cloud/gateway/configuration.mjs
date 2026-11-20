import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { createCloudAuthorizationClient } from './control-plane.mjs';
import { createClassificationProbe } from './classification-probe.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { createClassificationDispatcher } from './classification-dispatch.mjs';

// The file contains one key per line as ID:canonical-base64. It is mounted as a secret.
export function fingerprintKeyringFromEnvironment(environment, read = readFileSync) {
    const { CLOUD_FINGERPRINT_ACTIVE_KEY_ID: activeKeyId, CLOUD_FINGERPRINT_KEYS_FILE: path } = environment;
    if (!activeKeyId && !path) return undefined;
    if (!activeKeyId || !path || !isAbsolute(path)) throw new Error('Cloud fingerprint key configuration is incomplete.');
    const material = read(path);
    const keys = [];
    try {
        if (!(material instanceof Uint8Array) || material.byteLength > 4096) throw new Error('Invalid Cloud fingerprint key file.');
        const content = new TextDecoder('utf-8', { fatal: true }).decode(material);
        const lines = content.split(/\r?\n/);
        if (lines.at(-1) === '') lines.pop();
        if (lines.length < 1 || lines.length > 16) throw new Error('Invalid Cloud fingerprint key file.');
        for (const line of lines) {
            const match = /^([A-Za-z0-9_-]{1,64}):([A-Za-z0-9+/]+={0,2})$/.exec(line);
            if (!match) throw new Error('Invalid Cloud fingerprint key file.');
            const key = Buffer.from(match[2], 'base64');
            keys.push({ id: match[1], key });
            if (key.toString('base64') !== match[2]) throw new Error('Invalid Cloud fingerprint key file.');
        }
        return createFingerprintKeyring({ activeKeyId, keys });
    } finally {
        for (const { key } of keys) key.fill(0);
        if (material instanceof Uint8Array) material.fill(0);
    }
}

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

export function classificationExecutionFromEnvironment(environment, authorization, probe, read = readFileSync) {
    const enabled = environment.CLOUD_CLASSIFICATION_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || !authorization || !probe) throw new Error('Cloud classification execution configuration is incomplete.');
    const deploymentId = environment.CLOUD_CLASSIFICATION_DEPLOYMENT_ID;
    if (typeof deploymentId !== 'string' || !/^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(deploymentId))
        throw new Error('Cloud classification deployment identity is required.');
    const { CLOUD_CLASSIFICATION_ORIGIN: origin, CLOUD_CLASSIFICATION_CA_FILE: ca,
        CLOUD_CLASSIFICATION_CERT_FILE: cert, CLOUD_CLASSIFICATION_KEY_FILE: key,
        CLOUD_CLASSIFICATION_TOKEN_FILE: token, CLOUD_CLASSIFICATION_MODEL_REVISION: modelRevision } = environment;
    if (![origin, ca, cert, key, token, modelRevision].every(Boolean) || ![ca, cert, key, token].every(isAbsolute))
        throw new Error('Cloud classification execution configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read);
    if (!keyring) throw new Error('Cloud fingerprint key configuration is required.');
    try {
        const dispatcher = createClassificationDispatcher({ origin, ca: read(ca), certificate: read(cert), privateKey: read(key),
            workerToken: read(token).toString('utf8').replace(/\r?\n$/, ''), modelRevision });
        return Object.freeze({ keyring, dispatcher, deploymentId: deploymentId.toLowerCase() });
    } catch (error) { keyring.dispose(); throw error; }
}
