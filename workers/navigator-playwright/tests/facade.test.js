'use strict';

const assert = require('node:assert/strict');
const { describe, it, before, after } = require('node:test');
const { setTimeout: sleep } = require('node:timers/promises');

const policy = require('../src/policy');
const { createServer, withFreshProfile } = require('../src/server');

const TOKEN = 'test-token-123';
process.env.NAVIGATOR_WORKER_TOKEN = TOKEN;
process.env.NAVIGATOR_ALLOWED_ORIGINS = 'https://example.com,https://app.example.com';
process.env.PLAYWRIGHT_LAUNCH = '';

function fakeLookup(host) {
  const table = {
    'example.com': [{ address: '93.184.216.34', family: 4 }],
    'app.example.com': [{ address: '93.184.216.34', family: 4 }],
    'evil.local': [{ address: '127.0.0.1', family: 4 }],
    'metadata.rebind.test': [{ address: '169.254.169.254', family: 4 }],
  };
  if (table[host]) return Promise.resolve(table[host]);
  return Promise.reject(Object.assign(new Error('ENOTFOUND'), { code: 'ENOTFOUND' }));
}

describe('policy', () => {
  it('denies non-http schemes (file/javascript/data)', () => {
    for (const u of ['file:///etc/passwd', 'javascript:alert(1)', 'data:text/html,hi', 'ftp://x/y']) {
      assert.throws(() => policy.originOf(u), /scheme not allowed/);
    }
  });

  it('denies loopback, metadata, and private IPs', () => {
    for (const ip of ['127.0.0.1', '0.0.0.0', '10.1.2.3', '192.168.1.1', '172.16.0.1', '169.254.169.254', '::1']) {
      assert.equal(policy.isBlockedIp(ip), true, ip);
    }
    assert.equal(policy.isBlockedIp('93.184.216.34'), false);
  });

  it('denies DNS that resolves to blocked IPs (rebind guard)', async () => {
    await assert.rejects(
      policy.assertHostResolvesToPublicIp('evil.local', fakeLookup),
      /destination address not allowed/,
    );
    await assert.rejects(
      policy.assertHostResolvesToPublicIp('metadata.rebind.test', fakeLookup),
      /destination address not allowed/,
    );
  });

  it('denies unapproved origins, allows approved ones', async () => {
    await assert.rejects(
      policy.assertNavigationAllowed('https://evil.test/x', ['https://example.com'], fakeLookup),
      /origin not approved/,
    );
    const origin = await policy.assertNavigationAllowed('https://example.com/a', ['https://example.com'], fakeLookup);
    assert.equal(origin, 'https://example.com');
  });

  it('blocks secret fills', () => {
    assert.equal(policy.isSecretFill({ targetId: 'fld-password', value: 'x' }), true);
    assert.equal(policy.isSecretFill({ targetId: 'fld-x', value: 'x', fieldType: 'password' }), true);
    assert.equal(policy.isSecretFill({ targetId: 'fld-x', value: 'x', isSecret: true }), true);
    assert.equal(policy.isSecretFill({ targetId: 'fld-search', value: 'hello' }), false);
  });

  it('redacts emails from captured text', () => {
    assert.equal(policy.redactText('contact bob@example.com now'), 'contact [redacted-email] now');
  });

  it('cleans up per-run profiles', async () => {
    const fs = require('node:fs');
    let seen = null;
    await withFreshProfile(async (dir) => {
      seen = dir;
      await fs.promises.writeFile(require('node:path').join(dir, 'probe'), 'x');
    });
    assert.equal(fs.existsSync(seen), false);
  });

  it('cleans up per-run profiles on crash and timeout and isolates cross-tenant profiles', async () => {
    const fs = require('node:fs');
    const path = require('node:path');
    let crashedDir = null;
    await assert.rejects(
      withFreshProfile(
        async (dir) => {
          crashedDir = dir;
          await fs.promises.writeFile(path.join(dir, 'secret-cookie'), 'leaked');
          throw new Error('simulated browser crash');
        },
        { tenantId: 'tenant-a', sessionId: 'sess-1' },
      ),
      /simulated browser crash/,
    );
    assert.ok(crashedDir.includes('tenant-a-sess-1'));
    assert.equal(fs.existsSync(crashedDir), false);

    let timedOutDir = null;
    await assert.rejects(
      withFreshProfile(
        async (dir) => {
          timedOutDir = dir;
          await sleep(150);
        },
        { tenantId: 'tenant-a', sessionId: 'sess-timeout', timeoutMs: 25 },
      ),
      /timed out/,
    );
    assert.equal(fs.existsSync(timedOutDir), false);

    const seenDirs = [];
    await Promise.all([
      withFreshProfile(
        async (dir) => {
          seenDirs.push(dir);
          await fs.promises.writeFile(path.join(dir, 'storage'), 'tenant-alpha');
          await sleep(15);
        },
        { tenantId: 'tenant-alpha', sessionId: 's1' },
      ),
      withFreshProfile(
        async (dir) => {
          seenDirs.push(dir);
          assert.equal(fs.existsSync(path.join(dir, 'storage')), false);
          await sleep(15);
        },
        { tenantId: 'tenant-beta', sessionId: 's1' },
      ),
    ]);
    assert.equal(seenDirs.length, 2);
    assert.notEqual(seenDirs[0], seenDirs[1]);
    assert.equal(fs.existsSync(seenDirs[0]), false);
    assert.equal(fs.existsSync(seenDirs[1]), false);
  });

  it('rejects redirect chains that hop to unapproved origins or DNS-rebind to private/metadata IPs', async () => {
    const allowed = ['https://example.com', 'https://app.example.com'];
    const okChain = await policy.assertRedirectChainAllowed(
      ['https://example.com/start', { url: 'https://app.example.com/final', ip: '93.184.216.34' }],
      allowed,
      fakeLookup,
    );
    assert.deepEqual(okChain, ['https://example.com', 'https://app.example.com']);

    await assert.rejects(
      policy.assertRedirectChainAllowed(
        ['https://example.com/start', 'https://metadata.rebind.test/latest'],
        ['https://example.com', 'https://metadata.rebind.test'],
        fakeLookup,
      ),
      /destination address not allowed/,
    );

    await assert.rejects(
      policy.assertRedirectChainAllowed(
        ['https://example.com/start', { url: 'https://app.example.com/final', ip: '169.254.169.254' }],
        allowed,
        fakeLookup,
      ),
      /destination address not allowed/,
    );
  });

  it('strips PNG/JPEG metadata chunks and redacts bounding-box pixels on capture bytes', () => {
    const zlib = require('node:zlib');
    const pngWithMeta = policy.createSolidPng(4, 4, [200, 100, 50, 255], [
      { type: 'tEXt', data: 'Author\0secret@example.com' },
      { type: 'iTXt', data: 'Comment\0\0\0\0\0internal-token-xyz' },
      { type: 'eXIf', data: 'MM\x00*\x00\x00\x00\x08' },
    ]);
    assert.ok(pngWithMeta.includes(Buffer.from('secret@example.com')));

    const scrubbed = policy.scrubCaptureBytes(pngWithMeta, {
      redactBoxes: [{ x: 0, y: 0, width: 2, height: 2 }],
    });
    assert.equal(scrubbed.metadataStripped, true);
    assert.equal(scrubbed.strippedChunks, 3);
    assert.equal(scrubbed.redactedBoxes, 1);
    assert.equal(scrubbed.bytes.includes(Buffer.from('secret@example.com')), false);
    assert.equal(scrubbed.bytes.includes(Buffer.from('internal-token-xyz')), false);
    assert.equal(scrubbed.bytes.includes(Buffer.from('tEXt')), false);
    assert.equal(scrubbed.bytes.includes(Buffer.from('iTXt')), false);
    assert.equal(scrubbed.bytes.includes(Buffer.from('eXIf')), false);

    // Verify IDAT pixels inside (0..1, 0..1) are zeroed while (3, 3) remains intact.
    const idatIdx = scrubbed.bytes.indexOf(Buffer.from('IDAT'));
    const idatLen = scrubbed.bytes.readUInt32BE(idatIdx - 4);
    const idatData = scrubbed.bytes.subarray(idatIdx + 4, idatIdx + 4 + idatLen);
    const rawScanlines = zlib.inflateSync(idatData);
    const stride = 4 * 4;
    // Pixel (0, 0) is redacted -> [0, 0, 0, 255]
    assert.deepEqual([...rawScanlines.subarray(1, 5)], [0, 0, 0, 255]);
    // Pixel (3, 3) is unredacted -> [200, 100, 50, 255]
    const row3 = 3 * (stride + 1) + 1 + 3 * 4;
    assert.deepEqual([...rawScanlines.subarray(row3, row3 + 4)], [200, 100, 50, 255]);

    // JPEG APP1 (EXIF) and COM segment stripping (JPEG segment length includes the 2 length bytes)
    const fakeJpeg = Buffer.concat([
      Buffer.from([0xff, 0xd8]), // SOI
      Buffer.from([0xff, 0xe1, 0x00, 0x0c, ...Buffer.from('ExifSecret')]), // APP1 (2 + 10 = 12)
      Buffer.from([0xff, 0xfe, 0x00, 0x08, ...Buffer.from('Secret')]), // COM (2 + 6 = 8)
      Buffer.from([0xff, 0xda, 0x00, 0x02, 0x11, 0x22, 0xff, 0xd9]), // SOS + EOI
    ]);
    const scrubbedJpeg = policy.scrubCaptureBytes(fakeJpeg);
    assert.equal(scrubbedJpeg.mimeType, 'image/jpeg');
    assert.equal(scrubbedJpeg.strippedChunks, 2);
    assert.equal(scrubbedJpeg.bytes.includes(Buffer.from('ExifSecret')), false);
  });
});

