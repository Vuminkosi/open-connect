import { parseKnowledgeEmbeddingRequest } from './knowledge-embedding-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
// Call only after authentication, deployment matching and privacy inspection.
// Reservation is not authority to dispatch; a worker-enforced claim is also required.
export async function reserveKnowledgeEmbeddingRequest({ credential, identity, body, signal, traceparent }, { controlPlane, keyring, clock = Date.now }) {
    const unavailable = () => ({ status: 'quota_unavailable' });
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 131072 ||
        !identity || !controlPlane || !keyring || typeof clock !== 'function') return unavailable();
    let snapshot; let fingerprint;
    try {
        // A caller cannot change the request while a key lookup is in progress.
        snapshot = Buffer.from(body);
        const request = parseKnowledgeEmbeddingRequest(snapshot, clock());
        const stopped = () => signal?.aborted || clock() >= Date.parse(request.expiresAt);
        const lookup = await controlPlane.findEmbeddingFingerprintKey(credential, request.requestId, signal, traceparent);
        if (stopped()) return unavailable();
        const keyId = keyring.selectKeyId(lookup);
        const expiresAt = lookup.status === 'found' ? lookup.expiresAt : request.expiresAt;
        if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt)) ||
            Date.parse(expiresAt) <= clock() || Date.parse(expiresAt) > Date.parse(request.expiresAt)) return unavailable();
        fingerprint = keyring.compute(keyId, { tenantId: identity.tenantId, applicationId: identity.applicationId,
            capability: 'knowledge.embed', method: 'POST', route: '/cloud/v1/knowledge/embeddings', mediaType: 'application/json', body: snapshot });
        const result = await controlPlane.reserveEmbedding(credential, { requestId: request.requestId,
            fingerprintKeyId: keyId, fingerprint: fingerprint.toString('base64'), expiresAt }, signal, traceparent);
        if (stopped()) return unavailable();
        if (result?.status === 'reserved' && typeof result.reservationId === 'string' && guid.test(result.reservationId))
            return { status: 'reserved', reservationId: result.reservationId, fingerprintKeyId: keyId,
                fingerprint: fingerprint.toString('base64') };
        return ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(result?.status) ? { status: result.status } : unavailable();
    } catch { return unavailable(); }
    finally { snapshot?.fill(0); fingerprint?.fill(0); }
}
