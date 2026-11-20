import { parseUniqueJson } from './classification-request.mjs';

const guid = /^(?!00000000-0000-0000-0000-000000000000)[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, names) => object(value) && Object.keys(value).every(key => names.includes(key));
const text = (value, min, max) => typeof value === 'string' && [...value].length >= min && [...value].length <= max;
const supportedActions = new Set(['activate', 'navigate', 'setValue', 'select', 'highlight', 'focus', 'scroll', 'inspect']);

// The input matches the existing server-side LiveUI worker contract; the envelope adds Cloud admission fields.
export function parseLiveUIRequest(bytes, now = Date.now()) {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 131072) throw new TypeError('Invalid LiveUI request.');
    const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!only(value, ['schemaVersion', 'requestId', 'capability', 'expiresAt', 'input']) ||
        value.schemaVersion !== 'openconnect.cloud.liveui/v1' || typeof value.requestId !== 'string' || !guid.test(value.requestId) ||
        value.capability !== 'inference.liveui' || typeof value.expiresAt !== 'string' ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value.expiresAt) ||
        !Number.isFinite(Date.parse(value.expiresAt)) || new Date(value.expiresAt).toISOString() !== value.expiresAt ||
        !Number.isFinite(now) || Date.parse(value.expiresAt) <= now || Date.parse(value.expiresAt) > now + 300000 ||
        !only(value.input, ['schemaVersion', 'intent', 'context', 'generation']) || !text(value.input.schemaVersion, 1, 32) ||
        !text(value.input.intent, 1, 8000) || !only(value.input.context, ['route', 'availableActions']) ||
        !text(value.input.context.route, 1, 256) || !Array.isArray(value.input.context.availableActions) ||
        value.input.context.availableActions.length > 256 ||
        value.input.context.availableActions.some(item => !only(item, ['target', 'action', 'label']) || !text(item.target, 1, 256) ||
            !text(item.action, 1, 32) || (item.label !== undefined && item.label !== null && !text(item.label, 1, 256))) ||
        !only(value.input.generation, ['maxActions', 'maxOutputTokens']) || !Number.isInteger(value.input.generation.maxActions) ||
        value.input.generation.maxActions < 1 || value.input.generation.maxActions > 32 ||
        !Number.isInteger(value.input.generation.maxOutputTokens) || value.input.generation.maxOutputTokens < 16 || value.input.generation.maxOutputTokens > 512)
        throw new TypeError('Invalid LiveUI request.');
    for (const item of value.input.context.availableActions) Object.freeze(item);
    Object.freeze(value.input.context.availableActions);
    Object.freeze(value.input.context);
    Object.freeze(value.input.generation);
    Object.freeze(value.input);
    return Object.freeze(value);
}

// Every model-visible text field must be inspected, including labels and route metadata.
export function liveUIInspectionFields(request) {
    return [request.input.schemaVersion, request.input.intent, request.input.context.route,
        ...request.input.context.availableActions.flatMap(item => [item.target, item.action, ...(item.label == null ? [] : [item.label])])];
}

// This checks a proposal, never authorizes or executes browser actions.
export function validateLiveUIWorkerResponse(response, request) {
    if (!only(response, ['requestId', 'schemaVersion', 'provider', 'status', 'plan']) || response.requestId !== request.requestId ||
        response.schemaVersion !== request.input.schemaVersion || !['deterministic', 'qwen3-0.6b'].includes(response.provider) ||
        response.status !== 'completed' || !only(response.plan, ['schemaVersion', 'actions']) ||
        response.plan.schemaVersion !== request.input.schemaVersion || !Array.isArray(response.plan.actions) ||
        response.plan.actions.length < 1 || response.plan.actions.length > request.input.generation.maxActions) return null;
    const allowed = new Set(request.input.context.availableActions.map(item => JSON.stringify([item.target, item.action])));
    const actions = [];
    for (const action of response.plan.actions) {
        if (!only(action, ['type', 'target', 'value']) || !supportedActions.has(action.type) || !text(action.target, 1, 256) ||
            !allowed.has(JSON.stringify([action.target, action.type])) ||
            (['setValue', 'select'].includes(action.type) ? !text(action.value, 0, 256) : action.value !== undefined && action.value !== null)) return null;
        actions.push({ type: action.type, target: action.target, ...(['setValue', 'select'].includes(action.type) ? { value: action.value } : {}) });
    }
    return { schemaVersion: response.plan.schemaVersion, actions };
}
