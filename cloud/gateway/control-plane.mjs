import https from 'node:https';

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
        authorize(credential, capability, signal) {
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
                        headers: { 'content-type': 'application/json', 'content-length': body.length, 'cache-control': 'no-store' },
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
        inspectClassification(credential, requestId, content, signal) {
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
                            'content-type': 'text/plain; charset=utf-8', 'content-length': body.length, 'cache-control': 'no-store' },
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
