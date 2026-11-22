import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const validId = value => typeof value === 'string' && guid.test(value);
const validCredential = value => typeof value === 'string' && /^[^\s,]{1,160}$/.test(value);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const proof = value => typeof value === 'string' && /^[A-Za-z0-9+/]{43}=$/.test(value) && Buffer.from(value, 'base64').toString('base64') === value;
const token = (value, maximum) => typeof value === 'string' && value.length > 0 && value.length <= maximum && /^[A-Za-z0-9_.-]+$/.test(value);

// Dedicated deployment-owned HTTPS/mTLS client; callers never choose a destination.
export function createAnalyticsQuotaClient({ origin, ca, certificate, privateKey, timeoutMs = 5000 }) {
    const base = new URL(origin);
    if (base.protocol !== 'https:' || base.username || base.password || base.pathname !== '/' || base.search || base.hash ||
        !ca || !certificate || !privateKey || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid analytics quota client configuration.');

    function send(path, method, credential, input, signal, traceparent, parse, unavailable, extraHeaders = {}) {
        if (signal?.aborted) return Promise.resolve(unavailable());
        const body = input === null ? null : input instanceof Uint8Array ? Buffer.from(input) : Buffer.from(JSON.stringify(input));
        return new Promise(resolve => {
            let done = false; let timer; const chunks = [];
            const finish = result => {
                if (done) return;
                done = true; clearTimeout(timer); body?.fill(0);
                for (const chunk of chunks) chunk.fill(0);
                resolve(result);
            };
            try {
                const req = https.request(new URL(path, base), { method, ca, cert: certificate, key: privateKey,
                    rejectUnauthorized: true, signal, headers: { authorization: `Bearer ${credential}`, 'cache-control': 'no-store',
                        ...(body ? { 'content-type': 'application/json', 'content-length': body.length } : {}),
                        ...(isValidTraceparent(traceparent) ? { traceparent } : {}), ...extraHeaders } }, response => {
                    if (response.headers['content-encoding']) { response.destroy(); finish(unavailable()); return; }
                    let size = 0;
                    response.on('data', chunk => {
                        if (done) { chunk.fill(0); return; }
                        size += chunk.length; chunks.push(chunk);
                        if (size > 4096) { response.destroy(); finish(unavailable()); }
                    });
                    response.on('error', () => finish(unavailable()));
                    response.on('aborted', () => finish(unavailable()));
                    response.on('end', () => {
                        let data;
                        try {
                            data = Buffer.concat(chunks);
                            const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(data));
                            finish(parse(response.statusCode, value));
                        } catch { finish(unavailable()); }
                        finally { data?.fill(0); }
                    });
                });
                timer = setTimeout(() => { req.destroy(); finish(unavailable()); }, timeoutMs);
                req.on('error', () => finish(unavailable()));
                if (body) req.end(body, () => body.fill(0));
                else req.end();
            } catch { finish(unavailable()); }
        });
    }

    return Object.freeze({
        completeAnalytics(credential, reservationId, claimId, computeMilliseconds, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            if (!validCredential(credential) || !validId(reservationId) || !validId(claimId) ||
                !Number.isInteger(computeMilliseconds) || computeMilliseconds < 1 || computeMilliseconds > 120000) return Promise.resolve(unavailable());
            return send(`/cloud/internal/v1/analytics/quota/reservations/${reservationId}/claims/${claimId}/completion`,
                'POST', credential, { computeMilliseconds }, signal, traceparent, (status, value) => {
                    if (!object(value) || Object.keys(value).length !== 1 || typeof value.status !== 'string') return unavailable();
                    if (status === 200 && ['completed', 'already_completed'].includes(value.status)) return { status: value.status };
                    if (status === 409 && value.status === 'conflict') return { status: 'conflict' };
                    return unavailable();
                }, unavailable);
        },
        matchAnalyticsDeployment(credential, deployment, signal, traceparent) {
            if (!validCredential(credential) || !object(deployment) || !token(deployment.endpointReference, 128) ||
                !token(deployment.region, 80) || !token(deployment.contractVersion, 32) ||
                typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() || deployment.modelRevision.length > 256 ||
                !/^sha256:[0-9a-f]{64}$/.test(deployment.modelDigest ?? '')) return Promise.resolve(false);
            const request = { endpointReference: deployment.endpointReference, region: deployment.region, contractVersion: deployment.contractVersion,
                modelRevision: deployment.modelRevision, modelDigest: deployment.modelDigest };
            return send('/cloud/internal/v1/analytics/deployment-match', 'POST', credential, request, signal, traceparent,
                (status, value) => status === 200 && object(value) && Object.keys(value).length === 1 && value.matched === true, () => false);
        },
        inspectAnalyticsResponse(credential, input, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            if (!validCredential(credential) || !object(input) || !validId(input.requestId) ||
                !/^[A-Za-z0-9_-]{1,64}$/.test(input.fingerprintKeyId ?? '') || !proof(input.fingerprint) ||
                !(input.body instanceof Uint8Array) || input.body.byteLength < 1 || input.body.byteLength > 2 * 1024 * 1024)
                return Promise.resolve(unavailable());
            try {
                const response = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(input.body));
                if (!object(response) || response.taskId !== input.requestId || response.schemaVersion !== 'openconnect.v1' ||
                    response.kind !== 'analytics.analyze') return Promise.resolve(unavailable());
            } catch { return Promise.resolve(unavailable()); }
            return send('/cloud/internal/v1/analytics/privacy/response', 'POST', credential, input.body, signal, traceparent,
                (status, value) => {
                    if (!object(value) || Object.keys(value).some(key => !['allowed', 'code'].includes(key))) return unavailable();
                    if (status === 200 && value.allowed === true && value.code === 'allowed') return { status: 'allowed' };
                    if (status === 403 && value.allowed === false && value.code === 'privacy_blocked') return { status: 'privacy_blocked' };
                    return unavailable();
                }, unavailable, { 'x-cloud-request-id': input.requestId, 'x-cloud-fingerprint-key-id': input.fingerprintKeyId,
                    'x-cloud-request-fingerprint': input.fingerprint });
        },
        claimAnalytics(credential, reservationId, input, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            const deployment = input?.deployment;
            if (!validCredential(credential) || !validId(reservationId) || !object(input) ||
                !/^[A-Za-z0-9_-]{1,64}$/.test(input.fingerprintKeyId ?? '') || !proof(input.fingerprint) ||
                !proof(input.workerPayloadFingerprint) || !object(deployment) ||
                !token(deployment.endpointReference, 128) || !token(deployment.region, 80) || !token(deployment.contractVersion, 32) ||
                typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() || deployment.modelRevision.length > 256 ||
                !/^sha256:[0-9a-f]{64}$/.test(deployment.modelDigest ?? '')) return Promise.resolve(unavailable());
            const request = { fingerprintKeyId: input.fingerprintKeyId, fingerprint: input.fingerprint,
                workerPayloadFingerprint: input.workerPayloadFingerprint, endpointReference: deployment.endpointReference,
                region: deployment.region, contractVersion: deployment.contractVersion, modelRevision: deployment.modelRevision,
                modelDigest: deployment.modelDigest };
            return send(`/cloud/internal/v1/analytics/quota/reservations/${reservationId}/claim`, 'POST', credential, request, signal, traceparent,
                (status, value) => {
                    if (!object(value) || Object.keys(value).some(key => !['status', 'claimId'].includes(key))) return unavailable();
                    if (status === 200 && value.status === 'claimed' && validId(value.claimId)) return { status: 'claimed', claimId: value.claimId };
                    if (status === 409 && ['already_claimed', 'idempotency_conflict'].includes(value.status)) return { status: value.status };
                    return unavailable();
                }, unavailable);
        },
        inspectAnalytics(credential, input, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            if (!validCredential(credential) || !object(input) || !validId(input.requestId) ||
                !/^[A-Za-z0-9_-]{1,64}$/.test(input.fingerprintKeyId ?? '') || typeof input.fingerprint !== 'string' ||
                !/^[A-Za-z0-9+/]{43}=$/.test(input.fingerprint) || Buffer.from(input.fingerprint, 'base64').toString('base64') !== input.fingerprint ||
                !(input.body instanceof Uint8Array) || input.body.byteLength < 1 || input.body.byteLength > 262144) return Promise.resolve(unavailable());
            try {
                const request = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(input.body));
                if (!object(request) || request.requestId !== input.requestId || request.schemaVersion !== 'openconnect.cloud.analytics/v1' ||
                    request.capability !== 'analytics.analyze') return Promise.resolve(unavailable());
            } catch { return Promise.resolve(unavailable()); }
            return send('/cloud/internal/v1/analytics/privacy', 'POST', credential, input.body, signal, traceparent,
                (status, value) => {
                    if (!object(value) || Object.keys(value).some(key => !['allowed', 'code'].includes(key))) return unavailable();
                    if (status === 200 && value.allowed === true && value.code === 'allowed') return { status: 'allowed' };
                    if (status === 403 && value.allowed === false && value.code === 'privacy_blocked') return { status: 'privacy_blocked' };
                    return unavailable();
                }, unavailable, { 'x-cloud-request-id': input.requestId, 'x-cloud-fingerprint-key-id': input.fingerprintKeyId,
                    'x-cloud-request-fingerprint': input.fingerprint });
        },
        findAnalyticsFingerprintKey(credential, requestId, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            if (!validCredential(credential) || !validId(requestId)) return Promise.resolve(unavailable());
            return send(`/cloud/internal/v1/analytics/quota/fingerprint-key/${requestId}`, 'GET', credential, null, signal, traceparent,
                (status, value) => {
                    if (status !== 200 || !object(value) || Object.keys(value).some(key => !['status', 'keyId', 'expiresAt'].includes(key))) return unavailable();
                    if (value.status === 'found' && typeof value.keyId === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(value.keyId) &&
                        typeof value.expiresAt === 'string' && Number.isFinite(Date.parse(value.expiresAt)))
                        return { status: 'found', keyId: value.keyId, expiresAt: value.expiresAt };
                    if (['not_found', 'legacy_unverifiable'].includes(value.status) && value.keyId == null && value.expiresAt == null)
                        return { status: value.status };
                    return unavailable();
                }, unavailable);
        },
        reserveAnalytics(credential, input, signal, traceparent) {
            const unavailable = () => ({ status: 'quota_unavailable' });
            if (!validCredential(credential) || !object(input) || !validId(input.requestId) ||
                !/^[A-Za-z0-9_-]{1,64}$/.test(input.fingerprintKeyId ?? '') || typeof input.fingerprint !== 'string' ||
                !/^[A-Za-z0-9+/]{43}=$/.test(input.fingerprint) || Buffer.from(input.fingerprint, 'base64').toString('base64') !== input.fingerprint ||
                typeof input.expiresAt !== 'string' || !Number.isFinite(Date.parse(input.expiresAt))) return Promise.resolve(unavailable());
            const request = { requestId: input.requestId, fingerprintKeyId: input.fingerprintKeyId, fingerprint: input.fingerprint, expiresAt: input.expiresAt };
            return send('/cloud/internal/v1/analytics/quota/reservations', 'POST', credential, request, signal, traceparent,
                (status, value) => {
                    if (!object(value) || Object.keys(value).some(key => !['status', 'reservationId'].includes(key))) return unavailable();
                    if (status === 200 && ['reserved', 'duplicate'].includes(value.status) && validId(value.reservationId))
                        return { status: value.status, reservationId: value.reservationId };
                    if (status === 429 && value.status === 'quota_exceeded' && value.reservationId == null) return { status: 'quota_exceeded' };
                    if (status === 409 && value.status === 'idempotency_conflict') return { status: 'idempotency_conflict' };
                    return unavailable();
                }, unavailable);
        },
    });
}
