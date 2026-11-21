import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { parseTranslationRequest } from './translation-request.mjs';
import { prepareTranslationWorkerPayload, validateTranslationWorkerResponse } from './translation-worker-contract.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'upstream_unavailable' });

// This private transport does not perform admission, privacy screening or result release.
export function createTranslationDispatcher({ origin, ca, certificate, privateKey, workerToken, timeoutMs = 30000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000) throw new TypeError('Invalid translation dispatcher configuration.');
    return Object.freeze({
        async executeClaimed(claim, signal, traceparent) {
            if (!claim || typeof claim !== 'object') return unavailable();
            const { payload, key, reservationId, claimId } = claim;
            if (signal?.aborted || !(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 65536 ||
                !(key instanceof Uint8Array) || key.byteLength !== 32 || typeof reservationId !== 'string' || !guid.test(reservationId) ||
                typeof claimId !== 'string' || !guid.test(claimId)) return unavailable();
            let request; let expected; let snapshot;
            try {
                snapshot = Buffer.from(JSON.stringify(claim.request), 'utf8');
                request = parseTranslationRequest(snapshot);
                expected = prepareTranslationWorkerPayload(request);
                if (!expected || !expected.payload.equals(Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength))) return unavailable();
            } catch { return unavailable(); }
            finally { expected?.payload.fill(0); snapshot?.fill(0); }
            const deployment = { modelRevision: claim.deployment?.modelRevision, tokenizerHash: claim.deployment?.tokenizerHash };
            if (typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() || deployment.modelRevision.length > 256 ||
                typeof deployment.tokenizerHash !== 'string' || !/^[0-9a-f]{64}$/.test(deployment.tokenizerHash)) return unavailable();
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.worker.translation/v1', reservationId, claimId,
                payload: Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('base64'),
                key: Buffer.from(key.buffer, key.byteOffset, key.byteLength).toString('base64') }), 'utf8');
            if (body.length > 100000) { body.fill(0); return unavailable(); }
            return new Promise(resolve => {
                let finished = false; let timer; let clearResponse = () => {};
                const finish = result => { if (!finished) { finished = true; clearTimeout(timer); body.fill(0); clearResponse(); resolve(result); } };
                try {
                    const outgoing = https.request(new URL('/internal/cloud/v1/translation/execute', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'content-type': 'application/json', 'content-length': body.length,
                            accept: 'application/json', 'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding'] ||
                            !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(response.headers['content-type'] ?? '')) {
                            response.destroy(); finish(unavailable()); return;
                        }
                        const chunks = []; let size = 0;
                        const clear = () => { for (const chunk of chunks) chunk.fill(0); };
                        clearResponse = clear;
                        response.on('data', chunk => {
                            if (finished) { chunk.fill(0); return; }
                            size += chunk.length; chunks.push(chunk);
                            if (size > 100000) { response.destroy(); finish(unavailable()); }
                        });
                        response.on('error', () => finish(unavailable())); response.on('aborted', () => finish(unavailable()));
                        response.on('end', () => {
                            let combined;
                            try {
                                combined = Buffer.concat(chunks);
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(combined));
                                const result = validateTranslationWorkerResponse(value, request, deployment);
                                finish(!finished && !signal?.aborted && Date.parse(request.expiresAt) > Date.now() && result
                                    ? { status: 'completed', result } : unavailable());
                            } catch { finish(unavailable()); }
                            finally { combined?.fill(0); clear(); }
                        });
                    });
                    timer = setTimeout(() => { outgoing.destroy(); finish(unavailable()); }, timeoutMs);
                    outgoing.on('error', () => finish(unavailable())); outgoing.end(body, () => body.fill(0));
                } catch { finish(unavailable()); }
            });
        },
    });
}
