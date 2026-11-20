import { prepareClassificationRequest } from './classification-request.mjs';

const routes = new Map([
    ['/cloud/v1/inference/chat', 'inference.chat'],
    ['/cloud/v1/liveui/plan', 'inference.liveui'],
    ['/cloud/v1/security/inspect', 'security.inspect'],
    ['/cloud/v1/classification', 'classification.classify'],
    ['/cloud/v1/transcription', 'speech.transcribe'],
    ['/cloud/v1/voice/synthesis', 'voice.synthesize'],
    ['/cloud/v1/translation', 'translation.translate'],
    ['/cloud/v1/navigator/jobs', 'navigator.execute'],
    ['/cloud/v1/agents/jobs', 'agents.execute'],
    ['/cloud/v1/outbox/events', 'outbox.publish'],
]);

const problems = {
    unauthorized: [401, 'Authentication required'],
    forbidden: [403, 'Access denied'],
    capability_not_entitled: [403, 'Capability not entitled'],
    capability_disabled: [403, 'Capability disabled'],
    dependency_not_enabled: [403, 'Capability dependency unavailable'],
    invalid_request: [400, 'Invalid request'],
    invalid_semantic_request: [422, 'Invalid classification request'],
    payload_too_large: [413, 'Request body is too large'],
    unsupported_media_type: [415, 'Unsupported media type'],
    privacy_blocked: [403, 'Privacy policy blocked the request'],
    capability_unavailable: [503, 'Cloud gateway is not ready'],
};

function problem(code) {
    const safeCode = Object.hasOwn(problems, code) ? code : 'capability_unavailable';
    const [status, title] = problems[safeCode];
    const traceId = crypto.randomUUID();
    const publicCode = ['invalid_semantic_request', 'payload_too_large', 'unsupported_media_type'].includes(safeCode) ? 'invalid_request' : safeCode;
    return Response.json({ type: 'about:blank', title, status, code: publicCode, traceId },
        { status, headers: { 'cache-control': 'no-store', 'content-type': 'application/problem+json', 'x-trace-id': traceId } });
}

export async function handle(request, { authorization, classificationProbe } = {}) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/health/live')
        return Response.json({ status: 'live' }, { headers: { 'cache-control': 'no-store' } });
    // Authorization alone is insufficient: dispatch, privacy, and quota gates are not connected.
    if (request.method === 'GET' && url.pathname === '/health/ready')
        return problem('capability_unavailable');
    if (!authorization) return problem('capability_unavailable');
    const capability = request.method === 'POST' && !url.search ? routes.get(url.pathname) : undefined;
    if (!capability) return problem('invalid_request');
    const header = request.headers?.get('authorization');
    const match = typeof header === 'string' ? /^Bearer ([^\s,]{1,160})$/i.exec(header) : null;
    if (!match) return problem('unauthorized');
    let decision;
    try { decision = await authorization.authorize(match[1], capability, request.signal); }
    catch { return problem('capability_unavailable'); }
    if (decision?.authorized !== true) return problem(decision?.code);
    if (capability === 'classification.classify' && classificationProbe) {
        try {
            const readiness = await classificationProbe.probe(request.signal);
            if (readiness?.ready !== true) return problem('capability_unavailable');
            const prepared = await prepareClassificationRequest(request);
            if (prepared.code) return problem(prepared.code);
            if (!Object.hasOwn(prepared.value.input, 'text') || typeof authorization.inspectClassification !== 'function')
                return problem('capability_unavailable');
            const privacy = await authorization.inspectClassification(match[1], prepared.value.requestId, prepared.value.input.text, request.signal);
            if (privacy?.allowed !== true) return problem(privacy?.code === 'privacy_blocked' ? 'privacy_blocked' : 'capability_unavailable');
        }
        catch { /* Readiness errors never enable execution or expose diagnostics. */ }
    }
    // Authorization alone never permits execution: quota, privacy and routing are not connected.
    return problem('capability_unavailable');
}

export default { fetch: handle };
