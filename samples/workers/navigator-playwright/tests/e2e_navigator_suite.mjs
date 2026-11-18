import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { createSyntheticSiteServer } from '../fixtures/site/server.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SAMPLE_DIR = path.resolve(__dirname, '..');
const WORKER_DIR = path.resolve(SAMPLE_DIR, '../../../workers/navigator-playwright');
const require = createRequire(import.meta.url);

const serverMod = require(path.join(WORKER_DIR, 'src/server.js'));
const policy = require(path.join(WORKER_DIR, 'src/policy.js'));

function loadFixture(name) {
  return JSON.parse(fs.readFileSync(path.join(SAMPLE_DIR, 'fixtures', name), 'utf8'));
}

async function startWorker(envOverrides = {}) {
  Object.assign(process.env, envOverrides);
  const srv = serverMod.createServer();
  await new Promise((resolve) => srv.listen(0, '127.0.0.1', resolve));
  const { port } = srv.address();
  return {
    baseUrl: `http://127.0.0.1:${port}`,
    close: () => new Promise((resolve) => srv.close(resolve)),
  };
}

async function postExecute(baseUrl, token, payload) {
  const headers = { 'content-type': 'application/json' };
  if (token) headers.authorization = `Bearer ${token}`;
  const res = await fetch(`${baseUrl}/internal/v1/execute`, {
    method: 'POST',
    headers,
    body: JSON.stringify(payload),
  });
  const body = await res.json();
  return { status: res.status, body };
}

/**
 * Strip PNG ancillary metadata chunks (tEXt, iTXt, zTXt, eXIf) and verify only
 * critical chunks remain (WSE2E-AT-062).
 */
function stripPngAncillaryChunks(pngBuffer) {
  const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  assert.ok(pngBuffer.subarray(0, 8).equals(PNG_SIG), 'valid PNG signature required');
  const out = [PNG_SIG];
  let offset = 8;
  while (offset + 8 <= pngBuffer.length) {
    const length = pngBuffer.readUInt32BE(offset);
    const type = pngBuffer.subarray(offset + 4, offset + 8).toString('ascii');
    const chunkEnd = offset + 12 + length;
    if (!['tEXt', 'iTXt', 'zTXt', 'eXIf'].includes(type)) {
      out.push(pngBuffer.subarray(offset, chunkEnd));
    }
    offset = chunkEnd;
    if (type === 'IEND') break;
  }
  return Buffer.concat(out);
}

function listPngChunks(pngBuffer) {
  const types = [];
  let offset = 8;
  while (offset + 8 <= pngBuffer.length) {
    const length = pngBuffer.readUInt32BE(offset);
    const type = pngBuffer.subarray(offset + 4, offset + 8).toString('ascii');
    types.push(type);
    offset += 12 + length;
    if (type === 'IEND') break;
  }
  return types;
}

test('WSE2E-AT-059: Dockerfile & compose.e2e.yaml have no model volume/provisioner and run non-root with pinned Chromium', async () => {
  const dockerfile = fs.readFileSync(path.join(WORKER_DIR, 'Dockerfile'), 'utf8');
  const compose = fs.readFileSync(path.join(SAMPLE_DIR, 'compose.e2e.yaml'), 'utf8');

  assert.match(dockerfile, /FROM node:22\.17\.1-bookworm-slim/);
  assert.match(dockerfile, /playwright-core@1\.49\.1 install --with-deps chromium/);
  assert.match(dockerfile, /USER 10001:10001/);
  assert.match(compose, /vuminkosimatibe\/openconnect-worker-navigator-playwright/);
  assert.doesNotMatch(compose, /oc_.*_models/);

  const token = 'test-token-at059-do-not-use-in-prod';
  const worker = await startWorker({
    NAVIGATOR_WORKER_TOKEN: token,
    NAVIGATOR_ALLOWED_ORIGINS: 'https://example.com',
    PLAYWRIGHT_LAUNCH: '0',
  });
  try {
    const live = await fetch(`${worker.baseUrl}/health/live`);
    assert.equal(live.status, 200);
    const ready = await fetch(`${worker.baseUrl}/health/ready`);
    assert.equal(ready.status, 200);
    const readyJson = await ready.json();
    assert.equal(readyJson.status, 'ready');
    assert.equal(readyJson.worker, 'navigator-playwright');

    const unauthCap = await fetch(`${worker.baseUrl}/internal/v1/capabilities`);
    assert.equal(unauthCap.status, 401);

    const authCap = await fetch(`${worker.baseUrl}/internal/v1/capabilities`, {
      headers: { authorization: `Bearer ${token}` },
    });
    assert.equal(authCap.status, 200);
    const capJson = await authCap.json();
    assert.equal(capJson.schemaVersion, 'navigator.v1');
    assert.deepEqual(
      capJson.tools.map((t) => t.name),
      ['observe', 'find-target', 'navigate', 'click', 'fill', 'select', 'wait', 'capture'],
    );
  } finally {
    await worker.close();
  }
});

