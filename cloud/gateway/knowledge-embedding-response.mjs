import { parseUniqueJson } from './classification-request.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const exact = (value, keys) => object(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const digest = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const identifier = value => typeof value === 'string' && value.trim().length > 0 && [...value].length <= 256 &&
    !/[\u0000-\u001f\u007f]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value);

// Validity is not permission to disclose: output privacy inspection and settlement remain mandatory.
export function validateKnowledgeEmbeddingResponse(bytes, request) {
    try {
        if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 8 * 1024 * 1024 ||
            !object(request) || typeof request.requestId !== 'string' || !object(request.input) ||
            !Array.isArray(request.input.texts) || request.input.texts.length < 1 || request.input.texts.length > 32) return null;
        const envelope = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
        if (!exact(envelope, ['schemaVersion', 'taskId', 'kind', 'result']) ||
            envelope.schemaVersion !== 'openconnect.v1' || envelope.taskId !== request.requestId || envelope.kind !== 'knowledge.embed') return null;
        const result = envelope.result;
        if (!exact(result, ['items', 'model_id', 'fingerprint', 'dimension', 'space_id']) ||
            !identifier(result.model_id) || !digest(result.fingerprint) || !digest(result.space_id) ||
            !Number.isInteger(result.dimension) || result.dimension < 1 || result.dimension > 8192 ||
            !Array.isArray(result.items) || result.items.length !== request.input.texts.length) return null;
        for (const item of result.items) {
            if (!exact(item, ['vector', 'truncated', 'norm']) || typeof item.truncated !== 'boolean' ||
                typeof item.norm !== 'number' || !Number.isFinite(item.norm) || item.norm < 0 ||
                !Array.isArray(item.vector) || item.vector.length !== result.dimension ||
                item.vector.some(value => typeof value !== 'number' || !Number.isFinite(value) || Math.abs(value) > 1.001)) return null;
            const norm = Math.sqrt(item.vector.reduce((sum, value) => sum + value * value, 0));
            if (!Number.isFinite(norm) || Math.abs(norm - 1) > 0.001 || Math.abs(item.norm - norm) > 0.001) return null;
        }
        return envelope;
    } catch { return null; }
}
