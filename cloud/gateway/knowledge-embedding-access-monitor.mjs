// Periodic checks revoke access; they never grant a claim or authorize disclosure by themselves.
export function createKnowledgeEmbeddingAccessMonitor({ controlPlane, credential, identity, deployment, signal, traceparent, intervalMs = 1000 }) {
    if (!controlPlane || typeof controlPlane.authorize !== 'function' || typeof controlPlane.matchEmbeddingDeployment !== 'function' ||
        !identity || !deployment || !['credentialId', 'tenantId', 'applicationId', 'environment'].every(key => typeof identity[key] === 'string') ||
        !Number.isInteger(intervalMs) || intervalMs < 10 || intervalMs > 10000) throw new TypeError('Invalid access monitor.');
    const lifetime = new AbortController();
    const combined = signal ? AbortSignal.any([signal, lifetime.signal]) : lifetime.signal;
    let timer; let pending; let closed = false;
    async function check() {
        if (closed || combined.aborted) return false;
        try {
            const checkSignal = AbortSignal.any([combined, AbortSignal.timeout(2000)]);
            let onAbort;
            const aborted = new Promise(resolve => {
                onAbort = () => resolve(null);
                if (checkSignal.aborted) onAbort(); else checkSignal.addEventListener('abort', onAbort, { once: true });
            });
            let decision;
            try {
                decision = await Promise.race([Promise.all([
                    controlPlane.authorize(credential, 'knowledge.embed', checkSignal, traceparent),
                    controlPlane.matchEmbeddingDeployment(credential, deployment, checkSignal, traceparent),
                ]), aborted]);
            } finally { checkSignal.removeEventListener('abort', onAbort); }
            const [authorization, deploymentAllowed] = decision ?? [];
            const allowed = !checkSignal.aborted && authorization?.authorized === true && deploymentAllowed === true &&
                ['credentialId', 'tenantId', 'applicationId', 'environment'].every(key => authorization.identity?.[key] === identity[key]);
            if (!allowed && !closed) lifetime.abort();
            return allowed && !closed;
        } catch {
            if (!closed) lifetime.abort();
            return false;
        }
    }
    function schedule() {
        if (closed || combined.aborted) return;
        timer = setTimeout(() => { pending = check().then(ok => { if (ok) schedule(); return ok; }); }, intervalMs);
        timer.unref?.();
    }
    return {
        signal: combined,
        async start() { const ok = await check(); if (ok) schedule(); return ok; },
        async recheck() { clearTimeout(timer); if (pending) await pending; clearTimeout(timer); return check(); },
        dispose() { closed = true; clearTimeout(timer); lifetime.abort(); },
    };
}
