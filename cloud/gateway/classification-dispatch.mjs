import https from 'node:https';
import { isValidTraceparent } from './trace-context.mjs';
import { parseUniqueJson } from './classification-request.mjs';

const unavailable = () => ({ status: 'upstream_unavailable' });
const labelsByVersion = new Map([
    ['v1', new Set(['safe', 'spam', 'toxic', 'sensitive'])],
    ['v2', new Set(['safe', 'spam', 'toxic', 'sensitive', 'pii', 'injection'])],
]);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, allowed) => object(value) && Object.keys(value).every(key => allowed.includes(key));
const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

// Worker output is untrusted. Only the versioned label vocabulary may leave this boundary.
export function validateClassificationWorkerResponse(value, taskId, modelRevision, labelSetVersion) {
    if (!only(value, ['schemaVersion', 'taskId', 'status', 'result', 'error']) || value.schemaVersion !== 'openconnect.v1' ||
        value.taskId !== taskId || value.status !== 'completed' || value.error != null ||
        !only(value.result, ['labels', 'scores', 'abstained', 'modelRevision', 'labelSetVersion', 'engine', 'engineWarning', 'grantsTools'])) return null;
    const result = value.result;
    const vocabulary = labelsByVersion.get(labelSetVersion);
    if (!vocabulary || result.modelRevision !== modelRevision || result.labelSetVersion !== labelSetVersion || result.engine !== 'real' ||
        result.engineWarning != null || result.grantsTools !== false || typeof result.abstained !== 'boolean' ||
        !Array.isArray(result.labels) || result.labels.length > vocabulary.size || new Set(result.labels).size !== result.labels.length ||
        !result.labels.every(label => vocabulary.has(label)) || !object(result.scores)) return null;
    const entries = Object.entries(result.scores);
    if (entries.length > vocabulary.size || entries.some(([label, score]) => !vocabulary.has(label) || typeof score !== 'number' || !Number.isFinite(score) || score < 0 || score > 1) ||
        result.labels.some(label => !Object.hasOwn(result.scores, label))) return null;
    return { labels: [...result.labels], scores: Object.fromEntries(entries), abstained: result.abstained,
        modelRevision: result.modelRevision, labelSetVersion: result.labelSetVersion };
}

// Destination and credentials are deployment-owned. This adapter does not grant execution authority.
export function createClassificationDispatcher({ origin, ca, certificate, privateKey, workerToken, modelRevision, timeoutMs = 5000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        typeof modelRevision !== 'string' || !modelRevision.trim() || modelRevision.length > 256 ||
        !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000) throw new TypeError('Invalid classification dispatcher configuration.');
    return Object.freeze({
        async execute(request, signal, traceparent) {
            if (signal?.aborted || !object(request) || typeof request.requestId !== 'string' || !guid.test(request.requestId) ||
                !object(request.input) || typeof request.input.text !== 'string' || [...request.input.text].length < 1 || [...request.input.text].length > 20000 ||
                Object.keys(request.input).some(key => key !== 'text')) return unavailable();
            const config = request.configuration ?? {};
            if (!only(config, ['labelSetVersion', 'localeHint', 'threshold', 'maxLabels'])) return unavailable();
            const labelSetVersion = config.labelSetVersion ?? 'v1';
            if (!labelsByVersion.has(labelSetVersion) || (config.localeHint !== undefined && (typeof config.localeHint !== 'string' || [...config.localeHint].length > 16)) ||
                (config.threshold !== undefined && (typeof config.threshold !== 'number' || !Number.isFinite(config.threshold) || config.threshold < 0 || config.threshold > 1)) ||
                (config.maxLabels !== undefined && (!Number.isInteger(config.maxLabels) || config.maxLabels < 1 || config.maxLabels > 10))) return unavailable();
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: request.requestId,
                input: { text: request.input.text, ...config } }), 'utf8');
            if (body.length > 131072) { body.fill(0); return unavailable(); }
            return new Promise(resolve => {
                let done = false; let timer;
                const finish = value => { if (!done) { done = true; clearTimeout(timer); body.fill(0); resolve(value); } };
                try {
                    const outgoing = https.request(new URL('/internal/v1/execute', url), {
                        method: 'POST', ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                        headers: { authorization: `Bearer ${workerToken}`, 'content-type': 'application/json', 'content-length': body.length,
                            accept: 'application/json', 'cache-control': 'no-store', ...(isValidTraceparent(traceparent) ? { traceparent } : {}) },
                    }, response => {
                        if (response.statusCode !== 200 || response.headers['content-encoding']) { response.destroy(); finish(unavailable()); return; }
                        const chunks = []; let size = 0;
                        const clear = () => { for (const chunk of chunks) chunk.fill(0); };
                        response.on('data', chunk => { size += chunk.length; chunks.push(chunk); if (size > 16384) { response.destroy(); clear(); finish(unavailable()); } });
                        response.on('error', () => { clear(); finish(unavailable()); }); response.on('aborted', () => { clear(); finish(unavailable()); });
                        response.on('end', () => {
                            let combined;
                            try {
                                combined = Buffer.concat(chunks);
                                const text = new TextDecoder('utf-8', { fatal: true }).decode(combined);
                                const result = validateClassificationWorkerResponse(parseUniqueJson(text), request.requestId, modelRevision, labelSetVersion);
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
