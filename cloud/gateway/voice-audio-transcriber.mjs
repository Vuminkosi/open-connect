import https from 'node:https';
import { createHash } from 'node:crypto';
import { parseUniqueJson } from './classification-request.mjs';
import { isValidTraceparent } from './trace-context.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const only = (v, keys) => v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).every(k => keys.includes(k));
const maxPcm = 24000 * 2 * 120;

// This client obtains a transcript; it does not make an output privacy decision.
export function createVoiceAudioTranscriber({ origin, ca, certificate, privateKey, workerToken, modelId, modelRevision, modelFingerprint, timeoutMs = 30000 }) {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
        !ca || !certificate || !privateKey || typeof workerToken !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(workerToken) ||
        typeof modelId !== 'string' || modelId.length < 1 || modelId.length > 256 ||
        typeof modelRevision !== 'string' || modelRevision.length < 1 || modelRevision.length > 128 ||
        !/^[0-9a-f]{64}$/.test(modelFingerprint ?? '') || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000)
        throw new TypeError('Invalid audio transcriber configuration.');

    async function call(method, path, body, signal, traceparent) {
        if (signal?.aborted) return null;
        return new Promise(resolve => {
            let done = false; let timer; let chunks = [];
            const finish = value => { if (done) return; done = true; clearTimeout(timer); for (const part of chunks) part.fill(0); chunks = []; resolve(value); };
            try {
                const req = https.request(new URL(path, url), { method, ca, cert: certificate, key: privateKey, rejectUnauthorized: true, signal,
                    headers: { authorization: `Bearer ${workerToken}`, accept: 'application/json', 'cache-control': 'no-store',
                        ...(body ? { 'content-type': 'application/json', 'content-length': body.byteLength } : {}),
                        ...(isValidTraceparent(traceparent) ? { traceparent } : {}) } }, res => {
                    if (res.statusCode !== 200 || res.headers['content-encoding'] ||
                        !/^application\/json(?:\s*;\s*charset\s*=\s*utf-8)?\s*$/i.test(res.headers['content-type'] ?? '')) {
                        res.destroy(); finish(null); return;
                    }
                    let size = 0;
                    res.on('data', part => { if (done) { part.fill(0); return; } size += part.length; chunks.push(part); if (size > 32768) { res.destroy(); finish(null); } });
                    res.on('error', () => finish(null)); res.on('aborted', () => finish(null));
                    res.on('end', () => { let raw; try { raw = Buffer.concat(chunks); finish(parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(raw))); }
                        catch { finish(null); } finally { raw?.fill(0); } });
                });
                timer = setTimeout(() => { req.destroy(); finish(null); }, timeoutMs);
                req.on('error', () => finish(null));
                req.end(body ?? undefined);
            } catch { finish(null); }
        });
    }

    const identityMatches = value => value?.modelId === modelId && value?.modelRevision === modelRevision && value?.modelFingerprint === modelFingerprint;
    return Object.freeze({
        async isReady(signal, traceparent) {
            const v = await call('GET', '/internal/v1/audio-inspection/ready', null, signal, traceparent);
            return only(v, ['ready', 'modelId', 'modelRevision', 'modelFingerprint', 'maxAudioBytes', 'maxDurationMs', 'sampleRatesHz']) &&
                v.ready === true && identityMatches(v) && Number.isSafeInteger(v.maxAudioBytes) && v.maxAudioBytes >= maxPcm + 44 &&
                Number.isSafeInteger(v.maxDurationMs) && v.maxDurationMs >= 120000 && Array.isArray(v.sampleRatesHz) && v.sampleRatesHz.includes(24000);
        },
        async transcribe({ requestId, audio, format, locale }, signal, traceparent) {
            if (!guid.test(requestId ?? '') || !(audio instanceof Uint8Array) || !['pcm', 'wav'].includes(format) ||
                typeof locale !== 'string' || !/^[a-z]{2}(?:-[A-Z]{2})?$/.test(locale)) return null;
            const pcmLength = audio.byteLength - (format === 'wav' ? 44 : 0);
            if (pcmLength < 2 || pcmLength > maxPcm || pcmLength % 2 !== 0) return null;
            const durationMs = Math.floor(pcmLength / 48);
            if (durationMs < 1 || durationMs > 120000) return null;
            const digest = createHash('sha256').update(audio).digest('hex');
            const body = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', requestId,
                input: { audioBase64: Buffer.from(audio.buffer, audio.byteOffset, audio.byteLength).toString('base64'),
                    mimeType: format === 'wav' ? 'audio/wav' : 'audio/pcm', sampleRateHz: 24000, channels: 1, bitsPerSample: 16, durationMs, locale } }));
            try {
                const v = await call('POST', '/internal/v1/audio-inspection/transcribe', body, signal, traceparent);
                if (!only(v, ['schemaVersion', 'requestId', 'modelId', 'modelRevision', 'modelFingerprint', 'audioSha256', 'transcript', 'locale', 'durationMs']) ||
                    v.schemaVersion !== 'openconnect.audio-inspection.transcript/v1' || v.requestId !== requestId || !identityMatches(v) ||
                    v.audioSha256 !== digest || v.locale !== locale || v.durationMs !== durationMs ||
                    typeof v.transcript !== 'string' || !v.transcript.isWellFormed() || v.transcript.length < 1 || v.transcript.length > 8192) return null;
                return { transcript: v.transcript, modelFingerprint };
            } finally { body.fill(0); }
        },
    });
}
