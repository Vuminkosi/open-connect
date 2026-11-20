import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const unavailable = () => ({ status: 'upstream_unavailable' });
const only = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).every(key => keys.includes(key));

// This validates transport and accounting only; output must still pass gateway egress inspection.
export function validateInferenceWorkerResponse(value, requestId, modelId, reservedTokens, maxOutputTokens) {
    if (!only(value, ['schemaVersion', 'requestId', 'modelId', 'output', 'usage']) ||
        value.schemaVersion !== 'openconnect.cloud.inference.result/v1' || value.requestId !== requestId || value.modelId !== modelId ||
        typeof value.output !== 'string' || Buffer.byteLength(value.output, 'utf8') > 131072 ||
        !only(value.usage, ['inputTokens', 'outputTokens']) || !Number.isSafeInteger(value.usage.inputTokens) ||
        !Number.isSafeInteger(value.usage.outputTokens) || value.usage.inputTokens < 0 || value.usage.outputTokens < 0 ||
        !Number.isSafeInteger(reservedTokens) || !Number.isSafeInteger(maxOutputTokens) ||
        value.usage.outputTokens > maxOutputTokens || value.usage.inputTokens + value.usage.outputTokens > reservedTokens) return null;
    return { output: value.output, usage: { inputTokens: value.usage.inputTokens, outputTokens: value.usage.outputTokens } };
}

// This adapter can only carry an already claimed payload to a deployment-owned HTTPS endpoint.
export function createInferenceDispatcher({ origin, ca, certificate, privateKey, workerToken, timeoutMs = 30000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000) throw new TypeError('Invalid inference dispatcher configuration.');
    return Object.freeze({
        async executeClaimed({ payload, key, reservationId, claimId, requestId, modelId, reservedTokens, maxOutputTokens }, signal, traceparent) {
            if (signal?.aborted || !(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 131072 ||
                !(key instanceof Uint8Array) || key.byteLength !== 32 || !guid.test(reservationId ?? '') || !guid.test(claimId ?? '') ||
                !guid.test(requestId ?? '') || typeof modelId !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(modelId) ||
                !Number.isSafeInteger(reservedTokens) || reservedTokens < 1 || !Number.isSafeInteger(maxOutputTokens) ||
                maxOutputTokens < 1 || maxOutputTokens > 4096 || maxOutputTokens > reservedTokens) return unavailable();
            try {
                const request = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(payload));
                if (request?.schemaVersion !== 'openconnect.cloud.inference.worker/v1' || request.requestId !== requestId ||
                    request.modelId !== modelId || request.input?.maxTokens !== maxOutputTokens || request.input?.stream !== false) return unavailable();
            } catch { return unavailable(); }
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.cloud.worker.inference/v1', reservationId, claimId,
                payload: Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('base64'),
                key: Buffer.from(key.buffer, key.byteOffset, key.byteLength).toString('base64') }), 'utf8');
            if (body.length > 180000) { body.fill(0); return unavailable(); }
            return new Promise(resolve => {
                let finished = false; let timer;
                const finish = result => { if (!finished) { finished = true; clearTimeout(timer); body.fill(0); resolve(result); } };
                try {
                    const outgoing = https.request(new URL('/internal/cloud/v1/inference/execute', url), {
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
                                const result = validateInferenceWorkerResponse(value, requestId, modelId, reservedTokens, maxOutputTokens);
                                finish(result ? { status: 'completed', result } : unavailable());
                            } catch { finish(unavailable()); }
                            finally { combined?.fill(0); clear(); }
                        });
                    });
                    timer = setTimeout(() => { outgoing.destroy(); finish(unavailable()); }, timeoutMs);
                    outgoing.on('error', () => finish(unavailable()));
                    outgoing.end(body, () => body.fill(0));
                } catch { finish(unavailable()); }
            });
        },
    });
}
