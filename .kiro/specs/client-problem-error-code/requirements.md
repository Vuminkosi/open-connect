# Client problem error code requirements

**Tier:** T1. **Issue:** #110.

- **CLIENT-ERR-REQ-001:** An absolute `https` problem `type` URI yields `ErrorCode` equal to its last non-empty path segment.
- **CLIENT-ERR-REQ-002:** A valid body `code` (1 to 64 ASCII letters, digits, `_`, `-`) takes precedence over `type`.
- **CLIENT-ERR-REQ-003:** Types longer than 256 characters, relative, non-https, malformed, or containing whitespace, control characters, userinfo, query, fragment, percent-escapes or `.`/`..` raw path segments yield `null`. Non-string or missing `type` yields `null`.
- **CLIENT-ERR-REQ-004:** A derived segment must satisfy the same identifier rule as `code`; otherwise `null`.
- **CLIENT-ERR-REQ-005:** `Message` stays fixed text and never contains `title` or `detail`. Public API is unchanged.
- **CLIENT-ERR-REQ-006:** The 422 replay mapping to `legacy_event_without_identity` is unchanged.
