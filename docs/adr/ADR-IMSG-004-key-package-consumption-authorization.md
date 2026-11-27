# ADR-IMSG-004: Key Package Consumption Authorization and Quota

- **Status:** Accepted
- **Scope:** `GET /openconnect/v1/messaging/keys/packages`, `OpenConnect.Hosting.AspNetCore.Messaging`
- **Issue:** #167

## Context

The endpoint consumes a single-use public key package. Previously any authenticated principal in the tenant could consume packages for any `principalId`, `agentId` or `deviceId`, with no authorization hook and no limit. A caller could exhaust a victim's packages (denial of service for new conversations) and probe package existence.

## Decision

1. **Authorization hook.** The host's `IMessagingAuthorizer` decides with `MessagingPermission.Initiate`, the caller as actor and the package owner as `target`. Agent and principal owners are each authorized. Fail closed: no registered authorizer means denial.
2. **Self-service.** A caller consuming only its own `principalId` needs no authorizer decision.
3. **Device-only lookups are denied** because the owner cannot be authorized.
4. **Uniform denial.** Authorization runs before the store is touched. Denied callers always get 403 with a fixed message, so package existence is not disclosed.
5. **Quota.** `MessagingHostingOptions.KeyPackageConsumeLimitPerCaller` (default 30), `KeyPackageConsumeLimitPerTarget` (default 60) and `KeyPackageConsumeWindow` (default 1 minute) bound consumption. Denied attempts count against the caller. Exceeding a limit returns 429 with `Retry-After`. Non-positive values deny all. The in-memory fixed-window counter caps tracked keys at 10,000 and rejects new keys when full. Multi-instance hosts get per-instance limits.
6. **GET vs POST.** The method stays GET for backward compatibility with shipped .NET and TypeScript clients. The OpenAPI description documents that the call is state-changing and must not be cached. A POST alias can be added in a later minor version without breaking clients.

## Consequences

Hosts must register an `IMessagingAuthorizer` that permits initiators to fetch key packages for allowed targets, otherwise cross-principal consumption returns 403.
