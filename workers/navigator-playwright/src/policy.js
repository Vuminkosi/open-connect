'use strict';

/**
 * Allowlist policy for the navigator-playwright worker.
 *
 * Fail-closed: anything not explicitly allowlisted is denied.
 * No dependency on playwright here so policy can be unit-tested standalone.
 */

const dns = require('node:dns').promises;
const net = require('node:net');

/** Tools the facade is permitted to execute. Everything else -> tool_not_allowed. */
const ALLOWED_TOOLS = Object.freeze([
  'observe',
  'find-target',
  'navigate',
  'click',
  'fill',
  'select',
  'wait',
  'capture',
]);

/** Tools that are explicitly denied (documented for clear error messaging). */
const EXPLICITLY_DENIED_TOOLS = Object.freeze([
  'browser_evaluate',
  'evaluate',
  'eval',
  'script',
  'exec',
  'run-js',
  'file-read',
  'file-write',
  'download',
  'upload',
]);

const TARGET_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const MAX_WAIT_MS = 10_000;
const MAX_URL_LENGTH = 2048;

/** Hostnames that are never navigated to, even before DNS resolution. */
const BLOCKED_HOSTNAMES = Object.freeze([
  'localhost',
  'metadata.google.internal',
  'metadata.google.com',
  'instance-data',
  'instance-data-compute',
]);

/** Suffixes that indicate cloud metadata / internal DNS. */
const BLOCKED_HOST_SUFFIXES = Object.freeze([
  '.internal',
  '.metadata.google.internal',
]);

const SECRET_FIELD_PATTERN = /(passw|passwd|pwd|secret|token|api[-_]?key|ssn|social|card|ccnum|cvv|cvc|pin|otp|private[-_]?key)/i;
const EMAIL_PATTERN = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
const CARD_PATTERN = /\b(?:\d[ -]?){13,19}\b/g;

function error(code, message, status) {
  const err = new Error(message);
  err.code = code;
  err.status = status;
  return err;
}

/**
 * Parse the approved-origin allowlist from env.
 * NAVIGATOR_ALLOWED_ORIGINS="https://example.com,https://app.example.com"
 * Each entry must be an http(s) origin (scheme + host + optional port).
 */
function parseAllowedOrigins(raw) {
  if (!raw || typeof raw !== 'string') return [];
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => {
      const u = new URL(s);
      if (u.protocol !== 'http:' && u.protocol !== 'https:') {
        throw new Error(`allowed origin must be http(s): ${s}`);
      }
      return u.origin.toLowerCase();
    });
}

