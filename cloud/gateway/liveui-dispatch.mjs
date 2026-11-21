import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { validateLiveUIWorkerResponse } from './liveui-contract.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'upstream_unavailable' });

// This private adapter transports only a claimed payload; plans remain untrusted proposals.
export function createLiveUIDispatcher({ origin, ca, certificate, privateKey, workerToken, timeoutMs = 30000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000) throw new TypeError('Invalid LiveUI dispatcher configuration.');
    return Object.freeze({
        async executeClaimed({ payload, key, reservationId, claimId, request, modelId }, signal, traceparent) {
            if (signal?.aborted || !(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 131072 ||
                !(key instanceof Uint8Array) || key.byteLength !== 32 || !guid.test(reservationId ?? '') || !guid.test(claimId ?? '') ||
                !request || !guid.test(request.requestId ?? '') || typeof modelId !== 'string' ||
                !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId)) return unavailable();
            try {
                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(payload));
                if (!value || Object.keys(value).sort().join(',') !== 'input,modelId,requestId,schemaVersion' ||
                    value.schemaVersion !== 'openconnect.cloud.liveui.worker/v1' || value.requestId !== request.requestId || value.modelId !== modelId ||
                    JSON.stringify(value.input) !== JSON.stringify(request.input)) return unavailable();
            } catch { return unavailable(); }
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.worker.liveui/v1', reservationId, claimId,
                payload: Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('base64'),
                key: Buffer.from(key.buffer, key.byteOffset, key.byteLength).toString('base64') }), 'utf8');
            if (body.length > 180000) { body.fill(0); return unavailable(); }
            return new Promise(resolve => {
                let finished = false; let timer;
                const finish = result => { if (!finished) { finished = true; clearTimeout(timer); body.fill(0); resolve(result); } };
                try {
                    const outgoing = https.request(new URL('/internal/cloud/v1/liveui/execute', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'content-type': 'application/json', 'content-length': body.length,
                            accept: 'application/json', 'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding']) { response.destroy(); finish(unavailable()); return; }
                        const chunks = []; let size = 0;
                        const clear = () => { for (const chunk of chunks) chunk.fill(0); };
                        response.on('data', chunk => { size += chunk.length; chunks.push(chunk); if (size > 262144) { response.destroy(); clear(); finish(unavailable()); } });
                        response.on('error', () => { clear(); finish(unavailable()); });
                        response.on('aborted', () => { clear(); finish(unavailable()); });
                        response.on('end', () => {
                            let combined;
                            try {
                                combined = Buffer.concat(chunks);
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(combined));
                                const plan = validateLiveUIWorkerResponse(value, request);
                                finish(plan ? { status: 'completed', result: { plan, provider: value.provider } } : unavailable());
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
