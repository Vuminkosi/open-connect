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

const PROFILE_TOKEN_PATTERN = /^[A-Za-z0-9_.-]{1,64}$/;

function sanitizeProfileSegment(value, fallback) {
  if (value === undefined || value === null || value === '') return fallback;
  if (typeof value !== 'string' || !PROFILE_TOKEN_PATTERN.test(value) || value === '.' || value === '..') {
    throw policy.error('invalid_request', 'tenantId/sessionId must match [A-Za-z0-9_.-]{1,64}', 400);
  }
  return value;
}

/**
 * Create an isolated per-run browser profile directory scoped to tenantId/sessionId
 * (never shared across runs or tenants) and remove it afterwards, even on error or timeout.
 * Real chromium launches (production) receive this dir as --user-data-dir so no
 * state, cookies, storage, or cache leaks between runs or tenants.
 */
async function withFreshProfile(fn, options = {}) {
  const tenantSegment = sanitizeProfileSegment(options.tenantId, 'anon');
  const sessionSegment = sanitizeProfileSegment(options.sessionId, 'run');
  const prefix = path.join(os.tmpdir(), `nav-play-${tenantSegment}-${sessionSegment}-`);
  const dir = await fs.promises.mkdtemp(prefix);
  const timeoutMs =
    typeof options.timeoutMs === 'number' && Number.isFinite(options.timeoutMs) && options.timeoutMs > 0
      ? Math.min(options.timeoutMs, policy.MAX_WAIT_MS)
      : null;
  let timer = null;
  try {
    if (timeoutMs !== null) {
      return await Promise.race([
        fn(dir),
        new Promise((_, reject) => {
          timer = setTimeout(() => {
            reject(policy.error('invalid_request', `profile execution timed out after ${timeoutMs}ms`, 408));
          }, timeoutMs);
        }),
      ]);
    }
    return await fn(dir);
  } finally {
    if (timer !== null) clearTimeout(timer);
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
    case 'capture': {
      const scrubbed = policy.scrubCaptureBytes(args.bytesBase64 || args.imageBase64, {
        redactBoxes: args.redactBoxes,
      });
      return {
        ok: true,
        tool,
        at: now,
        mimeType: scrubbed.mimeType,
        // Scrubbed image bytes: EXIF/PNG text chunks stripped and redactBoxes blacked out.
        bytesBase64: scrubbed.bytes.toString('base64'),
        metadataStripped: scrubbed.metadataStripped,
        redactedBoxes: scrubbed.redactedBoxes,
        text: policy.redactText(args.text || ''),
        consent: true,
        redacted: true,
      };
    }
    default:
      throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }
}

async function maybeLaunchBrowser(tool, args, profileDir, allowedOrigins = [], lookup = undefined) {
  if (process.env.PLAYWRIGHT_LAUNCH !== '1') return null;
  let playwright;
  try {
    playwright = require('playwright-core');
  } catch {
    throw policy.error('capability_unavailable', 'browser runtime unavailable', 503);
  }
  const executablePath = process.env.PLAYWRIGHT_CHROMIUM_PATH || undefined;
  const browser = await playwright.chromium.launch({
    executablePath,
    args: [`--user-data-dir=${profileDir}`],
  });
  try {
    const context = await browser.newContext();
    await context.route('**/*', async (route, request) => {
      try {
        await policy.assertNavigationAllowed(request.url(), allowedOrigins, lookup);
        await route.continue();
      } catch {
        await route.abort('accessdenied');
      }
    });
    const page = await context.newPage();
    if (args.url) {
      const response = await page.goto(args.url, { waitUntil: 'domcontentloaded', timeout: policy.MAX_WAIT_MS });
      if (response) {
        const chain = [];
        let req = response.request();
        while (req) {
          chain.unshift({ url: req.url() });
          req = typeof req.redirectedFrom === 'function' ? req.redirectedFrom() : null;
        }
        const addr = typeof response.serverAddr === 'function' ? await response.serverAddr() : null;
        if (addr && addr.ip && chain.length > 0) {
          chain[chain.length - 1].ip = addr.ip;
        }
        if (chain.length > 0) {
          await policy.assertRedirectChainAllowed(chain, allowedOrigins, lookup);
        }
      }
    }
    if (tool === 'capture') {
      const rawShot = await page.screenshot({ type: 'png' });
      const scrubbed = policy.scrubCaptureBytes(rawShot, { redactBoxes: args.redactBoxes });
      return {
        launched: true,
        tool,
        url: args.url || null,
        mimeType: scrubbed.mimeType,
        bytesBase64: scrubbed.bytes.toString('base64'),
        metadataStripped: scrubbed.metadataStripped,
        redactedBoxes: scrubbed.redactedBoxes,
        text: policy.redactText(args.text || ''),
        consent: true,
        redacted: true,
      };
    }
    return { launched: true, tool, url: args.url || null };
  } finally {
    await browser.close().catch(() => {});
  }
}

async function executeTool(tool, args, options = {}) {
  if (typeof tool !== 'string' || tool.length === 0) {
    throw policy.error('invalid_request', 'tool is required', 400);
  }
  if (!policy.ALLOWED_TOOLS.includes(tool)) {
    throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }
  if (args == null || typeof args !== 'object' || Array.isArray(args)) {
    throw policy.error('invalid_request', 'args must be an object', 400);
  }

  const allowedOrigins = options.allowedOrigins || getAllowedOrigins();
  const lookup = options.lookup;
  const approvedTargets =
    (Array.isArray(args.approvedTargets) && args.approvedTargets) ||
    (args.grant && Array.isArray(args.grant.targets) && args.grant.targets) ||
    [];

  async function validateUrlAndRedirects() {
    if (args.url !== undefined) {
      await policy.assertNavigationAllowed(args.url, allowedOrigins, lookup);
    }
    if (args.redirectChain !== undefined) {
      await policy.assertRedirectChainAllowed(args.redirectChain, allowedOrigins, lookup);
    }
  }

  switch (tool) {
    case 'navigate': {
      await policy.assertNavigationAllowed(args.url, allowedOrigins, lookup);
      if (args.redirectChain !== undefined) {
        await policy.assertRedirectChainAllowed(args.redirectChain, allowedOrigins, lookup);
      }
      break;
    }
    case 'observe':
    case 'find-target': {
      if (tool === 'find-target' && (typeof args.query !== 'string' || args.query.length === 0)) {
        throw policy.error('invalid_request', 'query is required', 400);
      }
      await validateUrlAndRedirects();
      break;
    }
    case 'click':
    case 'select': {
      policy.assertApprovedTarget(args.targetId, approvedTargets);
      if (tool === 'select' && typeof args.value !== 'string') {
        throw policy.error('invalid_request', 'value is required', 400);
      }
      await validateUrlAndRedirects();
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
      await validateUrlAndRedirects();
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
      policy.normalizeRedactBoxes(args.redactBoxes);
      await validateUrlAndRedirects();
      break;
    }
    default:
      throw policy.error('tool_not_allowed', `tool not allowed: ${tool}`, 403);
  }

  return withFreshProfile(
    async (profileDir) => {
      const launched = await maybeLaunchBrowser(tool, args, profileDir, allowedOrigins, lookup);
      if (launched) return { ok: true, ...launched, profileCleaned: true };
      return { ...simulatedResult(tool, args), profileCleaned: true };
    },
    {
      tenantId: args.tenantId,
      sessionId: args.sessionId,
      timeoutMs: args.timeoutMs,
    },
  );
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
