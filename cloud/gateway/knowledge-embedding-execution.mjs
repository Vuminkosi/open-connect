import { bindWorkerPayload } from './worker-payload.mjs';
import { parseKnowledgeEmbeddingRequest } from './knowledge-embedding-request.mjs';
import { prepareKnowledgeEmbeddingPayload } from './knowledge-embedding-payload.mjs';
import { validateKnowledgeEmbeddingResponse } from './knowledge-embedding-response.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'unavailable' });

// The caller authenticates, authorizes the capability and selects a deployment before entry.
// Failed or ambiguous operations leave any reservation to expire; this function never retries them.
export async function executeKnowledgeEmbedding({ credential, identity, body, deployment, signal, traceparent },
    { controlPlane, reserve, dispatcher, monitorFactory, clock = Date.now }) {
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 131072 ||
        typeof credential !== 'string' || !credential || !identity || !deployment || !controlPlane || typeof reserve !== 'function' || !dispatcher ||
        typeof monitorFactory !== 'function' || typeof clock !== 'function') return unavailable();
    let snapshot; let prepared; let binding; let monitor; let response; let responseSnapshot;
    try {
        snapshot = Buffer.from(body);
        const request = parseKnowledgeEmbeddingRequest(snapshot, clock());
        const stopped = () => signal?.aborted || clock() >= Date.parse(request.expiresAt);
        prepared = prepareKnowledgeEmbeddingPayload(snapshot, identity, clock());
        if (!prepared || stopped()) return unavailable();
        binding = bindWorkerPayload(prepared.payload);
        let reservation;
        const reservationBody = Buffer.from(snapshot);
        try { reservation = await reserve({ credential, identity, body: reservationBody, signal, traceparent }); }
        finally { reservationBody.fill(0); }
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || typeof reservation.reservationId !== 'string' || !guid.test(reservation.reservationId))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const proof = { requestId: request.requestId, fingerprintKeyId: reservation.fingerprintKeyId, fingerprint: reservation.fingerprint };
        let privacy;
        const privacyBody = Buffer.from(snapshot);
        try { privacy = await controlPlane.inspectEmbedding(credential, { ...proof, body: privacyBody }, signal, traceparent); }
        finally { privacyBody.fill(0); }
        if (stopped()) return unavailable();
        if (privacy?.status !== 'allowed') return { status: privacy?.status === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        const claim = await controlPlane.claimEmbedding(credential, reservation.reservationId, {
            fingerprintKeyId: proof.fingerprintKeyId, fingerprint: proof.fingerprint,
            workerPayloadFingerprint: binding.fingerprint.toString('base64'), deployment,
        }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || typeof claim.claimId !== 'string' || !guid.test(claim.claimId)) return unavailable();
        monitor = monitorFactory({ controlPlane, credential, identity, deployment, signal, traceparent });
        if (!monitor || !monitor.signal || typeof monitor.signal.aborted !== 'boolean' || !await monitor.start() || monitor.signal.aborted || stopped()) return unavailable();
        response = await dispatcher.executeClaimed(prepared, { reservationId: reservation.reservationId, claimId: claim.claimId, key: binding.key }, monitor.signal, traceparent);
        if (stopped() || monitor.signal?.aborted || response?.status !== 'received' || !(response.bytes instanceof Uint8Array) ||
            response.bytes.byteLength < 1 || response.bytes.byteLength > 8 * 1024 * 1024) return unavailable();
        responseSnapshot = Buffer.from(response.bytes);
        const validated = validateKnowledgeEmbeddingResponse(responseSnapshot, request);
        if (!validated || !await monitor.recheck() || stopped()) return unavailable();
        let outputPrivacy;
        const outputBody = Buffer.from(responseSnapshot);
        try { outputPrivacy = await controlPlane.inspectEmbeddingResponse(credential, { ...proof, body: outputBody }, monitor.signal, traceparent); }
        finally { outputBody.fill(0); }
        if (stopped() || monitor.signal?.aborted || outputPrivacy?.status !== 'allowed')
            return { status: outputPrivacy?.status === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        if (!await monitor.recheck() || stopped()) return unavailable();
        const completion = await controlPlane.completeEmbedding(credential, reservation.reservationId, claim.claimId, monitor.signal, traceparent);
        if (stopped() || monitor.signal?.aborted || !['completed', 'already_completed'].includes(completion?.status) || !await monitor.recheck() || stopped()) return unavailable();
        return { status: 'completed', result: validated.result };
    } catch { return unavailable(); }
    finally {
        try { monitor?.dispose?.(); } catch { /* Cleanup must not expose diagnostics or skip buffer clearing. */ }
        snapshot?.fill(0); prepared?.payload.fill(0); binding?.key.fill(0); binding?.fingerprint.fill(0);
        responseSnapshot?.fill(0); response?.bytes?.fill?.(0);
    }
}
