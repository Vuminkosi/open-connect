import { reserveClassificationRequest } from './classification-quota.mjs';
import { prepareClassificationWorkerPayload } from './classification-dispatch.mjs';
import { bindClassificationPayload } from './classification-payload.mjs';
import { createClassificationAccessMonitor } from './classification-access-monitor.mjs';

const unavailable = () => ({ status: 'unavailable' });
const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

// Invoke only inside the validated-body boundary after identity, deployment and privacy checks.
// Any ambiguous operation leaves the reservation held; no operation here is retried.
export async function executeClassification({ credential, identity, value, body, deployment, signal, traceparent }, { controlPlane, keyring, dispatcher }) {
    const expiry = Date.parse(value?.expiresAt);
    const stopped = () => signal?.aborted || !Number.isFinite(expiry) || Date.now() >= expiry;
    if (stopped() || !controlPlane || !keyring || !dispatcher || !deployment) return unavailable();
    const prepared = prepareClassificationWorkerPayload(value);
    if (!prepared) return unavailable();
    let binding; let monitor;
    try {
        binding = bindClassificationPayload(prepared.payload);
        const reservation = await reserveClassificationRequest({ credential, identity, value, body, signal, traceparent }, { controlPlane, keyring });
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || typeof reservation.reservationId !== 'string' || !guid.test(reservation.reservationId))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const claim = await controlPlane.claimClassification(credential, reservation.reservationId, {
            fingerprintKeyId: reservation.fingerprintKeyId, fingerprint: reservation.fingerprint,
            workerPayloadFingerprint: binding.fingerprint.toString('base64'), deployment,
        }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || typeof claim.claimId !== 'string' || !guid.test(claim.claimId)) return unavailable();
        monitor = createClassificationAccessMonitor({ controlPlane, credential, identity, signal, traceparent });
        if (!await monitor.start() || stopped()) return unavailable();
        const result = await dispatcher.executeClaimed(prepared, { reservationId: reservation.reservationId, claimId: claim.claimId, key: binding.key }, monitor.signal, traceparent);
        if (stopped() || monitor.signal.aborted || result?.status !== 'completed' || !result.result || !await monitor.recheck()) return unavailable();
        const completion = await controlPlane.completeClassification(credential, reservation.reservationId, claim.claimId, monitor.signal, traceparent);
        if (stopped() || monitor.signal.aborted || !['completed', 'already_completed'].includes(completion?.status) || !await monitor.recheck()) return unavailable();
        return { status: 'completed', result: result.result };
    } catch { return unavailable(); }
    finally { monitor?.dispose(); prepared.payload.fill(0); binding?.key.fill(0); binding?.fingerprint.fill(0); }
}
