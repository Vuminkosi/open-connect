import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

export function createVoiceSynthesisProbe({ origin, ca, certificate, privateKey, workerToken, deploymentId, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        !guid.test(deploymentId ?? '') || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid voice probe configuration.');
    return Object.freeze({
        probe(signal, traceparent) {
            if (signal?.aborted) return Promise.resolve(false);
            return new Promise(resolve => {
                let done = false; let timer;
                const finish = value => { if (!done) { done = true; clearTimeout(timer); resolve(value); } };
                try {
                    const request = https.request(new URL('/internal/cloud/v1/voice-synthesis/ready', url), {
                        method: 'GET', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'cache-control': 'no-store',
                            ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding']) { response.destroy(); finish(false); return; }
                        const chunks = []; let size = 0;
                        response.on('data', chunk => { size += chunk.length; if (size > 4096) { response.destroy(); finish(false); } else chunks.push(chunk); });
                        response.on('error', () => finish(false)); response.on('aborted', () => finish(false));
                        response.on('end', () => {
                            try {
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks)));
                                finish(value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length === 2 &&
                                    value.status === 'ready' && typeof value.deploymentId === 'string' && value.deploymentId.toLowerCase() === deploymentId.toLowerCase());
                            } catch { finish(false); }
                        });
                    });
                    timer = setTimeout(() => { request.destroy(); finish(false); }, timeoutMs);
                    request.on('error', () => finish(false)); request.end();
                } catch { finish(false); }
            });
        },
    });
}
