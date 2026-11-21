import { parseTranslationRequest } from './translation-request.mjs';

const only = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).length === keys.length && Object.keys(value).every(key => keys.includes(key));

// Bind these exact bytes to a claim after admission and privacy inspection.
// The caller owns the returned payload and must clear it after use.
export function prepareTranslationWorkerPayload(request, now = Date.now()) {
    let snapshot;
    let payload;
    try {
        snapshot = Buffer.from(JSON.stringify(request), 'utf8');
        const input = parseTranslationRequest(snapshot, now);
        payload = Buffer.from(JSON.stringify({ schemaVersion: 'openconnect.v1', taskId: input.requestId,
            input: { text: input.input.text, sourceLang: input.input.sourceLang, targetLang: input.input.targetLang } }), 'utf8');
        if (payload.length > 65536) { payload.fill(0); return null; }
        return { payload, requestId: input.requestId };
    } catch { payload?.fill(0); return null; }
    finally { snapshot?.fill(0); }
}

// Worker output is untrusted and must still pass output privacy inspection.
export function validateTranslationWorkerResponse(value, request, deployment) {
    if (!request || typeof request.requestId !== 'string' || !request.input ||
        typeof request.input.sourceLang !== 'string' || typeof request.input.targetLang !== 'string' ||
        !deployment || typeof deployment.modelRevision !== 'string' || !deployment.modelRevision.trim() ||
        deployment.modelRevision.length > 256 || typeof deployment.tokenizerHash !== 'string' ||
        !/^[0-9a-f]{64}$/.test(deployment.tokenizerHash) ||
        !only(value, ['schemaVersion', 'taskId', 'status', 'result', 'error']) ||
        value.schemaVersion !== 'openconnect.v1' || value.taskId !== request.requestId ||
        value.status !== 'completed' || value.error !== null ||
        !only(value.result, ['translatedText', 'sourceLang', 'targetLang', 'modelRevision', 'tokenizerHash', 'engine', 'engineWarning'])) return null;
    const result = value.result;
    if (result.sourceLang !== request.input.sourceLang || result.targetLang !== request.input.targetLang ||
        result.modelRevision !== deployment.modelRevision || result.tokenizerHash !== deployment.tokenizerHash ||
        result.engine !== 'real' || result.engineWarning !== null || typeof result.translatedText !== 'string' ||
        !result.translatedText.isWellFormed() || !result.translatedText.trim() || [...result.translatedText].length > 20000) return null;
    return { translatedText: result.translatedText, sourceLang: result.sourceLang, targetLang: result.targetLang,
        modelRevision: result.modelRevision, tokenizerHash: result.tokenizerHash };
}
