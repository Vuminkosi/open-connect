import https from 'node:https';
import { isValidTraceparent } from './trace-context.mjs';

const unavailable = () => ({ ready: false, code: 'capability_unavailable' });

// All connection and identity material is deployment-owned, never request-provided.
export function createClassificationProbe({ origin, ca, certificate, privateKey, workerToken, modelRevision, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        typeof modelRevision !== 'string' || modelRevision.length < 1 || modelRevision.length > 256 ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000)
        throw new TypeError('Invalid classification probe configuration.');

    function get(path, signal, traceparent) {
        if (signal?.aborted) return Promise.resolve(null);
        return new Promise(resolve => {
            let completed = false;
            let timer;
            const finish = value => { if (!completed) { completed = true; clearTimeout(timer); resolve(value); } };
            try {
                const request = https.request(new URL(path, url), {
                    method: 'GET', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                    headers: { authorization: `Bearer ${workerToken}`, accept: 'application/json', 'cache-control': 'no-store',
                        ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                }, response => {
                    if (response.statusCode !== 200) { response.destroy(); finish(null); return; }
                    const chunks = [];
                    let size = 0;
                    response.on('data', chunk => {
                        size += chunk.length;
                        if (size > 8192) { response.destroy(); finish(null); }
                        else chunks.push(chunk);
                    });
                    response.on('error', () => finish(null));
                    response.on('aborted', () => finish(null));
                    response.on('end', () => {
                        try { finish(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
                        catch { finish(null); }
                    });
                });
                timer = setTimeout(() => { request.destroy(); finish(null); }, timeoutMs);
                request.on('error', () => finish(null));
                request.end();
            } catch { finish(null); }
        });
    }

    return {
        async probe(signal, traceparent, requireCloudExecution = false, expectedDeploymentId = null) {
            const health = await get('/health/ready', signal, traceparent);
            if (health?.status !== 'ready' || health.ready !== true || health.engine !== 'real' || health.modelRevision !== modelRevision)
                return unavailable();
            const advertised = await get('/internal/v1/capabilities', signal, traceparent);
            if (signal?.aborted || advertised?.workerKind !== 'classification' || advertised.engine !== 'real' ||
                advertised.modelRevision !== modelRevision || !Array.isArray(advertised.capabilities) ||
                !advertised.capabilities.some(item => item?.kind === 'classification' && item.grantsTools === false))
                return unavailable();
            if (requireCloudExecution) {
                const cloud = await get('/internal/cloud/v1/classification/ready', signal, traceparent);
                if (signal?.aborted || !cloud || Object.keys(cloud).length !== 2 || cloud.status !== 'ready' ||
                    typeof cloud.deploymentId !== 'string' || typeof expectedDeploymentId !== 'string' || cloud.deploymentId.toLowerCase() !== expectedDeploymentId.toLowerCase()) return unavailable();
            }
            return { ready: true, capability: 'classification.classify', modelRevision };
        },
    };
}
