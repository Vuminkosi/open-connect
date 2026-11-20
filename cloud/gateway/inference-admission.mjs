import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { parseUniqueJson } from './classification-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, names) => object(value) && Object.keys(value).every(key => names.includes(key));
const unavailable = () => ({ status: 'unavailable' });

export function parseInferenceRequest(body, now) {
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(body));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'modelId', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.inference/v1' || !guid.test(value.requestId ?? '') ||
        value.capability !== 'inference.chat' || typeof value.modelId !== 'string' ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(value.modelId) ||
        typeof value.expiresAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value.expiresAt) ||
        !Number.isFinite(Date.parse(value.expiresAt)) || new Date(value.expiresAt).toISOString() !== value.expiresAt ||
        Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000 ||
        !only(value.input, ['messages', 'maxTokens', 'stream']) || !Array.isArray(value.input.messages) ||
        value.input.messages.length < 1 || value.input.messages.length > 64 ||
        !Number.isInteger(value.input.maxTokens) || value.input.maxTokens < 1 || value.input.maxTokens > 4096 ||
        typeof value.input.stream !== 'boolean') throw new TypeError('Invalid inference request.');
    let characters = 0;
    const messages = value.input.messages.map(item => {
        if (!only(item, ['role', 'content']) || !['system', 'user', 'assistant', 'tool'].includes(item.role) ||
            typeof item.content !== 'string' || item.content.length > 32768) throw new TypeError('Invalid inference message.');
        characters += item.content.length;
        return Object.freeze({ role: item.role, content: item.content });
    });
    if (characters < 1 || characters > 65536) throw new TypeError('Invalid inference content.');
    return Object.freeze({ requestId: value.requestId, expiresAt: value.expiresAt, modelId: value.modelId,
        input: Object.freeze({ messages: Object.freeze(messages), maxTokens: value.input.maxTokens, stream: value.input.stream }) });
}

// The callback owns dispatch and settlement.
// The token quote must use a locally verified tokenizer for the selected model and exact payload.
// It must not contact a worker or provider or execute a model.
export async function withInferenceAdmission({ credential, identity, body, deployment, signal, traceparent },
    { controlPlane, keyring, quote }, executeClaimed) {
    if (signal?.aborted || !(body instanceof Uint8Array) || body.byteLength < 1 || body.byteLength > 65536 ||
        !identity || !controlPlane || !keyring || !deployment || typeof quote !== 'function' || typeof executeClaimed !== 'function') return unavailable();
    let raw, workerPayload, quotePayload, quoteKey, key, proof, requestFingerprint;
    try {
        raw = Buffer.from(body);
        const request = parseInferenceRequest(raw, Date.now());
        const stopped = () => signal?.aborted || Date.now() >= Date.parse(request.expiresAt);
        const lookup = await controlPlane.findInferenceFingerprintKey(credential, request.requestId, signal, traceparent);
        if (stopped()) return unavailable();
        const fingerprintKeyId = keyring.selectKeyId(lookup);
        const expiresAt = lookup.status === 'found' ? lookup.expiresAt : request.expiresAt;
        if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt)) || Date.parse(expiresAt) <= Date.now()) return unavailable();
        requestFingerprint = keyring.compute(fingerprintKeyId, { tenantId: identity.tenantId, applicationId: identity.applicationId,
            capability: 'inference.chat', method: 'POST', route: '/cloud/v1/inference/chat', mediaType: 'application/json', body: raw });
        const fingerprint = requestFingerprint.toString('base64');
        workerPayload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.inference.worker/v1', requestId: request.requestId,
            modelId: request.modelId, input: request.input }), 'utf8');
        if (workerPayload.length > 131072) return unavailable();
        key = randomBytes(32);
        proof = createHmac('sha256', key).update(workerPayload).digest();
        const inspection = await controlPlane.inspectInference(credential, request.requestId, request.input.messages, fingerprintKeyId, fingerprint, signal, traceparent);
        if (stopped()) return unavailable();
        if (inspection?.allowed !== true) return { status: inspection?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
        quotePayload = Buffer.from(workerPayload);
        quoteKey = Buffer.from(key);
        const quoted = await quote({ payload: quotePayload, key: quoteKey, deployment, signal, traceparent });
        quotePayload.fill(0); quotePayload = undefined; quoteKey.fill(0); quoteKey = undefined;
        if (stopped() || !quoted || quoted.requestId !== request.requestId || quoted.modelId !== request.modelId ||
            quoted.workerPayloadFingerprint !== proof.toString('base64') || quoted.maxOutputTokens !== request.input.maxTokens ||
            !Number.isSafeInteger(quoted.inputTokenUpperBound) || quoted.inputTokenUpperBound < 0 ||
            !Number.isSafeInteger(quoted.inputTokenUpperBound + quoted.maxOutputTokens)) return unavailable();
        const reservedTokens = quoted.inputTokenUpperBound + quoted.maxOutputTokens;
        const reservation = await controlPlane.reserveInference(credential, { requestId: request.requestId, fingerprintKeyId,
            fingerprint, expiresAt, reservedTokens }, signal, traceparent);
        if (stopped()) return unavailable();
        if (reservation?.status !== 'reserved' || !guid.test(reservation.reservationId ?? ''))
            return { status: ['duplicate', 'quota_exceeded', 'idempotency_conflict'].includes(reservation?.status) ? reservation.status : 'unavailable' };
        const claim = await controlPlane.claimInference(credential, reservation.reservationId, { fingerprintKeyId, fingerprint,
            workerPayloadFingerprint: proof.toString('base64'), modelId: request.modelId, deployment }, signal, traceparent);
        if (stopped() || claim?.status !== 'claimed' || !guid.test(claim.claimId ?? '')) return unavailable();
        const actualProof = createHmac('sha256', key).update(workerPayload).digest();
        try { if (!timingSafeEqual(actualProof, proof)) return unavailable(); } finally { actualProof.fill(0); }
        const result = await executeClaimed({ payload: workerPayload, key, reservationId: reservation.reservationId, claimId: claim.claimId,
            modelId: request.modelId, reservedTokens, signal, traceparent });
        return stopped() ? unavailable() : result;
    } catch { return unavailable(); }
    finally { raw?.fill(0); quotePayload?.fill(0); quoteKey?.fill(0); workerPayload?.fill(0); key?.fill(0); proof?.fill(0); requestFingerprint?.fill(0); }
}
