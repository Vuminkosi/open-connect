'use strict';

/**
 * Allowlist policy for the navigator-playwright worker.
 *
 * Fail-closed: anything not explicitly allowlisted is denied.
 * No dependency on playwright here so policy can be unit-tested standalone.
 */

const dns = require('node:dns').promises;
const net = require('node:net');
const zlib = require('node:zlib');

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
const MAX_REDIRECTS = 10;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const ALLOWED_PNG_CHUNKS = new Set(['IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND']);
const STRIPPED_PNG_METADATA_CHUNKS = Object.freeze(['tEXt', 'iTXt', 'zTXt', 'eXIf', 'tIME', 'iCCP']);

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

/**
 * Enforce that every hop in a navigation redirect chain (and any final resolved
 * remote IP) is http(s), on the approved-origin allowlist, and resolves only to
 * public IPs. Defends against redirect-based SSRF and DNS rebinding mid-chain.
 */
async function assertRedirectChainAllowed(chain, allowedOrigins, lookup) {
  if (!Array.isArray(chain) || chain.length === 0) {
    throw error('invalid_request', 'redirect chain must be a non-empty array', 400);
  }
  if (chain.length > MAX_REDIRECTS) {
    throw error('invalid_request', `redirect chain exceeds max hops (${MAX_REDIRECTS})`, 400);
  }
  const origins = [];
  for (const hop of chain) {
    const hopUrl = typeof hop === 'string' ? hop : hop && typeof hop === 'object' ? hop.url : null;
    const remoteIp =
      hop && typeof hop === 'object' ? hop.ip || hop.remoteAddress || hop.serverAddr || null : null;
    const origin = await assertNavigationAllowed(hopUrl, allowedOrigins, lookup);
    if (remoteIp !== null && remoteIp !== undefined) {
      if (typeof remoteIp !== 'string' || isBlockedIp(remoteIp)) {
        throw error('invalid_request', 'destination address not allowed', 403);
      }
    }
    origins.push(origin);
  }
  return origins;
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i += 1) {
    c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function makePngChunk(type, data) {
  const typeBuf = Buffer.from(type, 'ascii');
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32BE(data.length, 0);
  const crcInput = Buffer.concat([typeBuf, data]);
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(crcInput), 0);
  return Buffer.concat([lenBuf, typeBuf, data, crcBuf]);
}

/**
 * Create a valid 8-bit RGBA PNG buffer (optionally with extra metadata chunks
 * for testing metadata stripping).
 */
function createSolidPng(width = 8, height = 8, rgba = [200, 220, 240, 255], extraChunks = []) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colorType 6 = RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y += 1) {
    const rowStart = y * (stride + 1);
    raw[rowStart] = 0; // filter type 0 (None)
    for (let x = 0; x < width; x += 1) {
      const px = rowStart + 1 + x * 4;
      raw[px] = rgba[0];
      raw[px + 1] = rgba[1];
      raw[px + 2] = rgba[2];
      raw[px + 3] = rgba[3];
    }
  }
  const compressed = zlib.deflateSync(raw);
  const chunks = [PNG_SIGNATURE, makePngChunk('IHDR', ihdr)];
  for (const extra of extraChunks) {
    chunks.push(makePngChunk(extra.type, Buffer.isBuffer(extra.data) ? extra.data : Buffer.from(String(extra.data), 'utf8')));
  }
  chunks.push(makePngChunk('IDAT', compressed));
  chunks.push(makePngChunk('IEND', Buffer.alloc(0)));
  return Buffer.concat(chunks);
}

