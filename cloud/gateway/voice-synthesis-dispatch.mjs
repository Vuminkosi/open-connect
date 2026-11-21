import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { prepareVoiceSynthesisWorkerPayload, validateVoiceSynthesisWorkerResponse } from './voice-synthesis-worker-contract.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'upstream_unavailable' });
const maximumResponseBytes = 8_000_000;

// Private transport only: a successful result does not itself establish policy or delivery.
export function createVoiceSynthesisDispatcher({ origin, ca, certificate, privateKey, workerToken, timeoutMs = 30000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000) throw new TypeError('Invalid voice dispatcher configuration.');
    return Object.freeze({
        async executeClaimed(claim, signal, traceparent) {
            if (!claim || typeof claim !== 'object') return unavailable();
            const { payload, key, reservationId, claimId, request } = claim;
            if (signal?.aborted || !(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 65536 ||
                !(key instanceof Uint8Array) || key.byteLength !== 32 || !guid.test(reservationId ?? '') || !guid.test(claimId ?? '')) return unavailable();
            const expected = prepareVoiceSynthesisWorkerPayload(request);
            if (!expected) return unavailable();
            try {
                if (!expected.payload.equals(Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength))) return unavailable();
            } finally { expected.payload.fill(0); }
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.worker.voice-synthesis/v1', reservationId, claimId,
                payload: Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('base64'),
                key: Buffer.from(key.buffer, key.byteOffset, key.byteLength).toString('base64') }), 'utf8');
            if (body.length > 100000) { body.fill(0); return unavailable(); }
            return new Promise(resolve => {
                let finished = false; let timer; let clearResponse = () => {};
                const finish = result => { if (!finished) { finished = true; clearTimeout(timer); body.fill(0); clearResponse(); resolve(result); } };
                try {
                    const outgoing = https.request(new URL('/internal/cloud/v1/voice-synthesis/execute', url), {
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
                            if (size > maximumResponseBytes) { response.destroy(); clear(); finish(unavailable()); }
                        });
                        response.on('error', () => { clear(); finish(unavailable()); });
                        response.on('aborted', () => { clear(); finish(unavailable()); });
                        response.on('end', () => {
                            let combined;
                            try {
                                combined = Buffer.concat(chunks);
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(combined));
                                const result = validateVoiceSynthesisWorkerResponse(value, request);
                                if ((signal?.aborted || finished || Date.parse(request.expiresAt) <= Date.now()) && result) { result.audio.fill(0); finish(unavailable()); }
                                else finish(result ? { status: 'completed', result } : unavailable());
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
