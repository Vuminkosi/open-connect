import https from 'node:https';
import { isValidTraceparent } from './trace-context.mjs';

const denied = () => ({ authorized: false, code: 'capability_unavailable' });
const safeCodes = new Set(['unauthorized', 'forbidden', 'invalid_request', 'capability_unavailable',
    'capability_not_entitled', 'capability_disabled', 'dependency_not_enabled']);
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const validId = value => typeof value === 'string' && guid.test(value) && value !== '00000000-0000-0000-0000-000000000000';

// Configuration is deployment-owned; callers never choose a destination or certificates.
export function createCloudAuthorizationClient({ origin, ca, certificate, privateKey, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid Cloud authorization client configuration.');
    const endpoint = new URL('/cloud/internal/v1/authorize', url);
    return {
        authorize(credential, capability, signal, traceparent) {
            if (typeof credential !== 'string' || credential.length < 1 || credential.length > 160 ||
                typeof capability !== 'string' || capability.length < 1 || capability.length > 80 || signal?.aborted)
                return Promise.resolve(denied());
            const body = Buffer.from(JSON.stringify({ credential, capability }), 'utf8');
            if (body.length > 4096) return Promise.resolve(denied());
            return new Promise(resolve => {
                let finished = false;
                let timer;
                const finish = value => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(endpoint, {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true,
                        headers: { 'content-type': 'application/json', 'content-length': body.length, 'cache-control': 'no-store',
                            ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                        signal,
                    }, response => {
                        const chunks = [];
                        let size = 0;
                        response.on('data', chunk => {
                            size += chunk.length;
                            if (size > 8192) { response.destroy(); finish(denied()); }
                            else chunks.push(chunk);
                        });
                        response.on('error', () => finish(denied()));
                        response.on('aborted', () => finish(denied()));
                        response.on('end', () => {
                            try {
                                const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                                if (response.statusCode === 200 && value.authorized === true && value.code === 'authorized' &&
                                    value.identity && validId(value.identity.credentialId) && validId(value.identity.tenantId) &&
                                    validId(value.identity.applicationId) && ['Development', 'Staging', 'Production'].includes(value.identity.environment))
                                    return finish(value);
                                if ([400, 401, 403, 503].includes(response.statusCode) && value.authorized === false &&
                                    value.identity == null && safeCodes.has(value.code)) return finish({ authorized: false, code: value.code });
                            } catch { /* Do not expose remote diagnostics. */ }
                            finish(denied());
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(denied()); }, timeoutMs);
                    request.on('error', () => { body.fill(0); finish(denied()); });
                    request.end(body, () => body.fill(0));
                } catch { body.fill(0); finish(denied()); }
            });
        },
        findClassificationFingerprintKey(credential, requestId, signal, traceparent) {
            const unavailable = () => ({ status: 'unavailable' });
            if (typeof credential !== 'string' || !/^[^\s,]{1,160}$/.test(credential) || !validId(requestId) || signal?.aborted)
                return Promise.resolve(unavailable());
            return new Promise(resolve => {
                let finished = false;
                let timer;
                const finish = value => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(new URL(`/cloud/internal/v1/classification/quota/fingerprint-key/${requestId}`, url), {
                        method: 'GET', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${credential}`, 'cache-control': 'no-store',
                            ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200) { response.destroy(); finish(unavailable()); return; }
                        const chunks = [];
                        let size = 0;
                        response.on('data', chunk => { size += chunk.length; if (size > 4096) { response.destroy(); finish(unavailable()); } else chunks.push(chunk); });
                        response.on('error', () => finish(unavailable()));
                        response.on('aborted', () => finish(unavailable()));
                        response.on('end', () => {
                            try {
                                const result = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                                if (!result || typeof result !== 'object' || Array.isArray(result) || Object.keys(result).some(key => !['status', 'keyId', 'expiresAt'].includes(key))) return finish(unavailable());
                                if (result.status === 'found' && typeof result.keyId === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(result.keyId) &&
                                    typeof result.expiresAt === 'string' && Number.isFinite(Date.parse(result.expiresAt)))
                                    return finish({ status: 'found', keyId: result.keyId, expiresAt: result.expiresAt });
                                if (['not_found', 'legacy_unverifiable'].includes(result.status) && result.keyId == null && result.expiresAt == null) return finish({ status: result.status });
                            } catch { /* Never expose private endpoint diagnostics. */ }
                            finish(unavailable());
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(unavailable()); }, timeoutMs);
                    request.on('error', () => finish(unavailable()));
                    request.end();
                } catch { finish(unavailable()); }
            });
        },
        reserveClassification(credential, input, signal, traceparent) {
            const unavailable = () => ({ status: 'quota_unavailable' });
            if (typeof credential !== 'string' || !/^[^\s,]{1,160}$/.test(credential) || !input || signal?.aborted ||
                !validId(input.requestId) || !/^[A-Za-z0-9_-]{1,64}$/.test(input.fingerprintKeyId ?? '') ||
                typeof input.fingerprint !== 'string' || !/^[A-Za-z0-9+/]{43}=$/.test(input.fingerprint) ||
                typeof input.expiresAt !== 'string' || !Number.isFinite(Date.parse(input.expiresAt))) return Promise.resolve(unavailable());
            const body = Buffer.from(JSON.stringify({ requestId: input.requestId, fingerprintKeyId: input.fingerprintKeyId,
                fingerprint: input.fingerprint, expiresAt: input.expiresAt }), 'utf8');
            return new Promise(resolve => {
                let finished = false;
                let timer;
                const finish = value => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(new URL('/cloud/internal/v1/classification/quota/reservations', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${credential}`, 'content-type': 'application/json', 'content-length': body.length,
                            'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        const chunks = []; let size = 0;
                        response.on('data', chunk => { size += chunk.length; if (size > 4096) { response.destroy(); finish(unavailable()); } else chunks.push(chunk); });
                        response.on('error', () => finish(unavailable()));
                        response.on('aborted', () => finish(unavailable()));
                        response.on('end', () => {
                            try {
                                const result = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                                if (!result || typeof result !== 'object' || Array.isArray(result) || Object.keys(result).some(key => !['status', 'reservationId'].includes(key))) return finish(unavailable());
                                if (response.statusCode === 200 && ['reserved', 'duplicate'].includes(result.status) && validId(result.reservationId)) return finish(result);
                                if (response.statusCode === 429 && result.status === 'quota_exceeded' && result.reservationId == null) return finish({ status: 'quota_exceeded' });
                                if (response.statusCode === 409 && result.status === 'idempotency_conflict' && validId(result.reservationId)) return finish({ status: 'idempotency_conflict' });
                            } catch { /* Never expose private endpoint diagnostics. */ }
                            finish(unavailable());
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(unavailable()); }, timeoutMs);
                    request.on('error', () => finish(unavailable()));
                    request.end(body);
                } catch { finish(unavailable()); }
            });
        },
        matchClassificationDeployment(credential, deployment, signal, traceparent) {
            if (typeof credential !== 'string' || !/^[^\s,]{1,160}$/.test(credential) || !deployment || signal?.aborted)
                return Promise.resolve(false);
            const { endpointReference, region, contractVersion, modelRevision } = deployment;
            const token = (value, limit) => typeof value === 'string' && value.length > 0 && value.length <= limit && /^[A-Za-z0-9._-]+$/.test(value);
            if (!token(endpointReference, 128) || !token(region, 80) || !token(contractVersion, 32) ||
                typeof modelRevision !== 'string' || !modelRevision.trim() || modelRevision.length > 256) return Promise.resolve(false);
            const body = Buffer.from(JSON.stringify({ endpointReference, region, contractVersion, modelRevision }), 'utf8');
            if (body.length > 4096) { body.fill(0); return Promise.resolve(false); }
            return new Promise(resolve => {
                let finished = false;
                let timer;
                const finish = value => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(new URL('/cloud/internal/v1/classification/deployment-match', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${credential}`, 'content-type': 'application/json', 'content-length': body.length,
                            'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200) { response.destroy(); finish(false); return; }
                        const chunks = [];
                        let size = 0;
                        response.on('data', chunk => { size += chunk.length; if (size > 4096) { response.destroy(); finish(false); } else chunks.push(chunk); });
                        response.on('error', () => finish(false));
                        response.on('aborted', () => finish(false));
                        response.on('end', () => {
                            try { const result = JSON.parse(Buffer.concat(chunks).toString('utf8')); finish(result !== null && typeof result === 'object' && !Array.isArray(result) && Object.keys(result).length === 1 && result.matched === true); }
                            catch { finish(false); }
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(false); }, timeoutMs);
                    request.on('error', () => { body.fill(0); finish(false); });
                    request.end(body, () => body.fill(0));
                } catch { body.fill(0); finish(false); }
            });
        },
        inspectClassification(credential, requestId, content, signal, traceparent) {
            const rejected = () => ({ allowed: false, code: 'capability_unavailable' });
            if (typeof credential !== 'string' || !/^[^\s,]{1,160}$/.test(credential) || !validId(requestId) ||
                typeof content !== 'string' || !content || signal?.aborted) return Promise.resolve(rejected());
            const body = Buffer.from(content, 'utf8');
            if (body.length > 65536) { body.fill(0); return Promise.resolve(rejected()); }
            return new Promise(resolve => {
                let finished = false;
                let timer;
                const finish = value => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(new URL('/cloud/internal/v1/classification/privacy', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${credential}`, 'x-cloud-request-id': requestId,
                            'content-type': 'text/plain; charset=utf-8', 'content-length': body.length, 'cache-control': 'no-store',
                            ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        const chunks = [];
                        let size = 0;
                        response.on('data', chunk => {
                            size += chunk.length;
                            if (size > 8192) { response.destroy(); finish(rejected()); }
                            else chunks.push(chunk);
                        });
                        response.on('error', () => finish(rejected()));
                        response.on('aborted', () => finish(rejected()));
                        response.on('end', () => {
                            try {
                                const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                                if (response.statusCode === 200 && value.allowed === true && value.code === 'allowed') return finish({ allowed: true, code: 'allowed' });
                                if (response.statusCode === 403 && value.allowed === false && value.code === 'privacy_blocked') return finish({ allowed: false, code: 'privacy_blocked' });
                            } catch { /* Never expose private endpoint diagnostics. */ }
                            finish(rejected());
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(rejected()); }, timeoutMs);
                    request.on('error', () => { body.fill(0); finish(rejected()); });
                    request.end(body, () => body.fill(0));
                } catch { body.fill(0); finish(rejected()); }
            });
        },
    };
}
