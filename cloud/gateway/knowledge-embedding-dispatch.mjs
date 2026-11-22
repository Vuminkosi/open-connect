import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const validId = value => typeof value === 'string' && guid.test(value);
const unavailable = () => ({ status: 'upstream_unavailable' });

// Receipt is not approval to disclose: caller must validate, inspect privacy and settle first.
// Caller owns and clears the returned sensitive bytes.
export function createKnowledgeEmbeddingDispatcher({ origin, ca, certificate, privateKey, workerToken, timeoutMs = 5000 }) {
    const base = new URL(origin);
    if (base.protocol !== 'https:' || base.username || base.password || base.pathname !== '/' || base.search || base.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000) throw new TypeError('Invalid knowledge embedding dispatcher configuration.');
    return Object.freeze({
        executeClaimed(prepared, claim, signal, traceparent) {
            if (signal?.aborted || !prepared || !(prepared.payload instanceof Uint8Array) || prepared.payload.byteLength < 1 ||
                prepared.payload.byteLength > 131072 || !validId(prepared.requestId) || !claim || !validId(claim.reservationId) ||
                !validId(claim.claimId) || !(claim.key instanceof Uint8Array) || claim.key.byteLength !== 32)
                return Promise.resolve(unavailable());
            try {
                const payload = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(prepared.payload));
                if (!payload || payload.schemaVersion !== 'openconnect.v1' || payload.kind !== 'knowledge.embed' ||
                    payload.taskId !== prepared.requestId || payload.tenantId !== prepared.tenantId || payload.expiresAt !== prepared.expiresAt)
                    return Promise.resolve(unavailable());
            } catch { return Promise.resolve(unavailable()); }
            const payloadView = Buffer.from(prepared.payload.buffer, prepared.payload.byteOffset, prepared.payload.byteLength);
            const keyView = Buffer.from(claim.key.buffer, claim.key.byteOffset, claim.key.byteLength);
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.worker.knowledge-embed/v1',
                reservationId: claim.reservationId, claimId: claim.claimId, payload: payloadView.toString('base64'), key: keyView.toString('base64') }));
            if (body.length > 176000) { body.fill(0); return Promise.resolve(unavailable()); }
            return new Promise(resolve => {
                let done = false; let timer; const chunks = [];
                const finish = value => {
                    if (done) return;
                    done = true; clearTimeout(timer); body.fill(0);
                    for (const chunk of chunks) chunk.fill(0);
                    resolve(value);
                };
                try {
                    const req = https.request(new URL('/internal/cloud/v1/knowledge-embed/execute', base), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'content-type': 'application/json', 'content-length': body.length,
                            accept: 'application/json', 'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding']) { response.destroy(); finish(unavailable()); return; }
                        let size = 0;
                        response.on('data', chunk => {
                            if (done) { chunk.fill(0); return; }
                            size += chunk.length; chunks.push(chunk);
                            if (size > 8 * 1024 * 1024) { response.destroy(); finish(unavailable()); }
                        });
                        response.on('error', () => finish(unavailable())); response.on('aborted', () => finish(unavailable()));
                        response.on('end', () => {
                            if (done) return;
                            let bytes;
                            try {
                                bytes = Buffer.concat(chunks);
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
                                if (!value || value.schemaVersion !== 'openconnect.v1' || value.taskId !== prepared.requestId ||
                                    value.kind !== 'knowledge.embed' || !value.result || typeof value.result !== 'object' || Array.isArray(value.result)) {
                                    bytes.fill(0); finish(unavailable()); return;
                                }
                                finish({ status: 'received', bytes });
                            } catch { bytes?.fill(0); finish(unavailable()); }
                        });
                    });
                    timer = setTimeout(() => { req.destroy(); finish(unavailable()); }, timeoutMs);
                    req.on('error', () => finish(unavailable()));
                    req.end(body, () => body.fill(0));
                } catch { finish(unavailable()); }
            });
        },
    });
}
