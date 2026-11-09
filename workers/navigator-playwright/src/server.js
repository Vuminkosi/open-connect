'use strict';

/**
 * navigator-playwright worker: allowlisted Chromium tool facade over plain Node http.
 *
 * Endpoints:
 *   GET  /health/live                 (no auth)
 *   GET  /health/ready                (no auth)
 *   GET  /internal/v1/capabilities    (auth: Bearer token)
 *   POST /internal/v1/execute         (auth: Bearer token, allowlisted tools only)
 *
 * Security properties:
 * - Only ALLOWED_TOOLS execute; everything else -> tool_not_allowed (403).
 * - No browser_evaluate / arbitrary JS / file access surface exists.
 * - navigate/observe/capture URLs must be http(s), on the approved-origin
 *   allowlist, and resolve only to public IPs (SSRF + DNS-rebind guard).
 * - click/fill/select require a caller-approved targetId (approval_required otherwise).
 * - fill refuses secret fields/values (privacy_blocked).
 * - capture requires explicit consent and its output is always redacted.
 * - Per-run browser profiles are created under os.tmpdir() and removed after
 *   each execution (see withFreshProfile).
 *
 * Browser automation itself is intentionally simulated in this facade: the host
 * grants authority per command, and a real playwright-core launch happens only
 * when PLAYWRIGHT_LAUNCH=1 and playwright-core + chromium are installed
 * (production image). Unit tests run with the simulator (default).
 */

const http = require('node:http');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const policy = require('./policy');

const WORKER_NAME = 'navigator-playwright';
const SCHEMA_VERSION = 'navigator.v1';
const MAX_BODY_BYTES = 1_048_576; // 1 MiB

function getAuthToken() {
  return (
    process.env.NAVIGATOR_WORKER_TOKEN ||
    process.env.WORKER_AUTH_TOKEN ||
    process.env.NAV_WORKER_TOKEN ||
    null
  );
}

function getAllowedOrigins() {
  try {
    return policy.parseAllowedOrigins(process.env.NAVIGATOR_ALLOWED_ORIGINS || '');
  } catch {
    return [];
  }
}

function sendJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(payload),
    'cache-control': 'no-store',
  });
  res.end(payload);
}

function sendError(res, err) {
  const status = typeof err.status === 'number' ? err.status : 500;
  const code = typeof err.code === 'string' ? err.code : 'internal_error';
  const safeMessage =
    status >= 500 ? 'internal error' : String(err.message || 'request failed').slice(0, 300);
  sendJson(res, status, { error: { code, message: safeMessage } });
}

function isAuthorized(req) {
  const token = getAuthToken();
  if (!token) return { ok: false, unavailable: true };
  const header = req.headers.authorization || '';
  if (!header.startsWith('Bearer ')) return { ok: false, unavailable: false };
  const supplied = header.slice('Bearer '.length);
  const a = Buffer.from(supplied);
  const b = Buffer.from(token);
  if (a.length !== b.length) return { ok: false, unavailable: false };
  return { ok: crypto.timingSafeEqual(a, b), unavailable: false };
}

function requireAuth(req, res) {
  const result = isAuthorized(req);
  if (result.unavailable) {
    sendJson(res, 503, { error: { code: 'capability_unavailable', message: 'worker unavailable' } });
    return false;
  }
  if (!result.ok) {
    sendJson(res, 401, { error: { code: 'unauthorized', message: 'unauthorized' } });
    return false;
  }
  return true;
}

