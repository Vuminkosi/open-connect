import { parseVoiceSynthesisRequest } from './voice-synthesis-request.mjs';

const maxPcmBytes = 24000 * 2 * 120;
const only = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).every(key => keys.includes(key));

// The returned bytes are the exact worker payload to bind to a single-use claim.
// The caller owns and clears them after dispatch or failure.
export function prepareVoiceSynthesisWorkerPayload(request, now = Date.now()) {
    let bytes;
    try {
        bytes = Buffer.from(JSON.stringify(request), 'utf8');
        const validated = parseVoiceSynthesisRequest(bytes, now);
        const payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: validated.requestId,
            kind: 'speech.synthesize', payload: { schemaVersion: 'openconnect.v1', text: validated.input.text,
                voiceId: validated.input.voiceId, locale: validated.input.locale, format: validated.input.format } }), 'utf8');
        if (payload.byteLength > 65536) { payload.fill(0); return null; }
        return { payload, requestId: validated.requestId };
    } catch { return null; }
    finally { bytes?.fill(0); }
}

// The worker response is untrusted. Return only bounded, validated audio and
// non-sensitive metadata; the caller owns and clears the returned audio bytes.
export function validateVoiceSynthesisWorkerResponse(value, request) {
    if (!request || typeof request.requestId !== 'string' || !request.input ||
        !only(value, ['schemaVersion', 'taskId', 'kind', 'idempotentReplay', 'result']) ||
        value.schemaVersion !== 'openconnect.v1' || value.taskId !== request?.requestId ||
        value.kind !== 'speech.synthesize' || value.idempotentReplay !== false ||
        !only(value.result, ['text', 'voiceId', 'locale', 'format', 'sampleRateHz', 'channels', 'audioBase64'])) return null;
    const result = value.result;
    if (result.text !== request.input?.text || result.voiceId !== request.input?.voiceId ||
        result.locale !== request.input?.locale || result.format !== request.input?.format ||
        result.sampleRateHz !== 24000 || result.channels !== 1 || typeof result.audioBase64 !== 'string' ||
        result.audioBase64.length < 4 || result.audioBase64.length > Math.ceil((maxPcmBytes + 44) / 3) * 4 ||
        !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(result.audioBase64)) return null;
    const audio = Buffer.from(result.audioBase64, 'base64');
    let valid = audio.toString('base64') === result.audioBase64;
    if (result.format === 'pcm') {
        valid &&= audio.length >= 2 && audio.length <= maxPcmBytes && audio.length % 2 === 0;
    } else if (result.format === 'wav') {
        valid &&= audio.length >= 46 && audio.length <= maxPcmBytes + 44 &&
            audio.toString('ascii', 0, 4) === 'RIFF' && audio.readUInt32LE(4) === audio.length - 8 &&
            audio.toString('ascii', 8, 16) === 'WAVEfmt ' && audio.readUInt32LE(16) === 16 &&
            audio.readUInt16LE(20) === 1 && audio.readUInt16LE(22) === 1 && audio.readUInt32LE(24) === 24000 &&
            audio.readUInt32LE(28) === 48000 && audio.readUInt16LE(32) === 2 && audio.readUInt16LE(34) === 16 &&
            audio.toString('ascii', 36, 40) === 'data' && audio.readUInt32LE(40) === audio.length - 44 &&
            (audio.length - 44) % 2 === 0;
    } else valid = false;
    if (!valid) { audio.fill(0); return null; }
    return { audio, format: result.format, sampleRateHz: 24000, channels: 1, voiceId: result.voiceId, locale: result.locale };
}
