import http from 'node:http';

let readinessCalls = 0;
let planCalls = 0;
let mode = process.env.OPENCONNECT_LIVEUI_TEST_INVALID_PLAN === '1' ? 'invalid' : 'unavailable';
const token = 'liveui-fallback-acceptance-token';
http.createServer((request, response) => {
  if (request.method === 'POST' && request.url === '/__healthy') {
    mode = 'healthy';
    readinessCalls = 0;
    planCalls = 0;
    response.writeHead(204);
    return response.end();
  }
  if (request.url === '/__stats') {
    response.writeHead(200, { 'content-type': 'application/json' });
    return response.end(JSON.stringify({ readinessCalls, planCalls, mode }));
  }
  if (request.headers.authorization !== `Bearer ${token}`) {
    response.writeHead(401);
    return response.end();
  }
  if (request.method === 'GET' && request.url === '/health/ready') {
    readinessCalls++;
    response.writeHead(200);
    return response.end();
  }
  if (request.method === 'POST' && request.url === '/v1/liveui/inference/plan') {
    planCalls++;
    if (mode === 'unavailable' || mode === 'invalid') {
      request.resume();
      response.writeHead(mode === 'invalid' ? 422 : 503);
      return response.end();
    }
    let body = '';
    request.setEncoding('utf8');
    request.on('data', (chunk) => { body += chunk; });
    request.on('end', () => {
      const input = JSON.parse(body);
      const target = input.context.availableActions.find((item) =>
        item.action === 'activate' && item.target.endsWith('-tab-completed'))?.target;
      if (!target) {
        response.writeHead(422);
        return response.end();
      }
      response.writeHead(200, { 'content-type': 'application/json' });
      return response.end(JSON.stringify({ requestId: input.requestId, schemaVersion: input.schemaVersion,
        provider: 'acceptance-worker', status: 'completed',
        plan: { schemaVersion: input.schemaVersion, actions: [{ type: 'activate', target }] } }));
    });
    return;
  }
  response.writeHead(404);
  return response.end();
}).listen(5311, '127.0.0.1');
