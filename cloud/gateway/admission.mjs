// Adapters are trusted infrastructure, not values supplied by a caller.
// A reservation remains the quota authority's responsibility until settled or expired.
export async function admit({ credential, capability, signal }, adapters) {
    if (!credential || !capability) return { allowed: false, code: 'unauthorized' };
    try {
        const identity = await adapters.authenticate(credential, signal);
        if (!identity) return { allowed: false, code: 'unauthorized' };
        const decision = await adapters.authorize(identity, capability, signal);
        if (decision?.allowed !== true) return { allowed: false, code: decision?.code ?? 'forbidden' };
        const deployment = await adapters.resolveDeployment(identity, capability, signal);
        if (!deployment) return { allowed: false, code: 'capability_unavailable' };
        const reservation = await adapters.reserve(identity, capability, signal);
        if (!reservation) return { allowed: false, code: 'quota_exceeded' };
        return { allowed: true, identity, deployment, reservation };
    } catch {
        return { allowed: false, code: 'capability_unavailable' };
    }
}
