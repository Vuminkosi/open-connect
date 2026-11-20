import { createServer } from 'node:http';
import { handle } from './gateway.mjs';

const server = createServer(async (request, response) => {
    const origin = `http://localhost:${process.env.PORT || '8091'}`;
    const result = handle(new Request(new URL(request.url || '/', origin), { method: request.method }));
    response.writeHead(result.status, Object.fromEntries(result.headers));
    response.end(Buffer.from(await result.arrayBuffer()));
});
server.listen(Number(process.env.PORT || 8091), '0.0.0.0');
