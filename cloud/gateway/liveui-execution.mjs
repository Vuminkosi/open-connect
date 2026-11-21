import { withLiveUIAdmission } from './liveui-admission.mjs';

const unavailable = () => ({ status: 'unavailable' });

// The worker settles before returning; output inspection and access checks precede release.
export async function executeLiveUI(request, { controlPlane, authorization, keyring, dispatcher, createAccessMonitor }) {
    if (!controlPlane || typeof controlPlane.inspectOutput !== 'function' || !dispatcher ||
        typeof dispatcher.executeClaimed !== 'function' || typeof createAccessMonitor !== 'function') return unavailable();
    return withLiveUIAdmission(request, { controlPlane, authorization, keyring }, async claim => {
        let monitor;
        try {
            monitor = createAccessMonitor({ controlPlane, credential: request.credential, reservationId: claim.reservationId,
                claimId: claim.claimId, signal: claim.signal, traceparent: claim.traceparent });
            if (!monitor || typeof monitor.start !== 'function' || typeof monitor.recheck !== 'function' ||
                typeof monitor.dispose !== 'function' || !monitor.signal || !await monitor.start() || monitor.signal.aborted) return unavailable();
            const response = await dispatcher.executeClaimed({ payload: claim.payload, key: claim.key, reservationId: claim.reservationId,
                claimId: claim.claimId, request: claim.request, modelId: request.modelId }, monitor.signal, claim.traceparent);
            if (monitor.signal.aborted || response?.status !== 'completed' || !response.result?.plan || !await monitor.recheck()) return unavailable();
            const plan = response.result.plan;
            const fields = [plan.schemaVersion, ...plan.actions.flatMap(action =>
                [action.type, action.target, ...(action.value == null ? [] : [action.value])])];
            const decision = await controlPlane.inspectOutput(request.credential, claim.request.requestId, claim.reservationId,
                claim.claimId, fields, monitor.signal, claim.traceparent);
            if (monitor.signal.aborted || !await monitor.recheck()) return unavailable();
            if (decision?.allowed !== true || decision.code !== 'allowed')
                return { status: decision?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
            return { status: 'completed', result: response.result };
        } catch { return unavailable(); }
        finally { monitor?.dispose(); }
    });
}
