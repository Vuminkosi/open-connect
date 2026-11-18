import http from 'node:http';
import net from 'node:net';

let droppedReceipts = 0;
const proxy = http.createServer((request, response) => {
  if (request.url === '/__receipt-loss/stats') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ droppedReceipts }));
    return;
  }
  const chunks = [];
  let length = 0;
  request.on('data', (chunk) => {
    length += chunk.length;
    if (length > 1024 * 1024) request.destroy();
    else chunks.push(chunk);
  });
  request.on('end', () => {
    const body = Buffer.concat(chunks);
    if (request.method === 'POST' && /\/sessions\/[^/]+\/liveui\/responses$/.test(request.url ?? '') && droppedReceipts === 0) {
      try {
        if (JSON.parse(body.toString('utf8')).resultKind === 'action') {
          droppedReceipts++;
          response.writeHead(503);
          response.end();
          return;
        }
      } catch { /* Forward malformed input to the gateway for normal validation. */ }
    }
    const upstream = http.request({ hostname: '127.0.0.1', port: 5120, method: request.method,
      path: request.url, headers: request.headers }, (upstreamResponse) => {
      response.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.headers);
      upstreamResponse.pipe(response);
    });
    upstream.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end(); });
    upstream.end(body);
  });
});

proxy.on('upgrade', (request, client, head) => {
  const upstream = net.connect(5120, '127.0.0.1', () => {
    const headers = [];
    for (let index = 0; index < request.rawHeaders.length; index += 2)
      headers.push(`${request.rawHeaders[index]}: ${request.rawHeaders[index + 1]}`);
    upstream.write(`${request.method} ${request.url} HTTP/${request.httpVersion}\r\n${headers.join('\r\n')}\r\n\r\n`);
    if (head.length) upstream.write(head);
    client.pipe(upstream).pipe(client);
  });
  upstream.on('error', () => client.destroy());
  client.on('error', () => upstream.destroy());
});
proxy.listen(5130, '127.0.0.1');
