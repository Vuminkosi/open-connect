import { withVoiceSynthesisAdmission } from './voice-synthesis-admission.mjs';

const unavailable = () => ({ status: 'unavailable' });

// A worker completion does not by itself authorize audio release.
export async function executeVoiceSynthesis(request, { controlPlane, keyring, dispatcher, createAccessMonitor, outputScreen }) {
    if (!request || !controlPlane || !dispatcher || typeof dispatcher.executeClaimed !== 'function' ||
        typeof createAccessMonitor !== 'function' || !outputScreen || typeof outputScreen.isReady !== 'function' ||
        typeof outputScreen.inspectAudio !== 'function' || request.signal?.aborted) return unavailable();
    const readinessSignal = request.signal ? AbortSignal.any([request.signal, AbortSignal.timeout(2000)]) : AbortSignal.timeout(2000);
    let onAbort;
    try {
        const aborted = new Promise(resolve => {
            onAbort = () => resolve(false);
            if (readinessSignal.aborted) onAbort(); else readinessSignal.addEventListener('abort', onAbort, { once: true });
        });
        if (await Promise.race([outputScreen.isReady(readinessSignal), aborted]) !== true || readinessSignal.aborted) return unavailable();
    } catch { return unavailable(); }
    finally { readinessSignal.removeEventListener('abort', onAbort); }
    return withVoiceSynthesisAdmission(request, { controlPlane, keyring }, async claim => {
        let monitor; let audio; let released = false;
        try {
            monitor = createAccessMonitor({ controlPlane, credential: request.credential, reservationId: claim.reservationId,
                claimId: claim.claimId, signal: claim.signal, traceparent: claim.traceparent });
            if (!monitor || typeof monitor.start !== 'function' || typeof monitor.recheck !== 'function' ||
                typeof monitor.dispose !== 'function' || !monitor.signal || !await monitor.start() || monitor.signal.aborted) return unavailable();
            const response = await dispatcher.executeClaimed({ payload: claim.payload, key: claim.key, reservationId: claim.reservationId,
                claimId: claim.claimId, request: claim.request }, monitor.signal, claim.traceparent);
            audio = response?.result?.audio;
            if (!(audio instanceof Uint8Array) || audio.byteLength < 1 || monitor.signal.aborted || response.status !== 'completed' ||
                !await monitor.recheck()) return unavailable();
            const decision = await outputScreen.inspectAudio({ credential: request.credential, request: claim.request,
                reservationId: claim.reservationId, claimId: claim.claimId, audio, format: response.result.format,
                sampleRateHz: response.result.sampleRateHz, channels: response.result.channels }, monitor.signal, claim.traceparent);
            if (monitor.signal.aborted || !await monitor.recheck()) return unavailable();
            if (decision?.allowed !== true || decision.code !== 'allowed')
                return { status: decision?.code === 'privacy_blocked' ? 'privacy_blocked' : 'unavailable' };
            released = true;
            return { status: 'completed', result: response.result };
        } catch { return unavailable(); }
        finally { if (!released && audio instanceof Uint8Array) audio.fill(0); monitor?.dispose(); }
    });
}
