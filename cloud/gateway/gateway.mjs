import { withClassificationRequest } from './classification-request.mjs';
import { createTraceContext } from './trace-context.mjs';
import { executeClassification } from './classification-execution.mjs';
import { withInferenceRequest } from './inference-request.mjs';
import { executeInference } from './inference-execution.mjs';
import { withLiveUIRequest } from './liveui-request.mjs';
import { executeLiveUI } from './liveui-execution.mjs';
import { withVoiceSynthesisRequest } from './voice-synthesis-request.mjs';
import { executeVoiceSynthesis } from './voice-synthesis-execution.mjs';

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
    invalid_semantic_request: [422, 'Invalid capability request'],
    payload_too_large: [413, 'Request body is too large'],
    unsupported_media_type: [415, 'Unsupported media type'],
    privacy_blocked: [403, 'Privacy policy blocked the request'],
    upstream_timeout: [504, 'Request deadline exceeded'],
    quota_exceeded: [429, 'Quota exceeded'],
    duplicate: [409, 'Request already submitted'],
    idempotency_conflict: [409, 'Request identifier conflicts'],
    capability_unavailable: [503, 'Cloud gateway is not ready'],
};

function problem(code, traceId) {
    const safeCode = Object.hasOwn(problems, code) ? code : 'capability_unavailable';
    const [status, title] = problems[safeCode];
    const publicCode = ['invalid_semantic_request', 'payload_too_large', 'unsupported_media_type'].includes(safeCode) ? 'invalid_request' : safeCode === 'duplicate' ? 'conflict' : safeCode;
    return Response.json({ type: 'about:blank', title, status, code: publicCode, traceId },
        { status, headers: { 'cache-control': 'no-store', 'content-type': 'application/problem+json', 'x-trace-id': traceId } });
}