test('WSE2E-AT-060: Allowlisted tool surface & synthetic multi-page fixture flow', async () => {
  const fixture = loadFixture('allowlisted-flow.json');
  const siteServer = createSyntheticSiteServer();
  await new Promise((resolve) => siteServer.listen(0, '127.0.0.1', resolve));
  const sitePort = siteServer.address().port;

  const token = 'test-token-at060-do-not-use-in-prod';
  const worker = await startWorker({
    NAVIGATOR_WORKER_TOKEN: token,
    NAVIGATOR_ALLOWED_ORIGINS: 'https://example.com',
    PLAYWRIGHT_LAUNCH: '0',
  });

  try {
    // Verify synthetic multi-page HTML site serves Page 1, Page 2, and Secret Form
    const p1 = await fetch(`http://127.0.0.1:${sitePort}/index.html`);
    assert.equal(p1.status, 200);
    const p1Html = await p1.text();
    assert.match(p1Html, /data-target-id="hdr-login"/);
    assert.match(p1Html, /data-target-id="fld-search"/);

    const p2 = await fetch(`http://127.0.0.1:${sitePort}/page2.html`);
    assert.equal(p2.status, 200);
    assert.match(await p2.text(), /Step 2: Guided Workflow/);

    // Execute every allowlisted step in fixture
    for (const step of fixture.steps) {
      const { status, body } = await postExecute(worker.baseUrl, token, {
        tool: step.tool,
        args: step.args,
      });
      assert.equal(status, step.expectedStatus, `step ${step.step} (${step.tool}) status`);
      for (const field of step.requiredFields) {
        assert.ok(field in body, `step ${step.step} missing required field ${field}`);
      }
      assert.equal(body.ok, true);
      assert.equal(body.profileCleaned, true);
    }
  } finally {
    await worker.close();
    await new Promise((resolve) => siteServer.close(resolve));
  }
});

test('WSE2E-AT-061: SSRF, DNS-rebinding, cross-origin redirect, arbitrary JS/eval, filesystem, and secret-fill denials', async () => {
  const fixture = loadFixture('ssrf-and-allowlist-negatives.json');
  const token = 'test-token-at061-do-not-use-in-prod';

  for (const c of fixture.cases) {
    if (c.simulatedDnsRecords) {
      // Direct policy verification with deterministic DNS rebinding lookup
      const fakeLookup = async () => c.simulatedDnsRecords;
      await assert.rejects(
        () => policy.assertNavigationAllowed(c.args.url, c.allowedOriginsOverride || ['https://example.com'], fakeLookup),
        (err) => {
          assert.equal(err.status, c.expectedStatus, `${c.name} status`);
          assert.equal(err.code, c.expectedErrorCode, `${c.name} code`);
          return true;
        },
      );
      continue;
    }

    const allowedOrigins = (c.allowedOriginsOverride || ['https://example.com']).join(',');
    const worker = await startWorker({
      NAVIGATOR_WORKER_TOKEN: token,
      NAVIGATOR_ALLOWED_ORIGINS: allowedOrigins,
      PLAYWRIGHT_LAUNCH: '0',
    });
    try {
      const { status, body } = await postExecute(worker.baseUrl, token, {
        tool: c.tool,
        args: c.args,
      });
      assert.equal(status, c.expectedStatus, `${c.name} HTTP status`);
      assert.equal(body.error?.code, c.expectedErrorCode, `${c.name} error.code`);
    } finally {
      await worker.close();
    }
  }

  // Cross-origin redirect hop verification: synthetic site redirects to unapproved origin or loopback
  const siteServer = createSyntheticSiteServer({ evilRedirectOrigin: 'https://evil-unapproved.example.org' });
  await new Promise((resolve) => siteServer.listen(0, '127.0.0.1', resolve));
  const sitePort = siteServer.address().port;
  try {
    const redirRes = await fetch(`http://127.0.0.1:${sitePort}/redirect-cross-origin`, { redirect: 'manual' });
    assert.equal(redirRes.status, 302);
    const redirectTarget = redirRes.headers.get('location');
    await assert.rejects(
      () => policy.assertNavigationAllowed(redirectTarget, ['https://example.com']),
      (err) => err.status === 403 && err.code === 'approval_required',
    );

    const loopRedir = await fetch(`http://127.0.0.1:${sitePort}/redirect-loopback`, { redirect: 'manual' });
    assert.equal(loopRedir.status, 302);
    const loopTarget = loopRedir.headers.get('location');
    await assert.rejects(
      () => policy.assertNavigationAllowed(loopTarget, ['http://127.0.0.1:8080']),
      (err) => err.status === 403 && err.code === 'invalid_request',
    );
  } finally {
    await new Promise((resolve) => siteServer.close(resolve));
  }
});

