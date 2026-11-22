# Changelog

## Unreleased

- Fixed `OpenConnectClientException.ErrorCode` being null for host Problem Details (#110). When the body has no valid `code`, the SDK client now derives the code from the last path segment of an absolute `https` problem `type` URI (for example `https://openconnect.dev/problems/idempotency-conflict` gives `idempotency-conflict`). A body `code` still takes precedence. `type` values that are over 256 characters, relative, non-https, or that contain whitespace, control characters, userinfo, query or fragment yield null, as do segments that are not 1 to 64 ASCII letters, digits, `_` or `-`. `Message` remains fixed text and never includes the problem `title` or `detail`.

- The TypeScript task client now sends the host's `message` and optional `sessionId` fields and returns the host's `id`, tenant, principal, and lifecycle state. Callers must replace the former `input`/`agentId` request and `taskId` response shape; the old request shape is rejected rather than silently dropping the prompt.

- Enabled Qdrant event indexing now requires a host-provided `IProjectionReader`; the default empty reader is rejected at startup. Skipped and failed deliveries have sanitized diagnostics, and skips have a separate metric outcome.

- Semantic memory search now returns relevance scores instead of always returning zero. Qdrant searches fail on a returned vector dimension mismatch rather than silently omitting the result.

- LiveUI compiled interactive targets now require confirmation by default when they expose activation, value-setting, or selection capabilities. This changes generated manifest hashes and can produce `approval_required` for hosts without a confirmation provider. Tabs and links remain conservative because static markup cannot prove their handlers are side-effect free.

- Fixed speech synthesis worker failure handling (#66): transport/read failures now
  return generic 502 Problem Details and worker timeouts return 504. Worker 503 is
  preserved; worker 408/504 map to 504; all other unsuccessful worker statuses,
  including 401/403/429, map to 502 instead of being passed through. Clients relying
  on downstream 4xx passthrough should handle these gateway statuses instead.
  Caller cancellation propagates without a gateway error response or error log.
  Endpoint diagnostics contain only failure types or status codes, not request text,
  worker response bodies, exception details, or service tokens. Public APIs and
  caller authentication/validation behavior are unchanged.
  Host-configured HTTP client/delegating-handler logging remains host-owned; review
  those providers separately when configuring sensitive service diagnostics.