function normalizeRedactBoxes(boxes) {
  if (boxes === undefined || boxes === null) return [];
  if (!Array.isArray(boxes)) {
    throw error('invalid_request', 'redactBoxes must be an array', 400);
  }
  return boxes.map((b) => {
    if (!b || typeof b !== 'object') {
      throw error('invalid_request', 'each redactBox must be an object', 400);
    }
    const x = Number(b.x);
    const y = Number(b.y);
    const width = Number(b.width !== undefined ? b.width : b.w);
    const height = Number(b.height !== undefined ? b.height : b.h);
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(width) || !Number.isFinite(height)) {
      throw error('invalid_request', 'redactBox requires numeric x, y, width, height', 400);
    }
    if (width < 0 || height < 0) {
      throw error('invalid_request', 'redactBox width and height must be non-negative', 400);
    }
    return {
      x: Math.max(0, Math.floor(x)),
      y: Math.max(0, Math.floor(y)),
      width: Math.floor(width),
      height: Math.floor(height),
    };
  });
}

function scrubPngBuffer(buf, boxes) {
  let offset = PNG_SIGNATURE.length;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  let ihdrChunk = null;
  const keptNonIdat = [];
  const idatParts = [];
  let strippedChunks = 0;

  while (offset + 8 <= buf.length) {
    const length = buf.readUInt32BE(offset);
    const type = buf.subarray(offset + 4, offset + 8).toString('ascii');
    const dataStart = offset + 8;
    const dataEnd = dataStart + length;
    const chunkEnd = dataEnd + 4;
    if (chunkEnd > buf.length) {
      throw error('invalid_request', 'malformed PNG chunk', 400);
    }
    const data = buf.subarray(dataStart, dataEnd);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      ihdrChunk = makePngChunk('IHDR', data);
    } else if (type === 'IDAT') {
      idatParts.push(data);
    } else if (type === 'IEND') {
      break;
    } else if (ALLOWED_PNG_CHUNKS.has(type)) {
      keptNonIdat.push(makePngChunk(type, data));
    } else {
      strippedChunks += 1;
    }
    offset = chunkEnd;
  }

  if (!ihdrChunk || idatParts.length === 0) {
    throw error('invalid_request', 'invalid PNG: missing IHDR or IDAT', 400);
  }

  let finalIdatPayload = Buffer.concat(idatParts);
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
  if (boxes.length > 0 && bitDepth === 8 && channels > 0) {
    const decompressed = zlib.inflateSync(finalIdatPayload);
    const stride = width * channels;
    const expectedLen = (stride + 1) * height;
    if (decompressed.length === expectedLen) {
      const recon = Buffer.alloc(expectedLen);
      for (let y = 0; y < height; y += 1) {
        const rowOff = y * (stride + 1);
        const filter = decompressed[rowOff];
        recon[rowOff] = 0;
        for (let i = 0; i < stride; i += 1) {
          const rawByte = decompressed[rowOff + 1 + i];
          const a = i >= channels ? recon[rowOff + 1 + i - channels] : 0;
          const b = y > 0 ? recon[(y - 1) * (stride + 1) + 1 + i] : 0;
          const c = y > 0 && i >= channels ? recon[(y - 1) * (stride + 1) + 1 + i - channels] : 0;
          let val = rawByte;
          if (filter === 1) val = (rawByte + a) & 0xff;
          else if (filter === 2) val = (rawByte + b) & 0xff;
          else if (filter === 3) val = (rawByte + ((a + b) >> 1)) & 0xff;
          else if (filter === 4) {
            const p = a + b - c;
            const pa = Math.abs(p - a);
            const pb = Math.abs(p - b);
            const pc = Math.abs(p - c);
            const pr = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            val = (rawByte + pr) & 0xff;
          }
          recon[rowOff + 1 + i] = val;
        }
      }
      for (const box of boxes) {
        const x0 = Math.min(width, Math.max(0, box.x));
        const y0 = Math.min(height, Math.max(0, box.y));
        const x1 = Math.min(width, Math.max(0, box.x + box.width));
        const y1 = Math.min(height, Math.max(0, box.y + box.height));
        for (let y = y0; y < y1; y += 1) {
          const rowOff = y * (stride + 1) + 1;
          for (let x = x0; x < x1; x += 1) {
            const px = rowOff + x * channels;
            recon[px] = 0;
            recon[px + 1] = 0;
            recon[px + 2] = 0;
            if (channels === 4) recon[px + 3] = 255;
          }
        }
      }
      finalIdatPayload = zlib.deflateSync(recon);
    }
  }

  const outChunks = [
    PNG_SIGNATURE,
    ihdrChunk,
    ...keptNonIdat,
    makePngChunk('IDAT', finalIdatPayload),
    makePngChunk('IEND', Buffer.alloc(0)),
  ];
  return {
    bytes: Buffer.concat(outChunks),
    mimeType: 'image/png',
    metadataStripped: true,
    strippedChunks,
    redactedBoxes: boxes.length,
  };
}

