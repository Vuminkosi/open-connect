# Security worker vertical slice design

## Boundary

The Python worker detects and returns spans. The .NET host remains the authority for privacy policy, authorization, tokenization, vault restoration and external egress. The worker receives scan requests over private authenticated HTTP and has no database or vault access.

## Execution

1. Validate the worker Compose file and image policy.
2. Run offline fixture/schema/cache tests with no Docker or network.
3. Build the canonical `workers/security/Dockerfile` locally with the sample tag.
4. Wait for `/health/ready` with a bounded timeout.
5. POST the synthetic rules fixture with the bearer token.
6. Assert the response schema and expected spans.
7. Assert unauthenticated requests return 401.
8. Assert a request requiring disabled `deberta-injection` returns 503.
9. Sanitize logs and remove only the isolated Compose project, preserving named volumes.

## Compatibility and failure behavior

Rules-only is CPU-only and requires no model manifest. Model-backed detectors remain unavailable unless an approved immutable manifest and verified cache exist. A failed required detector blocks the protected flow rather than passing raw content.

## Evidence

The sample runner must emit machine-readable result metadata and a human-readable summary. Evidence is local/CI only until a release review approves publication.
