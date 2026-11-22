import { reserveAnalyticsRequest } from './analytics-quota.mjs';
import { parseAnalyticsRequest } from './analytics-request.mjs';
import { bindAnalyticsWorkerPayload, prepareAnalyticsWorkerPayload } from './analytics-payload.mjs';
import { validateAnalyticsResponse } from './analytics-response.mjs';
import { createAnalyticsAccessMonitor } from './analytics-access-monitor.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'unavailable' });

// Caller must authenticate, select a deployment, and enforce the request-body boundary first.
// Ambiguous operations leave the reservation held; this function never retries them.
export async function executeAnalytics({ credential, identity, body, deployment, signal, traceparent },
    { controlPlane, keyring, dispatcher, clock = Date.now, monitorFactory = createAnalyticsAccessMonitor }) {
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 262144 ||
        !identity || !deployment || !controlPlane || !keyring || !dispatcher || typeof clock !== 'function') return unavailable();
    let snapshot; let prepared; let binding; let monitor; let response; let responseSnapshot;
    try {
        snapshot = Buffer.from(body);
        const request = parseAnalyticsRequest(snapshot, clock());
        const stopped = () => signal?.aborted || clock() >= Date.parse(request.expiresAt);
        prepared = prepareAnalyticsWorkerPayload(snapshot, identity, clock());
        if (!prepared || stopped()) return unavailable();
        binding = bindAnalyticsWorkerPayload(prepared.payload);
        const reservation = await reserveAnalyticsRequest({ credential, identity, body: snapshot, signal, traceparent }, { controlPlane, keyring, clock });
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || typeof reservation.reservationId !== 'string' || !guid.test(reservation.reservationId))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const proof = { requestId: request.requestId, fingerprintKeyId: reservation.fingerprintKeyId, fingerprint: reservation.fingerprint };
        const privacy = await controlPlane.inspectAnalytics(credential, { ...proof, body: snapshot }, signal, traceparent);
        if (stopped()) return unavailable();
        if (privacy?.status !== 'allowed') return { status: privacy?.status === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        const claim = await controlPlane.claimAnalytics(credential, reservation.reservationId, {
            fingerprintKeyId: proof.fingerprintKeyId, fingerprint: proof.fingerprint,
            workerPayloadFingerprint: binding.fingerprint.toString('base64'), deployment,
        }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || typeof claim.claimId !== 'string' || !guid.test(claim.claimId)) return unavailable();
        monitor = monitorFactory({ controlPlane, credential, identity, deployment, signal, traceparent });
        if (!await monitor.start() || stopped()) return unavailable();
        response = await dispatcher.executeClaimed(prepared, { reservationId: reservation.reservationId, claimId: claim.claimId, key: binding.key }, monitor.signal, traceparent);
        if (stopped() || monitor.signal.aborted || response?.status !== 'received' || !(response.bytes instanceof Uint8Array)) return unavailable();
        responseSnapshot = Buffer.from(response.bytes);
        const validated = validateAnalyticsResponse(responseSnapshot, request);
        if (!validated || validated.computeMilliseconds === null || !await monitor.recheck() || stopped()) return unavailable();
        const outputPrivacy = await controlPlane.inspectAnalyticsResponse(credential, { ...proof, body: responseSnapshot }, monitor.signal, traceparent);
        if (stopped() || monitor.signal.aborted || outputPrivacy?.status !== 'allowed')
            return { status: outputPrivacy?.status === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        if (!await monitor.recheck() || stopped()) return unavailable();
        const completion = await controlPlane.completeAnalytics(credential, reservation.reservationId, claim.claimId,
            validated.computeMilliseconds, monitor.signal, traceparent);
        if (stopped() || monitor.signal.aborted || !['completed', 'already_completed'].includes(completion?.status) || !await monitor.recheck() || stopped()) return unavailable();
        return { status: 'completed', result: validated.result, usage: { computeMilliseconds: validated.computeMilliseconds } };
    } catch { return unavailable(); }
    finally {
        try { monitor?.dispose(); } catch { /* Continue clearing sensitive buffers. */ }
        snapshot?.fill(0); prepared?.payload.fill(0); binding?.key.fill(0); binding?.fingerprint.fill(0); responseSnapshot?.fill(0);
        response?.bytes?.fill?.(0);
    }
}