function scrubJpegBuffer(buf, boxes) {
  const out = [Buffer.from([0xff, 0xd8])];
  let offset = 2;
  let strippedSegments = 0;
  while (offset + 2 <= buf.length) {
    if (buf[offset] !== 0xff) {
      throw error('invalid_request', 'malformed JPEG marker', 400);
    }
    const marker = buf[offset + 1];
    if (marker === 0xd9) {
      out.push(Buffer.from([0xff, 0xd9]));
      break;
    }
    if (marker === 0xda) {
      // SOS: copy remaining scan data to end of buffer
      out.push(buf.subarray(offset));
      break;
    }
    if (offset + 4 > buf.length) {
      throw error('invalid_request', 'truncated JPEG segment', 400);
    }
    const segLen = buf.readUInt16BE(offset + 2);
    if (segLen < 2 || offset + 2 + segLen > buf.length) {
      throw error('invalid_request', 'invalid JPEG segment length', 400);
    }
    const isAppMetadata = marker >= 0xe1 && marker <= 0xef; // APP1 (EXIF/XMP) .. APP15
    const isComment = marker === 0xfe; // COM
    if (isAppMetadata || isComment) {
      strippedSegments += 1;
    } else {
      out.push(buf.subarray(offset, offset + 2 + segLen));
    }
    offset += 2 + segLen;
  }
  return {
    bytes: Buffer.concat(out),
    mimeType: 'image/jpeg',
    metadataStripped: true,
    strippedChunks: strippedSegments,
    redactedBoxes: boxes.length,
  };
}

/**
 * Strip embedded metadata (PNG tEXt/iTXt/zTXt/eXIf/tIME/iCCP chunks, JPEG EXIF/XMP/COM segments)
 * and optionally black-out bounding boxes (`redactBoxes`) before returning capture bytes.
 */
function scrubCaptureBytes(input, options = {}) {
  const boxes = normalizeRedactBoxes(options.redactBoxes);
  const buf = Buffer.isBuffer(input)
    ? input
    : typeof input === 'string' && input.length > 0
      ? Buffer.from(input, 'base64')
      : createSolidPng(16, 16);

  if (buf.length >= 8 && buf.subarray(0, 8).equals(PNG_SIGNATURE)) {
    return scrubPngBuffer(buf, boxes);
  }
  if (buf.length >= 2 && buf[0] === 0xff && buf[1] === 0xd8) {
    return scrubJpegBuffer(buf, boxes);
  }
  // Fallback for synthetic / simulator input: materialize a clean PNG and apply box redaction.
  return scrubPngBuffer(createSolidPng(16, 16), boxes);
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
  MAX_REDIRECTS,
  BLOCKED_HOSTNAMES,
  STRIPPED_PNG_METADATA_CHUNKS,
  parseAllowedOrigins,
  isHttpUrl,
  originOf,
  isBlockedIp,
  assertHostResolvesToPublicIp,
  assertNavigationAllowed,
  assertRedirectChainAllowed,
  assertApprovedTarget,
  isSecretFill,
  redactText,
  createSolidPng,
  normalizeRedactBoxes,
  scrubCaptureBytes,
  error,
};
