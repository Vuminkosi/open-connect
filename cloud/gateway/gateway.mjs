// The data plane stays unavailable until admission, quota, and worker routing are configured.
export function handle(request) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/health/live') {
        return Response.json({ status: 'live' }, { headers: { 'cache-control': 'no-store' } });
    }
    const traceId = crypto.randomUUID();
    return Response.json({
        type: 'about:blank', title: 'Cloud gateway is not ready', status: 503,
        code: 'capability_unavailable', traceId,
    }, { status: 503, headers: { 'cache-control': 'no-store', 'content-type': 'application/problem+json', 'x-trace-id': traceId } });
}

export default { fetch: handle };
