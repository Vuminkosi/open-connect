import { createServer } from 'node:http';
import { handle } from './gateway.mjs';

export function createLocalServer(authorization, classificationProbe) {
    const server = createServer(async (request, response) => {
        try {
            const origin = `http://localhost:${process.env.PORT || '8091'}`;
            const authHeaders = request.rawHeaders.filter((value, index) => index % 2 === 0 && value.toLowerCase() === 'authorization');
            const headers = new Headers();
            if (authHeaders.length === 1 && request.headers.authorization) headers.set('authorization', request.headers.authorization);
            const controller = new AbortController();
            request.once('aborted', () => controller.abort());
            response.once('close', () => { if (!response.writableEnded) controller.abort(); });
            const result = await handle({ url: new URL(request.url || '/', origin).href, method: request.method, headers, signal: controller.signal }, { authorization, classificationProbe });
            // No Cloud request body is needed while admission is unavailable.
            response.setHeader('connection', 'close');
            response.writeHead(result.status, Object.fromEntries(result.headers));
            response.end(Buffer.from(await result.arrayBuffer()));
        } catch {
            const traceId = crypto.randomUUID();
            response.writeHead(400, { 'content-type': 'application/problem+json', 'connection': 'close', 'cache-control': 'no-store', 'x-trace-id': traceId });
            response.end(JSON.stringify({ type: 'about:blank', title: 'Invalid request', status: 400, code: 'invalid_request', traceId }));
        }
    });
    server.headersTimeout = 10000;
    server.requestTimeout = 15000;
    return server;
}