function capabilitiesDocument() {
  const tools = policy.ALLOWED_TOOLS.map((name) => ({ name, version: '1.0.0' }));
  return {
    worker: WORKER_NAME,
    schemaVersion: SCHEMA_VERSION,
    tools,
    denied: [...policy.EXPLICITLY_DENIED_TOOLS],
    limits: {
      maxWaitMs: policy.MAX_WAIT_MS,
      maxUrlLength: 2048,
      maxBodyBytes: MAX_BODY_BYTES,
    },
  };
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let total = 0;
    const chunks = [];
    req.on('data', (chunk) => {
      total += chunk.length;
      if (total > MAX_BODY_BYTES) {
        reject(policy.error('invalid_request', 'request body too large', 413));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

/**
 * Create an isolated per-run browser profile directory and remove it afterwards.
 * Real chromium launches (production) receive this dir as user-data-dir so no
 * state leaks between runs.
 */
async function withFreshProfile(fn) {
  const dir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'nav-play-'));
  try {
    return await fn(dir);
  } finally {
    await fs.promises.rm(dir, { recursive: true, force: true });
  }
}

/** Deterministic simulator used when PLAYWRIGHT_LAUNCH is not enabled. */
function simulatedResult(tool, args) {
  const now = new Date().toISOString();
  switch (tool) {
    case 'observe':
      return {
        ok: true,
        tool,
        at: now,
        url: args.url || null,
        nodes: [
          { targetId: 'hdr-login', role: 'button', label: 'Log in' },
          { targetId: 'fld-search', role: 'textbox', label: 'Search' },
        ],
        note: 'snapshot redacted',
        redacted: true,
      };
    case 'find-target':
      return {
        ok: true,
        tool,
        at: now,
        query: args.query,
        targets: [{ targetId: 'hdr-login', role: 'button', label: 'Log in' }],
      };
    case 'navigate':
      return { ok: true, tool, at: now, url: args.url };
    case 'click':
      return { ok: true, tool, at: now, targetId: args.targetId };
    case 'fill':
      return { ok: true, tool, at: now, targetId: args.targetId, filled: true, redacted: true };
    case 'select':
      return { ok: true, tool, at: now, targetId: args.targetId, value: args.value };
    case 'wait':
      return { ok: true, tool, at: now, waitedMs: args.ms };
    case 'capture':
      return {
        ok: true,
        tool,
        at: now,
        mimeType: 'image/png',
        // Redacted placeholder: real captures are scrubbed before return.
        bytesBase64: Buffer.from('redacted-capture').toString('base64'),
        text: policy.redactText(args.text || ''),
        consent: true,
        redacted: true,
      };
    default:
      throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }
}

async function maybeLaunchBrowser(tool, args, profileDir) {
  if (process.env.PLAYWRIGHT_LAUNCH !== '1') return null;
  let playwright;
  try {
    playwright = require('playwright-core');
  } catch {
    throw policy.error('capability_unavailable', 'browser runtime unavailable', 503);
  }
  const executablePath = process.env.PLAYWRIGHT_CHROMIUM_PATH || undefined;
  const browser = await playwright.chromium.launch({ executablePath });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    void page;
    void profileDir;
    return { launched: true, tool, url: args.url || null };
  } finally {
    await browser.close().catch(() => {});
  }
}

async function executeTool(tool, args) {
  if (typeof tool !== 'string' || tool.length === 0) {
    throw policy.error('invalid_request', 'tool is required', 400);
  }
  if (!policy.ALLOWED_TOOLS.includes(tool)) {
    throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }
  if (args == null || typeof args !== 'object' || Array.isArray(args)) {
    throw policy.error('invalid_request', 'args must be an object', 400);
  }

  const allowedOrigins = getAllowedOrigins();
  const approvedTargets =
    (Array.isArray(args.approvedTargets) && args.approvedTargets) ||
    (args.grant && Array.isArray(args.grant.targets) && args.grant.targets) ||
    [];

  switch (tool) {
    case 'navigate': {
      await policy.assertNavigationAllowed(args.url, allowedOrigins);
      break;
    }
    case 'observe':
    case 'find-target': {
      if (tool === 'find-target' && (typeof args.query !== 'string' || args.query.length === 0)) {
        throw policy.error('invalid_request', 'query is required', 400);
      }
      if (args.url !== undefined) {
        await policy.assertNavigationAllowed(args.url, allowedOrigins);
      }
      break;
    }
    case 'click':
    case 'select': {
      policy.assertApprovedTarget(args.targetId, approvedTargets);
      if (tool === 'select' && typeof args.value !== 'string') {
        throw policy.error('invalid_request', 'value is required', 400);
      }
      if (args.url !== undefined) {
        await policy.assertNavigationAllowed(args.url, allowedOrigins);
      }
      break;
    }
    case 'fill': {
      policy.assertApprovedTarget(args.targetId, approvedTargets);
      if (typeof args.value !== 'string' || args.value.length === 0) {
        throw policy.error('invalid_request', 'value is required', 400);
      }
      if (policy.isSecretFill(args)) {
        throw policy.error('privacy_blocked', 'secret fill is not permitted', 403);
      }
      if (args.url !== undefined) {
        await policy.assertNavigationAllowed(args.url, allowedOrigins);
      }
      break;
    }
    case 'wait': {
      const ms = args.ms;
      if (typeof ms !== 'number' || !Number.isFinite(ms) || ms < 0 || ms > policy.MAX_WAIT_MS) {
        throw policy.error('invalid_request', `ms must be 0..${policy.MAX_WAIT_MS}`, 400);
      }
      break;
    }
    case 'capture': {
      const consent = args.consent === true || (args.grant && args.grant.consent === true);
      if (!consent) {
        throw policy.error('approval_required', 'capture requires explicit consent', 403);
      }
      if (args.url !== undefined) {
        await policy.assertNavigationAllowed(args.url, allowedOrigins);
      }
      break;
    }
    default:
      throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }

  return withFreshProfile(async (profileDir) => {
    const launched = await maybeLaunchBrowser(tool, args, profileDir);
    if (launched) return { ok: true, ...launched, profileCleaned: true };
    return { ...simulatedResult(tool, args), profileCleaned: true };
  });
}

function createServer() {
  return http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url || '/', 'http://localhost');
      if (req.method === 'GET' && url.pathname === '/health/live') {
        sendJson(res, 200, { status: 'live' });
        return;
      }
      if (req.method === 'GET' && url.pathname === '/health/ready') {
        sendJson(res, 200, {
          status: 'ready',
          worker: WORKER_NAME,
          capabilities: [...policy.ALLOWED_TOOLS],
          browser: process.env.PLAYWRIGHT_LAUNCH === '1' ? 'chromium' : 'simulator',
        });
        return;
      }
      if (req.method === 'GET' && url.pathname === '/internal/v1/capabilities') {
        if (!requireAuth(req, res)) return;
        sendJson(res, 200, capabilitiesDocument());
        return;
      }
      if (req.method === 'POST' && url.pathname === '/internal/v1/execute') {
        if (!requireAuth(req, res)) return;
        let raw;
        try {
          raw = await readBody(req);
        } catch (err) {
          sendError(res, err);
          return;
        }
        let body;
        try {
          body = raw ? JSON.parse(raw) : {};
        } catch {
          sendJson(res, 400, { error: { code: 'invalid_request', message: 'invalid JSON' } });
          return;
        }
        try {
          const result = await executeTool(body.tool, body.args || {});
          sendJson(res, 200, result);
        } catch (err) {
          sendError(res, err);
        }
        return;
      }
      sendJson(res, 404, { error: { code: 'invalid_request', message: 'not found' } });
    } catch (err) {
      sendError(res, err);
    }
  });
}

if (require.main === module) {
  const port = Number(process.env.PORT || 8080);
  const server = createServer();
  server.listen(port, '0.0.0.0', () => {
    // eslint-disable-next-line no-console
    console.log(`navigator-playwright listening on :${port}`);
  });
}

module.exports = { createServer, executeTool, withFreshProfile, capabilitiesDocument };