describe('http facade', () => {
  let server;
  let base;
  const auth = { Authorization: `Bearer ${TOKEN}` };

  async function postExecute(body, headers = auth) {
    const res = await fetch(`${base}/internal/v1/execute`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...headers },
      body: JSON.stringify(body),
    });
    return { status: res.status, json: await res.json() };
  }

  before(async () => {
    server = createServer();
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${server.address().port}`;
    // Wait for listen socket to be observable (CI flake guard).
    await sleep(10);
  });

  after(() => server.close());

  it('serves health endpoints without auth', async () => {
    const live = await fetch(`${base}/health/live`);
    assert.equal(live.status, 200);
    assert.deepEqual(await live.json(), { status: 'live' });
    const ready = await fetch(`${base}/health/ready`);
    assert.equal(ready.status, 200);
    const body = await ready.json();
    assert.equal(body.status, 'ready');
    assert.ok(body.capabilities.includes('navigate'));
  });

  it('requires auth on capabilities and execute', async () => {
    const noAuth = await fetch(`${base}/internal/v1/capabilities`);
    assert.equal(noAuth.status, 401);
    const bad = await fetch(`${base}/internal/v1/capabilities`, {
      headers: { Authorization: 'Bearer wrong' },
    });
    assert.equal(bad.status, 401);
    const ok = await fetch(`${base}/internal/v1/capabilities`, { headers: auth });
    assert.equal(ok.status, 200);
    const doc = await ok.json();
    assert.equal(doc.worker, 'navigator-playwright');
    assert.ok(doc.tools.some((t) => t.name === 'navigate'));
  });

  it('denies browser_evaluate and arbitrary JS tools', async () => {
    for (const tool of ['browser_evaluate', 'evaluate', 'exec', 'nope-tool']) {
      const r = await postExecute({ tool, args: {} });
      assert.equal(r.status, 403, tool);
      assert.equal(r.json.error.code, 'tool_not_allowed');
    }
  });

  it('denies SSRF navigation targets', async () => {
    for (const url of ['file:///etc/passwd', 'http://127.0.0.1/admin', 'http://169.254.169.254/latest']) {
      const r = await postExecute({ tool: 'navigate', args: { url } });
      assert.ok([400, 403].includes(r.status), `${url} -> ${r.status}`);
    }
  });

  it('denies unapproved origins', async () => {
    const r = await postExecute({ tool: 'navigate', args: { url: 'https://evil.test/x' } });
    assert.equal(r.status, 403);
    assert.equal(r.json.error.code, 'approval_required');
  });

  it('requires approved targets for click/fill/select', async () => {
    const click = await postExecute({ tool: 'click', args: { targetId: 'hdr-login' } });
    assert.equal(click.status, 403);
    assert.equal(click.json.error.code, 'approval_required');

    const ok = await postExecute({
      tool: 'click',
      args: { targetId: 'hdr-login', approvedTargets: ['hdr-login'] },
    });
    assert.equal(ok.status, 200);
    assert.equal(ok.json.ok, true);
  });

  it('denies secret fills and allows benign fills', async () => {
    const secret = await postExecute({
      tool: 'fill',
      args: { targetId: 'fld-password', value: 'hunter2', approvedTargets: ['fld-password'] },
    });
    assert.equal(secret.status, 403);
    assert.equal(secret.json.error.code, 'privacy_blocked');

    const ok = await postExecute({
      tool: 'fill',
      args: { targetId: 'fld-search', value: 'hello', approvedTargets: ['fld-search'] },
    });
    assert.equal(ok.status, 200);
  });

  it('requires consent for capture and redacts output', async () => {
    const noConsent = await postExecute({ tool: 'capture', args: {} });
    assert.equal(noConsent.status, 403);
    assert.equal(noConsent.json.error.code, 'approval_required');

    const ok = await postExecute({
      tool: 'capture',
      args: { consent: true, text: 'mail bob@example.com' },
    });
    assert.equal(ok.status, 200);
    assert.equal(ok.json.redacted, true);
    assert.ok(!ok.json.text.includes('bob@example.com'));
  });

  it('bounds wait durations', async () => {
    const bad = await postExecute({ tool: 'wait', args: { ms: 999_999 } });
    assert.equal(bad.status, 400);
    const ok = await postExecute({ tool: 'wait', args: { ms: 250 } });
    assert.equal(ok.status, 200);
    assert.equal(ok.json.waitedMs, 250);
  });
});
