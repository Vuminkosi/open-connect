const unavailable = () => ({ allowed: false, code: 'capability_unavailable' });

// Exact recognized word agreement is an additional restriction, not proof that
// a speech recognizer captured every sound in the audio.
function words(value) {
    if (typeof value !== 'string' || !value.isWellFormed()) return null;
    const normalized = value.normalize('NFKC').toLowerCase();
    if (/[^\p{L}\p{M}\p{N}\p{P}\p{Z}\s]/u.test(normalized)) return null;
    const result = normalized.match(/[\p{L}\p{M}\p{N}]+/gu);
    return result?.length ? result : null;
}

export function createVoiceOutputScreen({ transcriber, controlPlane }) {
    if (!transcriber || typeof transcriber.isReady !== 'function' || typeof transcriber.transcribe !== 'function' ||
        !controlPlane || typeof controlPlane.inspectOutput !== 'function') throw new TypeError('Invalid voice output screen configuration.');
    return Object.freeze({
        async isReady(signal, traceparent) {
            try { return !signal?.aborted && await transcriber.isReady(signal, traceparent) === true && !signal?.aborted; }
            catch { return false; }
        },
        async inspectAudio(input, signal, traceparent) {
            if (!input || signal?.aborted || !(input.audio instanceof Uint8Array) || input.sampleRateHz !== 24000 || input.channels !== 1)
                return unavailable();
            const original = words(input.request?.input?.text);
            if (!original) return unavailable();
            try {
                const result = await transcriber.transcribe({ requestId: input.request.requestId, audio: input.audio,
                    format: input.format, locale: input.request.input.locale }, signal, traceparent);
                const recognized = words(result?.transcript);
                if (!recognized || !/^[0-9a-f]{64}$/.test(result?.modelFingerprint ?? '') || signal?.aborted) return unavailable();
                const decision = await controlPlane.inspectOutput(input.credential, input.request.requestId, input.reservationId,
                    input.claimId, result.transcript, result.modelFingerprint, signal, traceparent);
                if (signal?.aborted) return unavailable();
                if (decision?.allowed !== true || decision.code !== 'allowed')
                    return decision?.code === 'privacy_blocked' ? { allowed: false, code: 'privacy_blocked' } : unavailable();
                if (original.length !== recognized.length || original.some((word, index) => word !== recognized[index])) return unavailable();
                return { allowed: true, code: 'allowed' };
            } catch { return unavailable(); }
        },
    });
}
