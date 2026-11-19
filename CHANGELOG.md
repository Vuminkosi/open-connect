# Changelog

## Unreleased

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