export async function handle(request, { authorization, classificationProbe, classificationExecution, inferenceExecution, liveUIExecution, voiceExecution } = {}) {
    const trace = createTraceContext(request.headers?.get('traceparent'));
    const reject = code => problem(code, trace.traceId);
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/' && !url.search)
        return Response.json({ service: 'OpenConnect Cloud Gateway', description: 'Cloud capability API',
            readiness: '/health/ready', liveness: '/health/live' }, { headers: { 'cache-control': 'no-store' } });
    if (request.method === 'GET' && url.pathname === '/health/live')
        return Response.json({ status: 'live' }, { headers: { 'cache-control': 'no-store' } });
    if (request.method === 'GET' && url.pathname === '/health/ready') {
        if ((!classificationExecution && !inferenceExecution && !liveUIExecution && !voiceExecution) || !authorization || typeof authorization.isReady !== 'function' ||
            (classificationExecution && !classificationProbe))
            return reject('capability_unavailable');
        try {
            const signal = request.signal ? AbortSignal.any([request.signal, AbortSignal.timeout(60000)]) : AbortSignal.timeout(60000);
            const [controlReady, classificationReady, inferenceReady, tokenizerReady, liveUIReady, voiceReady, voiceScreenReady] = await Promise.all([
                authorization.isReady(signal, trace.traceparent),
                classificationExecution ? classificationProbe.probe(signal, trace.traceparent, true, classificationExecution.deploymentId) : null,
                inferenceExecution ? inferenceExecution.probe.probe(signal, trace.traceparent) : null,
                inferenceExecution ? inferenceExecution.tokenizer.ready(signal) : null,
                liveUIExecution ? liveUIExecution.probe.probe(signal, trace.traceparent) : null,
                voiceExecution ? voiceExecution.probe.probe(signal, trace.traceparent) : null,
                voiceExecution ? voiceExecution.outputScreen.isReady(signal, trace.traceparent) : null,
            ]);
            if (signal.aborted || controlReady !== true || (classificationExecution && classificationReady?.ready !== true) ||
                (inferenceExecution && (inferenceReady !== true || tokenizerReady !== true)) ||
                (liveUIExecution && liveUIReady !== true) ||
                (voiceExecution && (voiceReady !== true || voiceScreenReady !== true))) return reject('capability_unavailable');
            const capabilities = [];
            if (classificationExecution) capabilities.push('classification.classify');
            if (inferenceExecution) capabilities.push('inference.chat');
            if (liveUIExecution) capabilities.push('inference.liveui');
            if (voiceExecution) capabilities.push('voice.synthesize');
            return Response.json({ status: 'ready', capabilities }, { headers: { 'cache-control': 'no-store', 'x-trace-id': trace.traceId } });
        } catch { return reject('capability_unavailable'); }
    }
    if (!authorization) return reject('capability_unavailable');
    const capability = request.method === 'POST' && !url.search ? routes.get(url.pathname) : undefined;
    if (!capability) return reject('invalid_request');
    const header = request.headers?.get('authorization');
    const match = typeof header === 'string' ? /^Bearer ([^\s,]{1,160})$/i.exec(header) : null;
    if (!match) return reject('unauthorized');
    let decision;
    try { decision = await authorization.authorize(match[1], capability, request.signal, trace.traceparent); }
    catch { return reject('capability_unavailable'); }
    if (decision?.authorized !== true) return reject(decision?.code);
    if (capability === 'voice.synthesize' && voiceExecution) {
        try {
            if (!await voiceExecution.probe.probe(request.signal, trace.traceparent)) return reject('capability_unavailable');
            const outcome = await withVoiceSynthesisRequest(request, async (value, body) => {
                const remaining = Date.parse(value.expiresAt) - Date.now();
                if (remaining <= 0) return reject('upstream_timeout');
                const deadline = AbortSignal.timeout(remaining);
                const signal = request.signal ? AbortSignal.any([request.signal, deadline]) : deadline;
                const result = await executeVoiceSynthesis({ credential: match[1], identity: decision.identity, body,
                    deployment: voiceExecution.deployment, modelId: voiceExecution.modelId, signal, traceparent: trace.traceparent }, voiceExecution);
                const audio = result?.result?.audio;
                try {
                    if (deadline.aborted || Date.now() >= Date.parse(value.expiresAt)) return reject('upstream_timeout');
                    if (signal.aborted) return reject('capability_unavailable');
                    if (result.status !== 'completed' || !(audio instanceof Uint8Array)) return reject(result.status);
                    return Response.json({ schemaVersion: 'openconnect.cloud.voice-synthesis.result/v1', requestId: value.requestId,
                        traceId: trace.traceId, result: { format: result.result.format, sampleRateHz: result.result.sampleRateHz,
                            channels: result.result.channels, voiceId: result.result.voiceId, locale: result.result.locale,
                            audioBase64: Buffer.from(audio.buffer, audio.byteOffset, audio.byteLength).toString('base64') } },
                    { headers: { 'cache-control': 'no-store', 'x-trace-id': trace.traceId } });
                } finally { if (audio instanceof Uint8Array) audio.fill(0); }
            });
            return outcome instanceof Response ? outcome : reject(outcome.code);
        } catch { return reject('capability_unavailable'); }
    }
    if (capability === 'inference.chat' && inferenceExecution) {
        try {
            if (!await inferenceExecution.probe.probe(request.signal, trace.traceparent)) return reject('capability_unavailable');
            const outcome = await withInferenceRequest(request, async (value, body) => {
                if (value.input.stream) return reject('capability_unavailable');
                const remaining = Date.parse(value.expiresAt) - Date.now();
                if (remaining <= 0) return reject('upstream_timeout');
                const deadline = AbortSignal.timeout(remaining);
                const signal = request.signal ? AbortSignal.any([request.signal, deadline]) : deadline;
                const result = await executeInference({ credential: match[1], identity: decision.identity, body,
                    deployment: inferenceExecution.deployment, signal, traceparent: trace.traceparent },
                { ...inferenceExecution, controlPlane: authorization });
                if (deadline.aborted || Date.now() >= Date.parse(value.expiresAt)) return reject('upstream_timeout');
                if (signal.aborted) return reject('capability_unavailable');
                if (result.status !== 'completed') return reject(result.status);
                return Response.json({ schemaVersion: 'openconnect.cloud.inference.result/v1', requestId: value.requestId,
                    modelId: value.modelId, traceId: trace.traceId, output: result.result.output, usage: result.result.usage },
                { headers: { 'cache-control': 'no-store', 'x-trace-id': trace.traceId } });
            });
            return outcome instanceof Response ? outcome : reject(outcome.code);
        } catch { return reject('capability_unavailable'); }
    }
    if (capability === 'inference.liveui' && liveUIExecution) {
        try {
            if (!await liveUIExecution.probe.probe(request.signal, trace.traceparent)) return reject('capability_unavailable');
            const outcome = await withLiveUIRequest(request, async (value, body) => {
                const remaining = Date.parse(value.expiresAt) - Date.now();
                if (remaining <= 0) return reject('upstream_timeout');
                const deadline = AbortSignal.timeout(remaining);
                const signal = request.signal ? AbortSignal.any([request.signal, deadline]) : deadline;
                const result = await executeLiveUI({ credential: match[1], identity: decision.identity, body,
                    deployment: liveUIExecution.deployment, modelId: liveUIExecution.modelId, tokenAmount: liveUIExecution.tokenAmount,
                    signal, traceparent: trace.traceparent },
                { ...liveUIExecution, authorization });
                if (deadline.aborted || Date.now() >= Date.parse(value.expiresAt)) return reject('upstream_timeout');
                if (signal.aborted) return reject('capability_unavailable');
                if (result.status !== 'completed') return reject(result.status);
                return Response.json({ schemaVersion: 'openconnect.cloud.liveui.result/v1', requestId: value.requestId,
                    traceId: trace.traceId, plan: result.result.plan, provider: result.result.provider },
                { headers: { 'cache-control': 'no-store', 'x-trace-id': trace.traceId } });
            });
            return outcome instanceof Response ? outcome : reject(outcome.code);
        } catch { return reject('capability_unavailable'); }
    }
    if (capability === 'classification.classify' && classificationProbe) {
        try {
            if (typeof authorization.matchClassificationDeployment !== 'function' ||
                !await authorization.matchClassificationDeployment(match[1], classificationProbe.deployment, request.signal, trace.traceparent))
                return reject('capability_unavailable');
            const readiness = await classificationProbe.probe(request.signal, trace.traceparent, !!classificationExecution, classificationExecution?.deploymentId);
            if (readiness?.ready !== true) return reject('capability_unavailable');
            const outcome = await withClassificationRequest(request, async (value, body) => {
                if (!Array.isArray(readiness.labelSets) || !readiness.labelSets.includes(value.configuration?.labelSetVersion ?? 'v1'))
                    return reject('capability_unavailable');
                if (!Object.hasOwn(value.input, 'text') || typeof authorization.inspectClassification !== 'function')
                    return reject('capability_unavailable');
                const remaining = value.expiresAt ? Date.parse(value.expiresAt) - Date.now() : null;
                if (remaining !== null && remaining <= 0) return reject('upstream_timeout');
                const deadline = remaining === null ? null : AbortSignal.timeout(remaining);
                const signal = deadline ? (request.signal ? AbortSignal.any([request.signal, deadline]) : deadline) : request.signal;
                let privacy;
                try { privacy = await authorization.inspectClassification(match[1], value.requestId, value.input.text, signal, trace.traceparent); }
                catch (error) {
                    if (deadline && (deadline.aborted || Date.now() >= Date.parse(value.expiresAt))) return reject('upstream_timeout');
                    throw error;
                }
                if (deadline && (deadline.aborted || Date.now() >= Date.parse(value.expiresAt))) return reject('upstream_timeout');
                if (request.signal?.aborted) return reject('capability_unavailable');
                if (privacy?.allowed !== true) return reject(privacy?.code === 'privacy_blocked' ? 'privacy_blocked' : 'capability_unavailable');
                if (!classificationExecution || value.schemaVersion !== 'openconnect.cloud.classification/v2') return reject('capability_unavailable');
                const result = await executeClassification({ credential: match[1], identity: decision.identity, value, body,
                    deployment: classificationProbe.deployment, signal, traceparent: trace.traceparent },
                { ...classificationExecution, controlPlane: authorization });
                if (deadline && (deadline.aborted || Date.now() >= Date.parse(value.expiresAt))) return reject('upstream_timeout');
                if (result.status !== 'completed') return reject(result.status);
                return Response.json({ schemaVersion: 'openconnect.cloud.classification.result/v1', requestId: value.requestId,
                    traceId: trace.traceId, result: result.result }, { headers: { 'cache-control': 'no-store', 'x-trace-id': trace.traceId } });
            });
            return outcome instanceof Response ? outcome : reject(outcome.code);
        }
        catch { /* Readiness errors never enable execution or expose diagnostics. */ }
    }
    // Other capabilities remain unavailable until their execution paths are connected.
    return reject('capability_unavailable');
}

export default { fetch: handle };
