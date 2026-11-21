import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { fingerprintKeyringFromEnvironment } from './configuration.mjs';
import { createVoiceSynthesisControlPlaneClient } from './voice-synthesis-control-plane.mjs';
import { createVoiceSynthesisDispatcher } from './voice-synthesis-dispatch.mjs';
import { createVoiceSynthesisProbe } from './voice-synthesis-probe.mjs';
import { createVoiceSynthesisAccessMonitor } from './voice-synthesis-access-monitor.mjs';
import { createVoiceAudioTranscriber } from './voice-audio-transcriber.mjs';
import { createVoiceOutputScreen } from './voice-output-screen.mjs';

export function voiceSynthesisFromEnvironment(environment, authorization, read = readFileSync) {
    const enabled = environment.CLOUD_VOICE_EXECUTION_ENABLED ?? 'false';
    if (enabled === 'false') return undefined;
    if (enabled !== 'true' || !authorization) throw new Error('Cloud voice configuration is incomplete.');
    const { CLOUD_VOICE_ORIGIN: origin, CLOUD_VOICE_CA_FILE: ca, CLOUD_VOICE_CERT_FILE: cert, CLOUD_VOICE_KEY_FILE: key,
        CLOUD_VOICE_TOKEN_FILE: tokenFile, CLOUD_VOICE_DEPLOYMENT_ID: deploymentId, CLOUD_VOICE_MODEL_ID: modelId,
        CLOUD_VOICE_ENDPOINT_REFERENCE: endpointReference, CLOUD_VOICE_REGION: region, CLOUD_VOICE_CONTRACT_VERSION: contractVersion,
        CLOUD_VOICE_MODEL_REVISION: modelRevision, CLOUD_VOICE_MODEL_DIGEST: modelDigest,
        CLOUD_AUTHORITY_ORIGIN: authority, CLOUD_AUTHORITY_CA_FILE: authorityCa, CLOUD_AUTHORITY_CERT_FILE: authorityCert,
        CLOUD_AUTHORITY_KEY_FILE: authorityKey, CLOUD_VOICE_SCREEN_ORIGIN: screenOrigin, CLOUD_VOICE_SCREEN_CA_FILE: screenCa,
        CLOUD_VOICE_SCREEN_CERT_FILE: screenCert, CLOUD_VOICE_SCREEN_KEY_FILE: screenKey, CLOUD_VOICE_SCREEN_TOKEN_FILE: screenToken,
        CLOUD_VOICE_SCREEN_MODEL_ID: screenModel, CLOUD_VOICE_SCREEN_MODEL_REVISION: screenRevision,
        CLOUD_VOICE_SCREEN_MODEL_FINGERPRINT: screenFingerprint } = environment;
    const paths = [ca, cert, key, tokenFile, authorityCa, authorityCert, authorityKey, screenCa, screenCert, screenKey, screenToken];
    const simple = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && /^[A-Za-z0-9._-]+$/.test(value);
    if (![origin, deploymentId, modelId, modelRevision, modelDigest, authority, screenOrigin, screenModel, screenRevision, screenFingerprint].every(Boolean) ||
        !paths.every(path => path && isAbsolute(path)) || !simple(endpointReference, 128) || !simple(region, 80) || !simple(contractVersion, 32) ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId) || !modelRevision.trim() || modelRevision.length > 256 ||
        !/^sha256:[0-9a-f]{64}$/.test(modelDigest)) throw new Error('Cloud voice configuration is incomplete.');
    const keyring = fingerprintKeyringFromEnvironment(environment, read);
    if (!keyring) throw new Error('Cloud fingerprint key configuration is required.');
    try {
        const voiceOptions = { origin, ca: read(ca), certificate: read(cert), privateKey: read(key),
            workerToken: read(tokenFile).toString('utf8').replace(/\r?\n$/, '') };
        const controlPlane = createVoiceSynthesisControlPlaneClient({ origin: authority, ca: read(authorityCa),
            certificate: read(authorityCert), privateKey: read(authorityKey) });
        const transcriber = createVoiceAudioTranscriber({ origin: screenOrigin, ca: read(screenCa), certificate: read(screenCert), privateKey: read(screenKey),
            workerToken: read(screenToken).toString('utf8').replace(/\r?\n$/, ''), modelId: screenModel, modelRevision: screenRevision,
            modelFingerprint: screenFingerprint });
        return Object.freeze({ keyring, modelId, controlPlane, dispatcher: createVoiceSynthesisDispatcher(voiceOptions),
            probe: createVoiceSynthesisProbe({ ...voiceOptions, deploymentId }), createAccessMonitor: createVoiceSynthesisAccessMonitor,
            outputScreen: createVoiceOutputScreen({ transcriber, controlPlane }),
            deployment: Object.freeze({ endpointReference, region, contractVersion, modelRevision, modelDigest }) });
    } catch (error) { keyring.dispose(); throw error; }
}
