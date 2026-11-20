import { createServer } from 'node:http';
import { handle } from './gateway.mjs';

export function createLocalServer() {
    const server = createServer(async (request, response) => {
        try {
            const origin = `http://localhost:${process.env.PORT || '8091'}`;
            const result = handle({ url: new URL(request.url || '/', origin).href, method: request.method });
            // No Cloud request body is needed while admission is unavailable.
            response.setHeader('connection', 'close');
            response.writeHead(result.status, Object.fromEntries(result.headers));
            response.end(Buffer.from(await result.arrayBuffer()));
        } catch {
            response.writeHead(400, { 'content-type': 'application/problem+json', 'connection': 'close', 'cache-control': 'no-store' });
            response.end(JSON.stringify({ title: 'Invalid request', status: 400, code: 'invalid_request' }));
        }
    });
    server.headersTimeout = 10000;
    server.requestTimeout = 15000;
    return server;
}
