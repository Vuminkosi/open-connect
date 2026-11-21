import net from 'node:net';

// The portal and this proxy share a network namespace. The browser reaches the
// same issuer through the loopback-published identity service on the host.
net.createServer((client) => {
  const upstream = net.connect(8443, 'local-identity');
  client.on('error', () => upstream.destroy());
  upstream.on('error', () => client.destroy());
  client.pipe(upstream).pipe(client);
}).listen(9443, '127.0.0.1');
