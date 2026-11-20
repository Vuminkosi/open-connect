import { parseUniqueJson } from './classification-request.mjs';
import { withInferenceAdmission } from './inference-admission.mjs';

const unavailable = () => ({ status: 'unavailable' });

// A caller must supply a deployment-aware monitor that aborts on access or policy revocation.
// Output is returned only after the worker's settlement and independent Cloud output inspection.
export async function executeInference(request, { controlPlane, keyring, quote, dispatcher, createAccessMonitor }) {
    if (!controlPlane || typeof controlPlane.inspectInferenceOutput !== 'function' || !dispatcher ||
        typeof dispatcher.executeClaimed !== 'function' || typeof createAccessMonitor !== 'function') return unavailable();
    return withInferenceAdmission(request, { controlPlane, keyring, quote }, async claim => {
        let monitor;
        try {
            const value = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(claim.payload));
            if (!value || value.schemaVersion !== 'openconnect.cloud.inference.worker/v1' || value.modelId !== claim.modelId ||
                value.input?.stream !== false || !Number.isSafeInteger(value.input?.maxTokens)) return unavailable();
            monitor = createAccessMonitor({ controlPlane, credential: request.credential, identity: request.identity,
                deployment: request.deployment, modelId: claim.modelId, signal: claim.signal, traceparent: claim.traceparent });
            if (!monitor || typeof monitor.start !== 'function' || typeof monitor.recheck !== 'function' ||
                typeof monitor.dispose !== 'function' || !monitor.signal || !await monitor.start() || monitor.signal.aborted) return unavailable();
            const response = await dispatcher.executeClaimed({ payload: claim.payload, key: claim.key, reservationId: claim.reservationId,
                claimId: claim.claimId, requestId: value.requestId, modelId: claim.modelId,
                reservedTokens: claim.reservedTokens, maxOutputTokens: value.input.maxTokens }, monitor.signal, claim.traceparent);
            if (monitor.signal.aborted || response?.status !== 'completed' || !response.result || !await monitor.recheck()) return unavailable();
            const decision = await controlPlane.inspectInferenceOutput(request.credential, value.requestId, claim.reservationId,
                claim.claimId, response.result.output, monitor.signal, claim.traceparent);
            if (monitor.signal.aborted || !await monitor.recheck()) return unavailable();
            if (decision?.allowed !== true || decision.code !== 'allowed')
                return { status: decision?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
            return { status: 'completed', result: response.result };
        } catch { return unavailable(); }
        finally { monitor?.dispose(); }
    });
}
