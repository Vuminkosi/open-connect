import https from 'node:https';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

export function createKnowledgeEmbeddingProbe({ origin, ca, certificate, privateKey, workerToken, deploymentId, timeoutMs = 5000 }) {
    const base = new URL(origin);
    if (base.protocol !== 'https:' || base.username || base.password || base.pathname !== '/' || base.search || base.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        typeof deploymentId !== 'string' || !guid.test(deploymentId) || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid knowledge embedding readiness configuration.');
    return Object.freeze({
        probe(signal, traceparent) {
            if (signal?.aborted) return Promise.resolve(false);
            return new Promise(resolve => {
                let done = false; let timer; const chunks = [];
                const finish = result => { if (!done) { done = true; clearTimeout(timer); for (const chunk of chunks) chunk.fill(0); resolve(result); } };
                try {
                    const req = https.request(new URL('/internal/cloud/v1/knowledge-embed/ready', base), {
                        method: 'GET', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding']) { response.destroy(); finish(false); return; }
                        let size = 0;
                        response.on('data', chunk => {
                            if (done) { chunk.fill(0); return; }
                            size += chunk.length; chunks.push(chunk);
                            if (size > 4096) { response.destroy(); finish(false); }
                        });
                        response.on('error', () => finish(false)); response.on('aborted', () => finish(false));
                        response.on('end', () => {
                            if (done) return;
                            let bytes;
                            try {
                                bytes = Buffer.concat(chunks);
                                const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
                                finish(value !== null && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length === 2 &&
                                    value.ready === true && typeof value.deploymentId === 'string' && value.deploymentId.toLowerCase() === deploymentId.toLowerCase());
                            } catch { finish(false); }
                            finally { bytes?.fill(0); }
                        });
                    });
                    timer = setTimeout(() => { req.destroy(); finish(false); }, timeoutMs);
                    req.on('error', () => finish(false)); req.end();
                } catch { finish(false); }
            });
        },
    });
}
