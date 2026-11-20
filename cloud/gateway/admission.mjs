// Adapters are trusted infrastructure. A reservation stays held until reconciled.
const authorizationErrors = new Set(['forbidden', 'capability_not_entitled', 'capability_disabled', 'capability_unavailable', 'dependency_not_enabled']);
const quotaErrors = new Map([
    ['quota_exceeded', 'quota_exceeded'],
    ['invalid_request', 'invalid_request'],
    ['idempotency_conflict', 'idempotency_conflict'],
    ['duplicate', 'conflict'],
]);

export async function admit({ credential, capability, signal }, adapters) {
    if (!credential) return { allowed: false, code: 'unauthorized' };
    if (!capability) return { allowed: false, code: 'invalid_request' };
    try {
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        const identity = await adapters.authenticate(credential, signal);
        if (!identity) return { allowed: false, code: 'unauthorized' };
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        const decision = await adapters.authorize(identity, capability, signal);
        if (decision?.allowed !== true)
            return { allowed: false, code: authorizationErrors.has(decision?.code) ? decision.code : 'forbidden' };
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        // Preparing the request may inspect its body, so it must follow authorization.
        const prepared = await adapters.prepare(identity, capability, signal);
        if (prepared?.valid !== true) return { allowed: false, code: 'invalid_request' };
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        const deployment = await adapters.resolveDeployment(identity, capability, prepared, signal);
        if (!deployment) return { allowed: false, code: 'capability_unavailable' };
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        const reservation = await adapters.reserve(identity, capability, prepared, signal);
        if (reservation?.status !== 'reserved' || typeof reservation.reservationId !== 'string' ||
            !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(reservation.reservationId) ||
            reservation.reservationId === '00000000-0000-0000-0000-000000000000')
            return { allowed: false, code: quotaErrors.get(reservation?.status) ?? 'capability_unavailable' };
        // An ambiguous cancellation after reservation must not authorize dispatch or release quota.
        if (signal?.aborted) return { allowed: false, code: 'capability_unavailable' };
        return { allowed: true, identity, deployment, reservation, prepared };
    } catch {
        return { allowed: false, code: 'capability_unavailable' };
    }
}
