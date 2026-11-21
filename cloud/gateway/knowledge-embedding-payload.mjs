import { cloudTenantToHostId } from './worker-payload.mjs';
import { parseKnowledgeEmbeddingRequest } from './knowledge-embedding-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const maxBytes = 128 * 1024;

// Preparation does not authorize, inspect, reserve or dispatch. The owner must clear the returned buffer.
export function prepareKnowledgeEmbeddingPayload(body, identity, now = Date.now()) {
    if (!(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > maxBytes ||
        !identity || typeof identity.applicationId !== 'string' || !guid.test(identity.applicationId) || !Number.isSafeInteger(now)) return null;
    let snapshot; let payload;
    try {
        snapshot = Buffer.from(body);
        const request = parseKnowledgeEmbeddingRequest(snapshot, now);
        const tenantId = cloudTenantToHostId(identity.tenantId);
        payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: request.requestId,
            kind: 'knowledge.embed', tenantId, issuedAt: new Date(now).toISOString(),
            expiresAt: request.expiresAt, payload: request.input }), 'utf8');
        if (payload.length > maxBytes) { payload.fill(0); return null; }
        return { payload, requestId: request.requestId, tenantId, expiresAt: request.expiresAt };
    } catch { payload?.fill(0); return null; }
    finally { snapshot?.fill(0); }
}
