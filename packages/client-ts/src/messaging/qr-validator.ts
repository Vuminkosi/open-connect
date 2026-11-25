import type { DeviceEnrollmentQrPayload } from "./types.js";

export const SUPPORTED_QR_ENROLLMENT_VERSION = 1;
export const EXPECTED_QR_ENROLLMENT_TYPE = "openconnect.device.enrollment";
export const MAX_QR_PAYLOAD_BYTES = 4096;

export class MessagingQrValidationError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "MessagingQrValidationError";
    this.code = code;
  }
}

export interface QrValidationOptions {
  readonly now?: Date;
  readonly allowLoopbackHttp?: boolean;
}

/**
 * Normalizes and strictly validates a gateway base URL.
 * Rejects non-HTTPS (unless explicit loopback test mode), embedded userinfo,
 * query parameters, fragments, and path traversal segments.
 */
export function normalizeMessagingGatewayUrl(
  rawUrl: string,
  allowLoopbackHttp = false
): string {
  let parsed: URL;
  try {
    parsed = new URL(rawUrl);
  } catch {
    throw new MessagingQrValidationError(
      "invalid_gateway_url",
      "Gateway URL is malformed."
    );
  }

  const isHttps = parsed.protocol === "https:";
  const isLoopback =
    parsed.hostname === "localhost" ||
    parsed.hostname === "127.0.0.1" ||
    parsed.hostname === "::1" ||
    parsed.hostname === "[::1]";
  const isAllowedLoopbackHttp =
    allowLoopbackHttp && parsed.protocol === "http:" && isLoopback;

  if (!isHttps && !isAllowedLoopbackHttp) {
    throw new MessagingQrValidationError(
      "insecure_gateway_url",
      "Gateway URL must use HTTPS."
    );
  }

  if (parsed.username !== "" || parsed.password !== "") {
    throw new MessagingQrValidationError(
      "invalid_gateway_url",
      "Gateway URL must not contain embedded credentials."
    );
  }

  if (parsed.search !== "" || parsed.hash !== "") {
    throw new MessagingQrValidationError(
      "invalid_gateway_url",
      "Gateway URL must not contain query parameters or fragments."
    );
  }

  if (rawUrl.includes("..") || rawUrl.includes("\\")) {
    throw new MessagingQrValidationError(
      "invalid_gateway_url",
      "Gateway URL contains invalid path segments."
    );
  }

  const normalizedPath = parsed.pathname.replace(/\/+$/, "") + "/";
  return `${parsed.origin}${normalizedPath}`;
}

/**
 * Parses and strictly validates a desktop-to-mobile QR enrollment payload.
 * Never accepts desktop bearer/refresh tokens; enforces HTTPS, schema version,
 * size limit, and expiry.
 */
export function parseAndValidateEnrollmentQr(
  rawPayload: string,
  options: QrValidationOptions = {}
): DeviceEnrollmentQrPayload {
  if (!rawPayload || rawPayload.trim().length === 0) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload is empty."
    );
  }

  const byteLength = new TextEncoder().encode(rawPayload).byteLength;
  if (byteLength > MAX_QR_PAYLOAD_BYTES) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload exceeds maximum allowed size."
    );
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(rawPayload);
  } catch {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload is not valid JSON."
    );
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload must be a JSON object."
    );
  }

  const record = parsed as Record<string, unknown>;
  if (record.version !== SUPPORTED_QR_ENROLLMENT_VERSION) {
    throw new MessagingQrValidationError(
      "unsupported_qr_version",
      `Unsupported QR enrollment version: ${String(record.version)}.`
    );
  }

  if (record.type !== EXPECTED_QR_ENROLLMENT_TYPE) {
    throw new MessagingQrValidationError(
      "invalid_qr_type",
      "QR payload type is not openconnect.device.enrollment."
    );
  }

  if (
    typeof record.baseGatewayUrl !== "string" ||
    typeof record.tenantId !== "string" ||
    typeof record.applicationId !== "string" ||
    typeof record.pairingId !== "string" ||
    typeof record.enrollmentToken !== "string" ||
    typeof record.expiresAt !== "string"
  ) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload is missing required string fields."
    );
  }

  const normalizedUrl = normalizeMessagingGatewayUrl(
    record.baseGatewayUrl,
    options.allowLoopbackHttp ?? false
  );

  if (
    record.tenantId.trim().length === 0 ||
    record.applicationId.trim().length === 0 ||
    record.pairingId.trim().length === 0
  ) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR payload has empty tenant, application, or pairing identifiers."
    );
  }

  if (record.enrollmentToken.trim().length < 16) {
    throw new MessagingQrValidationError(
      "invalid_qr_payload",
      "QR enrollment token is missing or too short."
    );
  }

  const expiresAtMs = Date.parse(record.expiresAt);
  const nowMs = (options.now ?? new Date()).getTime();
  if (Number.isNaN(expiresAtMs) || expiresAtMs <= nowMs) {
    throw new MessagingQrValidationError(
      "enrollment_expired",
      "QR enrollment code has expired."
    );
  }

  return {
    version: SUPPORTED_QR_ENROLLMENT_VERSION,
    type: EXPECTED_QR_ENROLLMENT_TYPE,
    baseGatewayUrl: normalizedUrl,
    tenantId: record.tenantId.trim(),
    applicationId: record.applicationId.trim(),
    pairingId: record.pairingId.trim(),
    enrollmentToken: record.enrollmentToken.trim(),
    expiresAt: new Date(expiresAtMs).toISOString(),
  };
}
