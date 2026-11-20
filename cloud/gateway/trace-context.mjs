const validTraceparent = /^00-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})$/;

export function isValidTraceparent(value) {
    const match = typeof value === 'string' ? validTraceparent.exec(value) : null;
    return match !== null && !/^0+$/.test(match[1]) && !/^0+$/.test(match[2]);
}

export function createTraceContext(incoming) {
    const match = isValidTraceparent(incoming) ? validTraceparent.exec(incoming) : null;
    const traceId = match?.[1] ?? crypto.randomUUID().replaceAll('-', '');
    let spanId;
    do { spanId = Array.from(crypto.getRandomValues(new Uint8Array(8)), b => b.toString(16).padStart(2, '0')).join(''); }
    while (/^0+$/.test(spanId));
    return { traceId, traceparent: `00-${traceId}-${spanId}-${match?.[3] ?? '00'}` };
}
