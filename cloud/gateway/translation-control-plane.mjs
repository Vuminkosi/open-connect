import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { parseTranslationRequest } from './translation-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const id = value => typeof value === 'string' && guid.test(value);
const keyId = value => typeof value === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(value);
const fingerprint = value => typeof value === 'string' && /^[A-Za-z0-9+/]{43}=$/.test(value) && Buffer.from(value, 'base64').toString('base64') === value;
const only = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(key => keys.includes(key));

export function createTranslationControlPlaneClient({ origin, ca, certificate, privateKey, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid translation control-plane configuration.');
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
        async matchDeployment(credential, deployment, input, signal, traceparent) {
            const token = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && /^[A-Za-z0-9._-]+$/.test(value);
            const language = value => typeof value === 'string' && /^[a-z]{3}_[A-Z][a-z]{3}$/.test(value);
            if (!deployment || !id(deployment.deploymentId) || !token(deployment.endpointReference, 128) || !token(deployment.region, 80) ||
                !token(deployment.contractVersion, 32) || typeof deployment.modelId !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(deployment.modelId) ||
                typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() || deployment.modelRevision.length > 256 ||
                !/^sha256:[0-9a-f]{64}$/.test(deployment.modelDigest ?? '') || !/^sha256:[0-9a-f]{64}$/.test(deployment.imageDigest ?? '') ||
                !/^[0-9a-f]{64}$/.test(deployment.tokenizerHash ?? '') || !input || !language(input.sourceLang) || !language(input.targetLang) ||
                input.sourceLang === input.targetLang) return false;
            const body = { deploymentId: deployment.deploymentId, endpointReference: deployment.endpointReference, region: deployment.region,
                contractVersion: deployment.contractVersion, modelId: deployment.modelId, modelRevision: deployment.modelRevision,
                modelDigest: deployment.modelDigest, imageDigest: deployment.imageDigest, tokenizerHash: deployment.tokenizerHash,
                sourceLang: input.sourceLang, targetLang: input.targetLang };
            const result = await call('POST', '/cloud/internal/v1/translation/deployment-match', credential, body, {}, signal, traceparent);
            return result?.status === 200 && only(result.value, ['matched']) && result.value.matched === true;
        },
        async findFingerprintKey(credential, requestId, signal, traceparent) {
            if (!id(requestId)) return { status: 'unavailable' };
            const result = await call('GET', `/cloud/internal/v1/translation/quota/fingerprint-key/${requestId}`, credential, undefined, {}, signal, traceparent);
            const value = result?.value;
            if (result?.status !== 200 || !only(value, ['status', 'keyId', 'expiresAt'])) return { status: 'unavailable' };
            if (value.status === 'found' && keyId(value.keyId) && typeof value.expiresAt === 'string' && Number.isFinite(Date.parse(value.expiresAt))) return value;
            if (['not_found', 'legacy_unverifiable'].includes(value.status) && value.keyId == null && value.expiresAt == null) return { status: value.status };
            return { status: 'unavailable' };
        },
        async inspect(credential, request, fingerprintKeyId, requestFingerprint, signal, traceparent) {
            if (!keyId(fingerprintKeyId) || !fingerprint(requestFingerprint)) return { allowed: false, code: 'capability_unavailable' };
            let raw; let validated;
            try { raw = Buffer.from(JSON.stringify(request)); validated = parseTranslationRequest(raw); }
            catch { return { allowed: false, code: 'capability_unavailable' }; }
            finally { raw?.fill(0); }
            const result = await call('POST', '/cloud/internal/v1/translation/privacy', credential, validated.input,
                { 'x-cloud-request-id': validated.requestId, 'x-cloud-fingerprint-key-id': fingerprintKeyId, 'x-cloud-request-fingerprint': requestFingerprint }, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['allowed', 'code', 'characters']) || value.characters !== [...validated.input.text].length) return { allowed: false, code: 'capability_unavailable' };
            if (result.status === 200 && value.allowed === true && value.code === 'allowed') return { allowed: true, code: 'allowed', characters: value.characters };
            if (result.status === 403 && value.allowed === false && value.code === 'privacy_blocked') return { allowed: false, code: 'privacy_blocked' };
            return { allowed: false, code: 'capability_unavailable' };
        },
        async reserve(credential, input, signal, traceparent) {
            if (!input || !id(input.requestId) || !keyId(input.fingerprintKeyId) || !fingerprint(input.fingerprint) ||
                !Number.isInteger(input.characters) || input.characters < 1 || input.characters > 5000 ||
                typeof input.expiresAt !== 'string' || !Number.isFinite(Date.parse(input.expiresAt))) return { status: 'quota_unavailable' };
            const body = { requestId: input.requestId, fingerprintKeyId: input.fingerprintKeyId, fingerprint: input.fingerprint,
                characters: input.characters, expiresAt: input.expiresAt };
            const result = await call('POST', '/cloud/internal/v1/translation/quota/reservations', credential, body, {}, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['status', 'reservationId'])) return { status: 'quota_unavailable' };
            if (result.status === 200 && ['reserved', 'duplicate'].includes(value.status) && id(value.reservationId)) return value;
            if (result.status === 429 && value.status === 'quota_exceeded' && value.reservationId == null) return { status: value.status };
            if (result.status === 409 && value.status === 'idempotency_conflict' && (value.reservationId == null || id(value.reservationId))) return { status: value.status };
            return { status: 'quota_unavailable' };
        },
        async claim(credential, reservationId, proof, signal, traceparent) {
            const deployment = proof?.deployment; const input = proof?.input;
            const token = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && /^[A-Za-z0-9._-]+$/.test(value);
            const language = value => typeof value === 'string' && /^[a-z]{3}_[A-Z][a-z]{3}$/.test(value);
            if (!id(reservationId) || !proof || !keyId(proof.fingerprintKeyId) || !fingerprint(proof.fingerprint) ||
                !fingerprint(proof.workerPayloadFingerprint) || !deployment || !id(deployment.deploymentId) ||
                !token(deployment.endpointReference, 128) || !token(deployment.region, 80) || !token(deployment.contractVersion, 32) ||
                typeof deployment.modelId !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(deployment.modelId) ||
                proof.modelId !== deployment.modelId || typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() ||
                deployment.modelRevision.length > 256 || !/^sha256:[0-9a-f]{64}$/.test(deployment.modelDigest ?? '') ||
                !/^sha256:[0-9a-f]{64}$/.test(deployment.imageDigest ?? '') || !/^[0-9a-f]{64}$/.test(deployment.tokenizerHash ?? '') ||
                !input || !language(input.sourceLang) || !language(input.targetLang) || input.sourceLang === input.targetLang) return { status: 'unavailable' };
            const body = { fingerprintKeyId: proof.fingerprintKeyId, fingerprint: proof.fingerprint, workerPayloadFingerprint: proof.workerPayloadFingerprint,
                deployment: { deploymentId: deployment.deploymentId, endpointReference: deployment.endpointReference, region: deployment.region,
                    contractVersion: deployment.contractVersion, modelId: deployment.modelId, modelRevision: deployment.modelRevision,
                    modelDigest: deployment.modelDigest, imageDigest: deployment.imageDigest, tokenizerHash: deployment.tokenizerHash,
                    sourceLang: input.sourceLang, targetLang: input.targetLang } };
            const result = await call('POST', `/cloud/internal/v1/translation/quota/reservations/${reservationId}/claim`, credential, body, {}, signal, traceparent);
            const value = result?.value;
            if (!only(value, ['status', 'claimId'])) return { status: 'unavailable' };
            if (result.status === 200 && value.status === 'claimed' && id(value.claimId)) return value;
            if (result.status === 409 && ['already_claimed', 'idempotency_conflict'].includes(value.status) && value.claimId == null) return { status: value.status };
            return { status: 'unavailable' };
        },
    });
}
