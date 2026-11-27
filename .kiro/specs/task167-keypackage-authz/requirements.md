# Issue #167 requirements (T2)

- AC-1: Consuming key packages for another principal or an agent requires an `IMessagingAuthorizer` allow decision; no authorizer or a deny returns 403 and `ConsumeOneAsync` is not called.
- AC-2: Device-only lookups return 403.
- AC-3: Per-caller and per-target limits return 429 with `Retry-After`; denied attempts count against the caller.
- AC-4: Denied responses do not reveal package existence.
- AC-5: GET is retained and the decision is recorded in ADR-IMSG-004 and the OpenAPI description; 403 and 429 are documented.
- AC-6: Tests in `tests/Unit/OpenConnect.Hosting.Tests/MessagingKeyPackageConsumeTests.cs` cover AC-1 to AC-4 and tenant isolation.
