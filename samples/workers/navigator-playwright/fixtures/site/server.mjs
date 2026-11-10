import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const FILES = {
  '/': 'index.html',
  '/index.html': 'index.html',
  '/page2.html': 'page2.html',
  '/secret-form.html': 'secret-form.html',
};

export function createSyntheticSiteServer(options = {}) {
  const evilRedirectOrigin = options.evilRedirectOrigin || 'https://evil-unapproved.example.org';
  return http.createServer((req, res) => {
    const u = new URL(req.url || '/', 'http://localhost');
    if (u.pathname === '/health') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ status: 'ok', service: 'synthetic-site' }));
      return;
    }
    if (u.pathname === '/redirect-cross-origin') {
      res.writeHead(302, { location: `${evilRedirectOrigin}/steal` });
      res.end();
      return;
    }
    if (u.pathname === '/redirect-loopback') {
      res.writeHead(302, { location: 'http://127.0.0.1:8080/internal/v1/capabilities' });
      res.end();
      return;
    }
    const rel = FILES[u.pathname];
    if (!rel) {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      res.end('not found');
      return;
    }
    const body = fs.readFileSync(path.join(__dirname, rel), 'utf8');
    res.writeHead(200, {
      'content-type': 'text/html; charset=utf-8',
      'cache-control': 'no-store',
    });
    res.end(body);
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  const port = Number(process.env.SITE_PORT || 8090);
  const srv = createSyntheticSiteServer();
  srv.listen(port, '0.0.0.0', () => {
    console.log(`synthetic-site listening on :${port}`);
  });
}
