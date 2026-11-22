# Client problem error code design

`OpenConnectClient.EnsureSuccessAsync` reads `code`, then falls back to `CodeFromProblemType(type)`.
The helper accepts a plain identifier (backward compatible) or an absolute https URI without userinfo,
query or fragment and at most 256 characters with no control or whitespace characters. It returns the last
non-empty path segment when it passes `IsIdentifier`. Hosts send these types in
`EndpointRouteBuilderExtensions` (`idempotency-conflict`, `session-presence`). No OpenAPI change (PR #113 overlaps).
