# OpenConnect Cloud Platform Design Decisions

## Code organization

Cloud code is grouped by feature (`Tenants`, `Capabilities`, and `Credentials`), with HTTP routing in `Http` and database infrastructure in `Persistence`. HTTP handlers delegate database operations to services. Tenant operations use a persistence port; membership checks and writes remain together in the persistence implementation. Capability and credential services still access EF Core directly and require persistence separation.

Status: Decisions for the current portal identity foundation; broader platform design remains governed by [`docs/plans/openconnect-cloud-requirements-spec.md`](../../../docs/plans/openconnect-cloud-requirements-spec.md).

## OC-CLOUD-ADR-001: Extend the existing Blazor portal

**Decision:** Keep the current ASP.NET Core / .NET 10 Blazor application as the public documentation host and add a separate Cloud layout and protected routes.

**Rationale:** The existing site is static-rendered by default and already hosts documentation and API reference. Cloud services must not make those public routes depend on new infrastructure.

**Status:** Implemented in the portal route/layout layer. Tenant/application and initial catalog persistence are implemented; entitlement administration and data-plane services remain pending.

## OC-CLOUD-ADR-002: External OIDC with server-side session cookie

**Decision:** Cloud identity is opt-in configuration. When enabled, use external OIDC authorization code flow with PKCE and a `__Host-` HttpOnly, Secure, SameSite=Lax session cookie. Do not add local password authentication. Sign-out is an authenticated, antiforgery-protected POST and also ends the OIDC session.

**Rationale:** The Cloud portal must not invent password handling, expose provider credentials to browser assets, or weaken public documentation availability.

**Configuration:** `OpenConnect:Cloud:Identity:Enabled`, `Authority`, `ClientId`, and `ClientSecret`. Supply the client secret through the deployment's secret store. Enabling identity with any required value absent fails startup. With identity disabled, Cloud sign-in returns service unavailable and no Cloud identity is asserted.

**Status:** Implemented; external provider interoperability and production cookie behavior still require deployment-specific verification.

## OC-CLOUD-ADR-003: No synthetic tenant or security state

**Decision:** The dashboard reads organization and service state from the Cloud control-plane store. It must not manufacture data, treat selection as execution authorization, or translate detector output into enforcement claims. Usage and evidence remain explicitly unavailable until connected to authoritative sources.

**Rationale:** A Cloud security or entitlement dashboard is an evidence surface; missing authoritative records must remain visibly missing.

**Status:** Implemented for the organization/application/catalog dashboard. Plan entitlement administration, receipt ingestion, usage, and security evidence remain pending.

## OC-CLOUD-ADR-004: Separate PostgreSQL Cloud control-plane schema

**Decision:** Keep Cloud entities in a dedicated `cloud` schema and DbContext in the portal host, isolated from public documentation readiness and existing application persistence. Configure it only through `ConnectionStrings:CloudControlPlane`. Do not apply migrations automatically at portal startup; manage them explicitly with the repository-pinned EF tool.

**Rationale:** The public portal must remain available when Cloud PostgreSQL is absent or unavailable, and Cloud migrations must not alter existing application tables.

**Status:** Initial PostgreSQL migration and model snapshot are implemented. A live database migration and restore test have not been run.

## OC-CLOUD-ADR-005: Tenant-scoped identity and initial roles

**Decision:** Bind each membership to the configured OIDC authority and validated `sub` claim. Organization creation atomically makes the creating identity an `Owner`. Only `Owner` and `Administrator` may create applications. Tenant reads always filter by both tenant ID and the authenticated identity's membership; unknown and non-member tenant access returns the same not-found response.

**Rationale:** Tenant IDs are identifiers, not credentials. Every resource read must independently establish membership, and app registration changes are privileged within a tenant.

**Status:** Implemented for organization/application creation and reads. Invitations, role changes, removal, recovery, broader role actions, and provider-to-provider subject migration remain pending.

## OC-CLOUD-ADR-006: Separate capability definitions, entitlements, and selections

**Decision:** Store logical capability definitions separately from worker deployment state, tenant plan grants, tenant selections, and application selections. An entitlement or enabled selection alone never means that a backend is ready. The initial catalog marks platform availability `not_configured` and marks modality/region metadata unverified until source contracts and deployment policy establish them.

**Authorization rule:** Organization enablement requires a current plan grant, platform availability, and all recursively declared dependencies to be entitled and selected. Application enablement additionally requires the organization selection. The future Cloud gateway must still validate credential scope, policy, region, deployment readiness, and quota before any execution.

Application dependencies must also be selected for that application. Read views and selection writes evaluate the recursive dependency chain at both organization and application scope.

**Concurrency:** Service-selection API writes require a quoted numeric `If-Match` revision. Updates use EF concurrency tokens and a PostgreSQL serializable transaction. Successful changes and audit events commit together. Stale updates return conflict; disabling remains possible after availability or entitlement loss.

**Status:** Catalog/selection persistence, authenticated read APIs, revisioned write APIs, and portal views are implemented. Plan grants, platform deployment health updates, gateway admission, quota, and PostgreSQL concurrency execution remain pending.

## OC-CLOUD-ADR-007: One-time scoped machine credentials

**Decision:** Issue credentials only to tenant owners/administrators for an application and a non-empty list of currently selectable service scopes. Generate a 256-bit random secret, return it once with `Cache-Control: no-store`, and persist only SHA-256 of the full key plus a short fingerprint. Credential metadata binds tenant, application, environment, scopes, issue/expiry/revocation/last-use. Rotation atomically revokes the previous credential and creates its replacement. Revocation takes effect in the control-plane store before the API returns.

**Authentication format:** `oc_<environment>_<credential-guid>_<base64url-random-secret>`. Validation performs fixed-time hash comparison and checks revocation, expiration, and application environment. The resulting credential identity and scopes are inputs to later gateway admission; successful credential authentication alone does not authorize a capability call.

The parser splits only the first three separators because the base64url secret may itself contain underscores.

**Exposure boundary:** Credential material is returned only by the authenticated management API at issuance/rotation, never placed in static assets or logged by the control plane. The caller must transfer it to server-side secret storage. No management UI displays or persists the raw value.

**Status:** Hash-only persistence, one-time API response, list, rotate, revoke, and credential validation logic are implemented. Gateway integration, credential rate limits/abuse controls, real database revocation-latency proof, and client onboarding UX remain pending.

## Deferred decisions

Plan-grant administration, policy cache/revocation, gateway runtime, worker authentication, receipt signature format, quota authority, retention, and billing are intentionally not decided by this portal/control-plane foundation. Resolve each against the main requirements and source contracts before its implementation; do not infer production behavior from a UI placeholder.
