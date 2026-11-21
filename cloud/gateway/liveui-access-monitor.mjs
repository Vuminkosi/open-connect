// A claim-specific control-plane check withdraws access; it never grants a new claim.
export function createLiveUIAccessMonitor({ controlPlane, credential, reservationId, claimId, signal, traceparent, intervalMs = 1000 }) {
    if (!controlPlane || typeof controlPlane.verifyClaim !== 'function' || typeof credential !== 'string' ||
        !Number.isInteger(intervalMs) || intervalMs < 10 || intervalMs > 10000) throw new TypeError('Invalid LiveUI access monitor.');
    const lifetime = new AbortController();
    const combined = signal ? AbortSignal.any([signal, lifetime.signal]) : lifetime.signal;
    let timer; let pending; let closed = false;
    async function check() {
        if (closed || combined.aborted) return false;
        try {
            const checkSignal = AbortSignal.any([combined, AbortSignal.timeout(2000)]);
            let onAbort;
            const aborted = new Promise(resolve => {
                onAbort = () => resolve(false);
                if (checkSignal.aborted) onAbort(); else checkSignal.addEventListener('abort', onAbort, { once: true });
            });
            let allowed;
            try { allowed = await Promise.race([controlPlane.verifyClaim(credential, reservationId, claimId, checkSignal, traceparent), aborted]); }
            finally { checkSignal.removeEventListener('abort', onAbort); }
            allowed = allowed === true && !checkSignal.aborted && !closed;
            if (!allowed && !closed) lifetime.abort();
            return allowed;
        } catch { if (!closed) lifetime.abort(); return false; }
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