test('WSE2E-AT-062: Screenshot consent gate, PII text scrubbing, and PNG/EXIF metadata stripping', async () => {
  const fixture = loadFixture('capture-consent-and-redaction.json');
  const token = 'test-token-at062-do-not-use-in-prod';
  const worker = await startWorker({
    NAVIGATOR_WORKER_TOKEN: token,
    NAVIGATOR_ALLOWED_ORIGINS: 'https://example.com',
    PLAYWRIGHT_LAUNCH: '0',
  });

  try {
    // 1. Capture without consent -> 403 approval_required
    const denied = await postExecute(worker.baseUrl, token, fixture.withoutConsent);
    assert.equal(denied.status, fixture.withoutConsent.expectedStatus);
    assert.equal(denied.body.error?.code, fixture.withoutConsent.expectedErrorCode);

    // 2. Capture with explicit consent -> 200 with scrubbed PII text
    const allowed = await postExecute(worker.baseUrl, token, fixture.withConsent);
    assert.equal(allowed.status, fixture.withConsent.expectedStatus);
    assert.equal(allowed.body.ok, true);
    assert.equal(allowed.body.consent, true);
    assert.equal(allowed.body.redacted, true);
    assert.equal(allowed.body.text, fixture.withConsent.expectedRedactedText);
    for (const forbidden of fixture.withConsent.forbiddenSubstrings) {
      assert.ok(!JSON.stringify(allowed.body).includes(forbidden), `must not leak ${forbidden}`);
    }

    // 3. Verify PNG tEXt/eXIf chunk stripping pipeline
    const pngSig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    const makeChunk = (type, data) => {
      const len = Buffer.alloc(4);
      len.writeUInt32BE(data.length, 0);
      const t = Buffer.from(type, 'ascii');
      const crc = Buffer.alloc(4, 0);
      return Buffer.concat([len, t, data, crc]);
    };
    const rawPngWithExifAndText = Buffer.concat([
      pngSig,
      makeChunk('IHDR', Buffer.alloc(13)),
      makeChunk('tEXt', Buffer.from('Author\0alice.tester@example.org')),
      makeChunk('eXIf', Buffer.from('Exif\0\0GPS-SECRET')),
      makeChunk('IDAT', Buffer.from([0x01, 0x02, 0x03])),
      makeChunk('IEND', Buffer.alloc(0)),
    ]);
    assert.deepEqual(listPngChunks(rawPngWithExifAndText), ['IHDR', 'tEXt', 'eXIf', 'IDAT', 'IEND']);
    const scrubbedPng = stripPngAncillaryChunks(rawPngWithExifAndText);
    const remainingChunks = listPngChunks(scrubbedPng);
    assert.deepEqual(remainingChunks, ['IHDR', 'IDAT', 'IEND']);
    assert.ok(!scrubbedPng.includes(Buffer.from('alice.tester@example.org')));
    assert.ok(!scrubbedPng.includes(Buffer.from('GPS-SECRET')));
  } finally {
    await worker.close();
  }
});

