import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { liveUIInspectionFields, parseLiveUIRequest } from './liveui-contract.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const unavailable = () => ({ status: 'unavailable' });

// Admission binds the exact inspected request and worker payload before dispatch.
export async function withLiveUIAdmission({ credential, identity, body, deployment, modelId, signal, traceparent },
    { controlPlane, authorization, keyring }, executeClaimed) {
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 131072 ||
        !identity || !controlPlane || !authorization || !keyring || !deployment || typeof modelId !== 'string' ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId) || typeof executeClaimed !== 'function') return unavailable();
    let raw, payload, key, proof, requestFingerprint;
    try {
        raw = Buffer.from(body);
        const request = parseLiveUIRequest(raw, Date.now());
        const stopped = () => signal?.aborted || Date.now() >= Date.parse(request.expiresAt);
        const lookup = await controlPlane.findFingerprintKey(credential, request.requestId, signal, traceparent);
        if (stopped()) return unavailable();
        const fingerprintKeyId = keyring.selectKeyId(lookup);
        const expiresAt = lookup.status === 'found' ? lookup.expiresAt : request.expiresAt;
        if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt)) || Date.parse(expiresAt) <= Date.now()) return unavailable();
        requestFingerprint = keyring.compute(fingerprintKeyId, { tenantId: identity.tenantId, applicationId: identity.applicationId,
            capability: 'inference.liveui', method: 'POST', route: '/cloud/v1/liveui/plan', mediaType: 'application/json', body: raw });
        const fingerprint = requestFingerprint.toString('base64');
        payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.liveui.worker/v1', requestId: request.requestId,
            modelId, input: request.input }), 'utf8');
        if (payload.length > 131072) return unavailable();
        key = randomBytes(32);
        proof = createHmac('sha256', key).update(payload).digest();
        const inspection = await authorization.inspectLiveUI(credential, request.requestId, liveUIInspectionFields(request), fingerprintKeyId, fingerprint, signal, traceparent);
        if (stopped()) return unavailable();
        if (inspection?.allowed !== true) return { status: inspection?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        const reservation = await controlPlane.reserve(credential, { requestId: request.requestId, fingerprintKeyId, fingerprint, expiresAt }, signal, traceparent);
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || !guid.test(reservation.reservationId ?? ''))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const claim = await controlPlane.claim(credential, reservation.reservationId, { fingerprintKeyId, fingerprint,
            workerPayloadFingerprint: proof.toString('base64'), modelId, deployment }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || !guid.test(claim.claimId ?? '')) return unavailable();
        const actualProof = createHmac('sha256', key).update(payload).digest();
        try { if (!timingSafeEqual(actualProof, proof)) return unavailable(); } finally { actualProof.fill(0); }
        const result = await executeClaimed({ request, payload, key, reservationId: reservation.reservationId, claimId: claim.claimId, signal, traceparent });
        return stopped() ? unavailable() : result;
    } catch { return unavailable(); }
    finally { raw?.fill(0); payload?.fill(0); key?.fill(0); proof?.fill(0); requestFingerprint?.fill(0); }
}