function isHttpUrl(value) {
  try {
    const u = new URL(value);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

/** Origin of a URL string, lower-cased; throws invalid_request on malformed input. */
function originOf(url) {
  let u;
  try {
    u = new URL(url);
  } catch {
    throw error('invalid_request', 'malformed url', 400);
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') {
    // file:, data:, javascript:, blob:, ftp:, etc. are never allowed.
    throw error('invalid_request', `url scheme not allowed: ${u.protocol}`, 400);
  }
  return { url: u, origin: u.origin.toLowerCase(), hostname: u.hostname.toLowerCase() };
}

function checkBlockedHostname(hostname) {
  if (BLOCKED_HOSTNAMES.includes(hostname)) {
    throw error('invalid_request', `host not allowed: ${hostname}`, 403);
  }
  for (const suffix of BLOCKED_HOST_SUFFIXES) {
    if (hostname.endsWith(suffix)) {
      throw error('invalid_request', `host not allowed: ${hostname}`, 403);
    }
  }
}

function isBlockedIp(ip) {
  if (net.isIP(ip) === 0) return true; // unparseable -> fail closed
  if (net.isIPv4(ip)) {
    const parts = ip.split('.').map(Number);
    const [a, b] = parts;
    if (a === 127) return true; // loopback 127.0.0.0/8
    if (a === 0) return true; // 0.0.0.0/8
    if (a === 10) return true; // RFC1918 10/8
    if (a === 172 && b >= 16 && b <= 31) return true; // RFC1918 172.16/12
    if (a === 192 && b === 168) return true; // RFC1918 192.168/16
    if (a === 169 && b === 254) return true; // link-local (cloud metadata 169.254.169.254)
    if (a >= 224) return true; // multicast + reserved
    if (a === 100 && b >= 64 && b <= 127) return true; // CGNAT 100.64/10
    if (a === 192 && b === 0 && (parts[2] === 0 || parts[2] === 2)) return true; // documentation/special
    return false;
  }
  // IPv6
  const lower = ip.toLowerCase();
  if (lower === '::1' || lower === '::') return true;
  if (lower.startsWith('fe80:') || lower.startsWith('fec0:') || lower.startsWith('fc00:') || lower.startsWith('fd00:')) return true;
  if (lower.startsWith('ff')) return true; // multicast
  if (lower === '::ffff:127.0.0.1') return true;
  return false;
}

/**
 * Resolve a hostname and deny it if ANY resolved address is in a blocked range.
 * This is the DNS-rebind guard: resolution happens at request time, per request.
 * NOTE: TOCTOU rebind between check and browser connect is mitigated by also
 * pinning the resolved IP where the browser stack allows it; residual risk is
 * documented in README.md.
 */
async function assertHostResolvesToPublicIp(hostname, lookup = dns.lookup) {
  checkBlockedHostname(hostname);
  if (net.isIP(hostname) !== 0) {
    if (isBlockedIp(hostname)) {
      throw error('invalid_request', 'destination address not allowed', 403);
    }
    return;
  }
  let records;
  try {
    records = await lookup(hostname, { all: true });
  } catch {
    throw error('invalid_request', `cannot resolve host: ${hostname}`, 400);
  }
  if (!records || records.length === 0) {
    throw error('invalid_request', `cannot resolve host: ${hostname}`, 400);
  }
  for (const r of records) {
    if (isBlockedIp(r.address)) {
      throw error('invalid_request', 'destination address not allowed', 403);
    }
  }
}

/** Enforce that a navigation URL is http(s), allowlisted origin, and SSRF-safe. */
async function assertNavigationAllowed(url, allowedOrigins, lookup) {
  if (typeof url !== 'string' || url.length === 0 || url.length > MAX_URL_LENGTH) {
    throw error('invalid_request', 'url is required (max 2048 chars)', 400);
  }
  const { origin, hostname } = originOf(url);
  if (allowedOrigins.length > 0 && !allowedOrigins.includes(origin)) {
    const denied = new Error(`origin not approved: ${origin}`);
    denied.code = 'approval_required';
    denied.status = 403;
    throw denied;
  }
  await assertHostResolvesToPublicIp(hostname, lookup);
  return origin;
}

function assertApprovedTarget(targetId, approvedTargets) {
  if (typeof targetId !== 'string' || !TARGET_ID_PATTERN.test(targetId)) {
    throw error('invalid_request', 'targetId must match [A-Za-z0-9_-]{1,64}', 400);
  }
  const list = Array.isArray(approvedTargets) ? approvedTargets : [];
  if (!list.includes(targetId)) {
    const denied = new Error(`target not approved: ${targetId}`);
    denied.code = 'approval_required';
    denied.status = 403;
    throw denied;
  }
}

function isSecretFill(args) {
  if (args && args.isSecret === true) return true;
  const haystack = [args && args.fieldName, args && args.fieldLabel, args && args.targetId]
    .filter((v) => typeof v === 'string')
    .join(' ');
  if (SECRET_FIELD_PATTERN.test(haystack)) return true;
  if (args && typeof args.fieldType === 'string' && args.fieldType.toLowerCase() === 'password') return true;
  return false;
}

/** Redact PII-ish spans from captured text. Always applied to capture/observe output. */
function redactText(text) {
  if (typeof text !== 'string') return text;
  return text.replace(EMAIL_PATTERN, '[redacted-email]').replace(CARD_PATTERN, '[redacted-card]');
}

module.exports = {
  ALLOWED_TOOLS,
  EXPLICITLY_DENIED_TOOLS,
  TARGET_ID_PATTERN,
  MAX_WAIT_MS,
  BLOCKED_HOSTNAMES,
  parseAllowedOrigins,
  isHttpUrl,
  originOf,
  isBlockedIp,
  assertHostResolvesToPublicIp,
  assertNavigationAllowed,
  assertApprovedTarget,
  isSecretFill,
  redactText,
  error,
};
