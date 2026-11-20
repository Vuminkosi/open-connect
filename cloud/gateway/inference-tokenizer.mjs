import { spawn } from 'node:child_process';
import { isAbsolute } from 'node:path';
import { parseUniqueJson } from './classification-request.mjs';

// A private child process uses the same verified model and prompt formatter as the worker.
// It handles one quote at a time; timeout, cancellation or malformed output kills the child.
export function createLocalInferenceTokenizer({ python, pythonPath, model, startupTimeoutMs = 60000, quoteTimeoutMs = 5000 }, spawnProcess = spawn) {
    if (!isAbsolute(python ?? '') || !isAbsolute(pythonPath ?? '') || !model || !isAbsolute(model.path ?? '') ||
        typeof model.id !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/.test(model.id) ||
        typeof model.revision !== 'string' || !model.revision.trim() || model.revision.length > 256 ||
        !/^sha256:[0-9a-f]{64}$/.test(model.digest ?? '') || model.format !== 'chatml' ||
        model.licenseAccepted !== true || model.artifactReviewed !== true ||
        !Number.isInteger(model.contextTokens) || model.contextTokens < 512 || model.contextTokens > 8192 ||
        !Number.isInteger(model.threads) || model.threads < 1 || model.threads > 16 ||
        !Number.isInteger(startupTimeoutMs) || startupTimeoutMs < 1 || startupTimeoutMs > 120000 ||
        !Number.isInteger(quoteTimeoutMs) || quoteTimeoutMs < 1 || quoteTimeoutMs > 30000) throw new TypeError('Invalid local tokenizer configuration.');
    model = Object.freeze({ ...model });
    let child; let pending; let line = Buffer.alloc(0); let busy = false; let disposed = false;
    const env = { PYTHONPATH: pythonPath, PYTHONUNBUFFERED: '1', OPENCONNECT_CLOUD_MODEL_PATH: model.path,
        OPENCONNECT_CLOUD_MODEL_ID: model.id, OPENCONNECT_CLOUD_MODEL_REVISION: model.revision,
        OPENCONNECT_CLOUD_MODEL_DIGEST: model.digest, OPENCONNECT_CLOUD_MODEL_FORMAT: model.format,
        OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED: 'true', OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED: 'true',
        OPENCONNECT_CLOUD_MODEL_CONTEXT_TOKENS: String(model.contextTokens), OPENCONNECT_CLOUD_MODEL_THREADS: String(model.threads) };
    if (process.platform === 'win32' && process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
    function stop() {
        const active = child; child = undefined;
        line.fill(0); line = Buffer.alloc(0);
        if (pending) { const resolve = pending; pending = undefined; resolve(null); }
        if (active) active.kill();
    }
    function nextLine(timeoutMs, signal) {
        return new Promise(resolve => {
            let timer;
            const abort = () => stop();
            const finish = value => { clearTimeout(timer); signal?.removeEventListener('abort', abort); resolve(value); };
            pending = finish;
            if (signal?.aborted) { stop(); return; }
            signal?.addEventListener('abort', abort, { once: true });
            timer = setTimeout(stop, timeoutMs);
        });
    }
    async function start(signal) {
        if (child) return true;
        try {
            const processChild = spawnProcess(python, ['-m', 'openconnect_inference.cloud_quote'], { env, stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true });
            child = processChild;
            processChild.on('error', () => { if (child === processChild) stop(); });
            processChild.on('exit', () => { if (child === processChild) stop(); });
            processChild.stdin.on('error', () => { if (child === processChild) stop(); });
            processChild.stdout.on('data', chunk => {
                if (child !== processChild) return;
                const combined = Buffer.concat([line, chunk]); line.fill(0); line = combined;
                const newline = line.indexOf(10);
                if (line.length > 4096 || (newline !== -1 && newline !== line.length - 1)) { stop(); return; }
                if (newline !== -1) {
                    if (!pending) { stop(); return; }
                    let value;
                    try { value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(line.subarray(0, newline))); }
                    catch { stop(); return; }
                    line.fill(0); line = Buffer.alloc(0);
                    const resolve = pending; pending = undefined; resolve(value);
                }
            });
            const ready = await nextLine(startupTimeoutMs, signal);
            if (ready?.status !== 'ready' || Object.keys(ready).length !== 1 || child !== processChild) { stop(); return false; }
            return true;
        } catch { stop(); return false; }
    }
    return Object.freeze({
        async quote({ payload, key, deployment, signal }) {
            if (disposed || busy || signal?.aborted || !(payload instanceof Uint8Array) || payload.byteLength < 1 || payload.byteLength > 131072 ||
                !(key instanceof Uint8Array) || key.byteLength !== 32 || deployment?.modelRevision !== model.revision || deployment?.modelDigest !== model.digest) return null;
            busy = true;
            try {
                if (!await start(signal) || signal?.aborted || !child) return null;
                const body = Buffer.from(JSON.stringify({ payload: Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('base64'),
                    key: Buffer.from(key.buffer, key.byteOffset, key.byteLength).toString('base64'),
                    modelRevision: model.revision, modelDigest: model.digest }) + '\n');
                if (body.length > 180000) { body.fill(0); return null; }
                const answer = nextLine(quoteTimeoutMs, signal);
                try { child.stdin.write(body, () => body.fill(0)); } catch { body.fill(0); stop(); }
                const result = await answer;
                return signal?.aborted ? null : result;
            } catch { stop(); return null; }
            finally { busy = false; }
        },
        dispose() { disposed = true; stop(); },
    });
}
