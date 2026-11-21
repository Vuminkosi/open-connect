import { createServer } from 'node:http';
import { handle } from './gateway.mjs';
import { createTraceContext } from './trace-context.mjs';

export function createLocalServer(authorization, classificationProbe, classificationExecution, inferenceExecution, liveUIExecution) {
    const server = createServer(async (request, response) => {
        try {
            const origin = `http://localhost:${process.env.PORT || '8091'}`;
            const authHeaders = request.rawHeaders.filter((value, index) => index % 2 === 0 && value.toLowerCase() === 'authorization');
            const headers = new Headers();
            if (authHeaders.length === 1 && request.headers.authorization) headers.set('authorization', request.headers.authorization);
            for (const name of ['content-type', 'content-encoding', 'content-length', 'traceparent']) {
                const count = request.rawHeaders.filter((value, index) => index % 2 === 0 && value.toLowerCase() === name).length;
                if (count === 1 && request.headers[name]) headers.set(name, request.headers[name]);
                else if (count > 1) headers.set(name, 'invalid-duplicate-header');
            }
            const controller = new AbortController();
            request.once('aborted', () => controller.abort());
            response.once('close', () => { if (!response.writableEnded) controller.abort(); });
            const readBody = async limit => {
                const chunks = [];
                let size = 0;
                try {
                    for await (const chunk of request.iterator({ destroyOnReturn: false })) {
                        chunks.push(chunk); size += chunk.length;
                        if (size > limit) return { tooLarge: true };
                    }
                    return { bytes: Buffer.concat(chunks, size) };
                } finally { for (const chunk of chunks) chunk.fill(0); }
            };
            const result = await handle({ url: new URL(request.url || '/', origin).href, method: request.method, headers, signal: controller.signal, readBody }, { authorization, classificationProbe, classificationExecution, inferenceExecution, liveUIExecution });
            response.setHeader('connection', 'close');
            response.writeHead(result.status, Object.fromEntries(result.headers));
            response.end(Buffer.from(await result.arrayBuffer()));
        } catch {
            const traceId = createTraceContext().traceId;
            response.writeHead(400, { 'content-type': 'application/problem+json', 'connection': 'close', 'cache-control': 'no-store', 'x-trace-id': traceId });
            response.end(JSON.stringify({ type: 'about:blank', title: 'Invalid request', status: 400, code: 'invalid_request', traceId }));
        }
    });
    server.headersTimeout = 10000;
    server.requestTimeout = 15000;
    return server;
}
