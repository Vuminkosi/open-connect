import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const id = value => typeof value === 'string' && guid.test(value);
const keyId = value => typeof value === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(value);
const token = (value, limit) => typeof value === 'string' && value.length > 0 && value.length <= limit && /^[A-Za-z0-9._-]+$/.test(value);
const fingerprint = value => typeof value === 'string' && /^[A-Za-z0-9+/]{43}=$/.test(value) && Buffer.from(value, 'base64').toString('base64') === value;
const only = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(key => keys.includes(key));

export function createVoiceSynthesisControlPlaneClient({ origin, ca, certificate, privateKey, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid voice control-plane configuration.');
    async function call(method, path, credential, payload, headers, signal, traceparent) {
        if (typeof credential !== 'string' || !/^[^\s,]{1,160}$/.test(credential) || signal?.aborted) return null;
        const body = payload === undefined ? null : Buffer.from(JSON.stringify(payload));
        if (body && body.length > 65536) { body.fill(0); return null; }
        return new Promise(resolve => {
            let done = false; let timer; let clearResponse = () => {};
            const finish = value => { if (!done) { done = true; clearTimeout(timer); body?.fill(0); clearResponse(); resolve(value); } };
            try {
                const request = https.request(new URL(path, url), { method, ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                    headers: { authorization: `Bearer ${credential}`, 'cache-control': 'no-store', ...headers,
                        ...(body ? { 'content-type': 'application/json', 'content-length': body.length } : {}),
                        ...(isValidTraceparent(traceparent) ? { traceparent } : {}) } }, response => {
                    if (response.headers['content-encoding']) { response.destroy(); finish(null); return; }
                    const chunks = []; let size = 0;
                    const clear = () => { for (const chunk of chunks) chunk.fill(0); };
                    clearResponse = clear;
                    response.on('data', chunk => {
                        if (done) { chunk.fill(0); return; }
                        size += chunk.length; chunks.push(chunk); if (size > 8192) { response.destroy(); finish(null); }
                    });
                    response.on('error', () => finish(null)); response.on('aborted', () => finish(null));
                    response.on('end', () => {
                        let bytes;
                        try { bytes = Buffer.concat(chunks); finish({ status: response.statusCode, value: parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) }); }
                        catch { finish(null); } finally { bytes?.fill(0); clear(); }
                    });
                });
                timer = setTimeout(() => { request.destroy(); finish(null); }, timeoutMs);
                request.on('error', () => finish(null));
                if (body) request.end(body, () => body.fill(0)); else request.end();
            } catch { finish(null); }
        });
    }
    return Object.freeze({
        async findFingerprintKey(credential, requestId, signal, traceparent) {
            if (!id(requestId)) return { status: 'unavailable' };
            const result = await call('GET', `/cloud/internal/v1/voice-synthesis/quota/fingerprint-key/${requestId}`, credential, undefined, {}, signal, traceparent);
            const value = result?.value;
            if (result?.status !== 200 || !only(value, ['status', 'keyId', 'expiresAt'])) return { status: 'unavailable' };
            if (value.status === 'found' && keyId(value.keyId) && typeof value.expiresAt === 'string' && Number.isFinite(Date.parse(value.expiresAt))) return value;
            if (['not_found', 'legacy_unverifiable'].includes(value.status) && value.keyId == null && value.expiresAt == null) return { status: value.status };
            return { status: 'unavailable' };
        },
        async inspect(credential, request, fingerprintKeyId, requestFingerprint, signal, traceparent) {
            if (!id(request?.requestId) || !keyId(fingerprintKeyId) || !fingerprint(requestFingerprint) ||
                !only(request.input, ['text', 'voiceId', 'locale', 'format']) || typeof request.input.text !== 'string' ||
                !request.input.text.isWellFormed() || !request.input.text.trim() || [...request.input.text].length > 2000 ||
                request.input.voiceId !== 'af_heart' || typeof request.input.locale !== 'string' || !request.input.locale.isWellFormed() ||
                [...request.input.locale].length < 1 || [...request.input.locale].length > 16 || !['wav', 'pcm'].includes(request.input.format))
                return { allowed: false, code: 'capability_unavailable' };
            const result = await call('POST', '/cloud/internal/v1/voice-synthesis/privacy', credential, request.input,
                { 'x-cloud-request-id': request.requestId, 'x-cloud-fingerprint-key-id': fingerprintKeyId, 'x-cloud-request-fingerprint': requestFingerprint }, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['allowed', 'code', 'characters']) || value.characters !== [...request.input.text].length) return { allowed: false, code: 'capability_unavailable' };
            if (result.status === 200 && value.allowed === true && value.code === 'allowed') return { allowed: true, code: 'allowed', characters: value.characters };
            if (result.status === 403 && value.allowed === false && value.code === 'privacy_blocked') return { allowed: false, code: 'privacy_blocked' };
            return { allowed: false, code: 'capability_unavailable' };
        },
        async reserve(credential, input, signal, traceparent) {
            if (!input || !id(input.requestId) || !keyId(input.fingerprintKeyId) || !fingerprint(input.fingerprint) ||
                !Number.isInteger(input.characters) || input.characters < 1 || input.characters > 2000 ||
                typeof input.expiresAt !== 'string' || !Number.isFinite(Date.parse(input.expiresAt))) return { status: 'quota_unavailable' };
            const body = { requestId: input.requestId, fingerprintKeyId: input.fingerprintKeyId, fingerprint: input.fingerprint,
                characters: input.characters, expiresAt: input.expiresAt };
            const result = await call('POST', '/cloud/internal/v1/voice-synthesis/quota/reservations', credential, body, {}, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['status', 'reservationId'])) return { status: 'quota_unavailable' };
            if (result.status === 200 && ['reserved', 'duplicate'].includes(value.status) && id(value.reservationId)) return value;
            if (result.status === 429 && value.status === 'quota_exceeded' && value.reservationId == null) return { status: value.status };
            if (result.status === 409 && value.status === 'idempotency_conflict' && (value.reservationId == null || id(value.reservationId))) return { status: value.status };
            return { status: 'quota_unavailable' };
        },
        async claim(credential, reservationId, proof, signal, traceparent) {
            const deployment = proof?.deployment;
            if (!id(reservationId) || !proof || !keyId(proof.fingerprintKeyId) || !fingerprint(proof.fingerprint) ||
                !fingerprint(proof.workerPayloadFingerprint) || typeof proof.modelId !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(proof.modelId) ||
                !deployment || !token(deployment.endpointReference, 128) || !token(deployment.region, 80) || !token(deployment.contractVersion, 32) ||
                typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() || deployment.modelRevision.length > 256 ||
                !/^sha256:[0-9a-f]{64}$/.test(deployment.modelDigest ?? '')) return { status: 'unavailable' };
            const body = { fingerprintKeyId: proof.fingerprintKeyId, fingerprint: proof.fingerprint, workerPayloadFingerprint: proof.workerPayloadFingerprint,
                modelId: proof.modelId, endpointReference: deployment.endpointReference, region: deployment.region,
                contractVersion: deployment.contractVersion, modelRevision: deployment.modelRevision, modelDigest: deployment.modelDigest };
            const result = await call('POST', `/cloud/internal/v1/voice-synthesis/quota/reservations/${reservationId}/claim`, credential, body, {}, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['status', 'claimId'])) return { status: 'unavailable' };
            if (result.status === 200 && value.status === 'claimed' && id(value.claimId)) return value;
            if (result.status === 409 && ['already_claimed', 'idempotency_conflict'].includes(value.status) && value.claimId == null) return { status: value.status };
            return { status: 'unavailable' };
        },
        async verifyClaim(credential, reservationId, claimId, signal, traceparent) {
            if (!id(reservationId) || !id(claimId)) return false;
            const result = await call('GET', `/cloud/internal/v1/voice-synthesis/claims/${reservationId}/${claimId}/access`, credential, undefined, {}, signal, traceparent);
            return result?.status === 200 && only(result.value, ['allowed']) && result.value.allowed === true;
        },
        async inspectOutput(credential, requestId, reservationId, claimId, transcript, modelFingerprint, signal, traceparent) {
            if (!id(requestId) || !id(reservationId) || !id(claimId) || typeof transcript !== 'string' || !transcript.isWellFormed() ||
                !transcript.trim() || transcript.length > 8192 || !/^[0-9a-f]{64}$/.test(modelFingerprint ?? ''))
                return { allowed: false, code: 'capability_unavailable' };
            const result = await call('POST', '/cloud/internal/v1/voice-synthesis/output-privacy', credential, { transcript, modelFingerprint },
                { 'x-cloud-request-id': requestId, 'x-cloud-reservation-id': reservationId, 'x-cloud-claim-id': claimId }, signal, traceparent);
            if (!only(result?.value, ['allowed', 'code'])) return { allowed: false, code: 'capability_unavailable' };
            if (result.status === 200 && result.value.allowed === true && result.value.code === 'allowed') return { allowed: true, code: 'allowed' };
            if (result.status === 403 && result.value.allowed === false && result.value.code === 'privacy_blocked') return { allowed: false, code: 'privacy_blocked' };
            return { allowed: false, code: 'capability_unavailable' };
        },
    });
}