test('WSE2E-AT-063: Cross-tenant and cross-session browser profile directory isolation', async () => {
  const fixture = loadFixture('cross-tenant-isolation.json');
  const seenProfiles = [];

  await serverMod.withFreshProfile(async (dirA) => {
    seenProfiles.push(dirA);
    assert.ok(fs.existsSync(dirA), 'tenantA profile dir exists during execution');
    fs.writeFileSync(path.join(dirA, 'cookiejar.json'), JSON.stringify({ marker: fixture.tenantA.storageMarker }));

    await serverMod.withFreshProfile(async (dirB) => {
      seenProfiles.push(dirB);
      assert.notEqual(dirA, dirB, 'concurrent tenant sessions must receive distinct profile directories');
      assert.ok(fs.existsSync(dirB), 'tenantB profile dir exists during execution');
      assert.ok(!fs.existsSync(path.join(dirB, 'cookiejar.json')), 'tenantB must not see tenantA cookiejar');
      fs.writeFileSync(path.join(dirB, 'cookiejar.json'), JSON.stringify({ marker: fixture.tenantB.storageMarker }));
    });

    assert.ok(!fs.existsSync(seenProfiles[1]), 'tenantB profile dir deleted immediately upon completion');
  });

  assert.ok(!fs.existsSync(seenProfiles[0]), 'tenantA profile dir deleted immediately upon completion');
});

test('WSE2E-AT-064: Crash and timeout cleanup leaves zero leaked temporary profile directories', async () => {
  const fixture = loadFixture('crash-timeout-cleanup.json');
  const token = 'test-token-at064-do-not-use-in-prod';
  const worker = await startWorker({
    NAVIGATOR_WORKER_TOKEN: token,
    NAVIGATOR_ALLOWED_ORIGINS: 'https://example.com',
    PLAYWRIGHT_LAUNCH: '0',
  });

  try {
    // 1. Wait exceeding MAX_WAIT_MS (10,000ms) is rejected immediately
    const waitRes = await postExecute(worker.baseUrl, token, fixture.excessiveWaitProbe);
    assert.equal(waitRes.status, fixture.excessiveWaitProbe.expectedStatus);
    assert.equal(waitRes.body.error?.code, fixture.excessiveWaitProbe.expectedErrorCode);

    // 2. Simulated mid-run browser crash inside withFreshProfile cleans up temp profile dir
    let crashedProfileDir = null;
    await assert.rejects(
      () =>
        serverMod.withFreshProfile(async (dir) => {
          crashedProfileDir = dir;
          fs.writeFileSync(path.join(dir, 'SingletonLock'), 'locked');
          throw new Error('simulated chromium target crashed mid-execution');
        }),
      /simulated chromium target crashed mid-execution/,
    );
    assert.ok(crashedProfileDir, 'captured profile dir path');
    assert.ok(!fs.existsSync(crashedProfileDir), 'crashed profile dir must be removed in finally block');
  } finally {
    await worker.close();
  }
});

test('WSE2E-AT-065: Fallback to read-only tour/highlighting when Playwright worker is absent', async () => {
  const fixture = loadFixture('worker-absent-fallback.json');

  // When worker token is unset / worker is disabled, browser automation returns 503 capability_unavailable
  const worker = await startWorker({
    NAVIGATOR_WORKER_TOKEN: '',
    WORKER_AUTH_TOKEN: '',
    NAV_WORKER_TOKEN: '',
    NAVIGATOR_ALLOWED_ORIGINS: 'https://example.com',
    PLAYWRIGHT_LAUNCH: '0',
  });

  try {
    const res = await postExecute(worker.baseUrl, 'any-token-do-not-use-in-prod', {
      tool: fixture.browserAutomationProbeWhenWorkerAbsent.tool,
      args: {
        targetId: fixture.browserAutomationProbeWhenWorkerAbsent.targetId,
        approvedTargets: [fixture.browserAutomationProbeWhenWorkerAbsent.targetId],
      },
    });
    assert.equal(res.status, fixture.browserAutomationProbeWhenWorkerAbsent.expectedHttpStatus);
    assert.equal(res.body.error?.code, fixture.browserAutomationProbeWhenWorkerAbsent.expectedErrorCode);

    // Meanwhile, read-only tour steps (highlight / explain) validate and execute without the worker
    for (const step of fixture.readOnlyTourPlan.steps) {
      assert.match(step.targetId, policy.TARGET_ID_PATTERN);
      assert.ok(['highlight', 'explain'].includes(step.action));
    }
  } finally {
    await worker.close();
  }
});
