import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { parseVoiceSynthesisRequest } from './voice-synthesis-request.mjs';
import { prepareVoiceSynthesisWorkerPayload } from './voice-synthesis-worker-contract.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'unavailable' });

// Admission binds inspected input, character quota and the exact worker payload before dispatch.
export async function withVoiceSynthesisAdmission({ credential, identity, body, deployment, modelId, signal, traceparent },
    { controlPlane, keyring }, executeClaimed) {
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 65536 ||
        !identity || !controlPlane || !keyring || !deployment || typeof modelId !== 'string' ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId) || typeof executeClaimed !== 'function') return unavailable();
    let raw, payload, key, proof, requestFingerprint;
    try {
        raw = Buffer.from(body);
        const request = parseVoiceSynthesisRequest(raw, Date.now());
        const stopped = () => signal?.aborted || Date.now() >= Date.parse(request.expiresAt);
        const lookup = await controlPlane.findFingerprintKey(credential, request.requestId, signal, traceparent);
        if (stopped()) return unavailable();
        const fingerprintKeyId = keyring.selectKeyId(lookup);
        const expiresAt = lookup.status === 'found' ? lookup.expiresAt : request.expiresAt;
        if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt)) || Date.parse(expiresAt) <= Date.now()) return unavailable();
        requestFingerprint = keyring.compute(fingerprintKeyId, { tenantId: identity.tenantId, applicationId: identity.applicationId,
            capability: 'voice.synthesize', method: 'POST', route: '/cloud/v1/voice/synthesis', mediaType: 'application/json', body: raw });
        const fingerprint = requestFingerprint.toString('base64');
        payload = prepareVoiceSynthesisWorkerPayload(request)?.payload;
        if (!payload) return unavailable();
        key = randomBytes(32);
        proof = createHmac('sha256', key).update(payload).digest();
        const inspection = await controlPlane.inspect(credential, request, fingerprintKeyId, fingerprint, signal, traceparent);
        if (stopped()) return unavailable();
        if (inspection?.allowed !== true || inspection.code !== 'allowed') return { status: inspection?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        const characters = [...request.input.text].length;
        if (inspection.characters !== characters) return unavailable();
        const reservation = await controlPlane.reserve(credential, { requestId: request.requestId, fingerprintKeyId, fingerprint, characters, expiresAt }, signal, traceparent);
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || !guid.test(reservation.reservationId ?? ''))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const claim = await controlPlane.claim(credential, reservation.reservationId, { fingerprintKeyId, fingerprint,
            workerPayloadFingerprint: proof.toString('base64'), modelId, deployment }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || !guid.test(claim.claimId ?? '')) return unavailable();
        const actualProof = createHmac('sha256', key).update(payload).digest();
        try { if (!timingSafeEqual(actualProof, proof)) return unavailable(); } finally { actualProof.fill(0); }
        const result = await executeClaimed({ request, payload, key, reservationId: reservation.reservationId, claimId: claim.claimId, signal, traceparent });
        if (stopped()) {
            if (result?.result?.audio instanceof Uint8Array) result.result.audio.fill(0);
            return unavailable();
        }
        return result;
    } catch { return unavailable(); }
    finally { raw?.fill(0); payload?.fill(0); key?.fill(0); proof?.fill(0); requestFingerprint?.fill(0); }
}
