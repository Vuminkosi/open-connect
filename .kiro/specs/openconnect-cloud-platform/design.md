# OpenConnect Cloud Platform Design Decisions

## Code organization

Cloud code is grouped by feature (`Tenants`, `Capabilities`, and `Credentials`), with HTTP routing in `Http` and database infrastructure in `Persistence`. HTTP handlers delegate database operations to services. Tenant operations use a persistence port; membership checks and writes remain together in the persistence implementation. Capability reads load tenant-scoped snapshots through a persistence port and project entitlement and dependency policy in the capability feature. Capability selection policy runs in the capability feature through a transaction-aware persistence port; its adapter loads authorization and selection facts, commits selection and audit together, and maps serialization conflicts without claiming an unobserved revision. Credential secret generation, parsing, environment tags, and fixed-time comparison are isolated in the credential feature. Credential operations use a transaction-aware persistence session; EF Core owns context lifetime, serializable transactions and persistence queries. Credential issuance and rotation continue to load capability data using the same EF context and serializable transaction as the credential write.

Status: Decisions for the current portal identity foundation; broader platform design remains governed by [`docs/plans/openconnect-cloud-requirements-spec.md`](../../../docs/plans/openconnect-cloud-requirements-spec.md).

## OC-CLOUD-ADR-001: Extend the existing Blazor portal

**Decision:** Keep the current ASP.NET Core / .NET 10 Blazor application as the public documentation host and add a separate Cloud layout and protected routes.

**Rationale:** The existing site is static-rendered by default and already hosts documentation and API reference. Cloud services must not make those public routes depend on new infrastructure.

**Status:** Implemented in the portal route/layout layer. Tenant/application and initial catalog persistence are implemented; entitlement administration and data-plane services remain pending.

## OC-CLOUD-ADR-002: External OIDC with server-side session cookie

**Decision:** Cloud identity is opt-in configuration. When enabled, use external OIDC authorization code flow with PKCE and a `__Host-` HttpOnly, Secure, SameSite=Lax session cookie. Do not add local password authentication. Sign-out is an authenticated, antiforgery-protected POST. Generic OIDC sign-out also ends the provider session; Google sign-out ends the Cloud session only.

**Rationale:** The Cloud portal must not invent password handling, expose provider credentials to browser assets, or weaken public documentation availability.

**Configuration:** `OpenConnect:Cloud:Identity:Enabled`, `Authority`, `ClientId`, and `ClientSecret`. Supply the client secret through the deployment's secret store. Enabling identity with any required value absent fails startup. Google sign-in can be independently enabled with `OpenConnect:Cloud:Identity:Google:Enabled`, `Google:ClientId`, and `Google:ClientSecret`. Register `/cloud/signin-google` as an exact authorized redirect URI on the public HTTPS origin. The Google issuer is `https://accounts.google.com`; validated subjects remain separated by issuer. Existing OIDC cookies without a locally stamped issuer remain valid only when the original subject claim issuer matches the enabled configured authority; other legacy sessions must sign in again. The session cookie is rejected when its provider is disabled or its stamped issuer no longer matches an enabled provider. With both providers disabled, Cloud sign-in returns service unavailable and no Cloud identity is asserted. Google client credentials belong in the deployment secret store. Local in-process HTTP tests exercise provider selection, disabled-provider behavior, authorization redirects and PKCE with synthetic discovery metadata. Focused tests also cover issuer claim replacement and provider-scoped invitations. An authenticated Google callback, browser sign-out, and deployment remain unverified.

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

**Decision:** Bind each membership to its validated identity provider issuer and `sub` claim. Organization creation atomically makes the creating identity an `Owner`. Only `Owner` and `Administrator` may create applications. Tenant reads always filter by both tenant ID and the authenticated identity's membership; unknown and non-member tenant access returns the same not-found response.

**Rationale:** Tenant IDs are identifiers, not credentials. Every resource read must independently establish membership, and app registration changes are privileged within a tenant.

**Status:** Implemented for organization/application creation and reads. Invitations, role changes, removal, recovery, broader role actions, and provider-to-provider subject migration remain pending.

Application creation reads the actor's membership and writes the application and audit event in the same serializable transaction. Concurrent membership administration must use a compatible transactional protocol and handle serialization failures without granting access.

## OC-CLOUD-ADR-006: Separate capability definitions, entitlements, and selections

**Decision:** Store logical capability definitions separately from worker deployment state, tenant plan grants, tenant selections, and application selections. An entitlement or enabled selection alone never means that a backend is ready. The initial catalog marks platform availability `not_configured` and marks modality/region metadata unverified until source contracts and deployment policy establish them.

**Authorization rule:** Organization enablement requires a current plan grant, platform availability, and all recursively declared dependencies to be entitled and selected. Application enablement additionally requires the organization selection. The future Cloud gateway must still validate credential scope, policy, region, deployment readiness, and quota before any execution.

Application dependencies must also be selected for that application. Read views and selection writes evaluate the recursive dependency chain at both organization and application scope.

Capability eligibility and dependency evaluation are isolated in a policy component. Persistence remains responsible for obtaining the relevant state inside the applicable transaction; the policy does not issue database queries.

**Concurrency:** Service-selection API writes require a quoted numeric `If-Match` revision. Updates use EF concurrency tokens and a PostgreSQL serializable transaction. Successful changes and audit events commit together. Stale updates and PostgreSQL serialization conflicts return conflict; disabling remains possible after availability or entitlement loss. Wrapped PostgreSQL conflicts do not return a guessed current revision; clients reload.

**Status:** Catalog/selection persistence, authenticated read APIs, revisioned write APIs, and portal views are implemented. Plan grants, platform deployment health updates, gateway admission, quota, and PostgreSQL concurrency execution remain pending.

## OC-CLOUD-ADR-007: One-time scoped machine credentials

**Decision:** Issue credentials only to tenant owners/administrators for an application and a non-empty list of currently selectable service scopes. Generate a 256-bit random secret, return it once with `Cache-Control: no-store`, and persist only SHA-256 of the full key plus a short fingerprint. Credential metadata binds tenant, application, environment, scopes, issue/expiry/revocation/last-use. Rotation atomically revokes the previous credential and creates its replacement. Revocation takes effect in the control-plane store before the API returns.

Issuance and rotation require an explicit future expiry; permanent credentials are rejected. Authentication rejects existing credentials without an expiry; issue replacements with an expiry.

Revocation and rotation read and modify credential state in serializable transactions. A concurrent update must not silently overwrite a revocation; serialization failures require callers to reload and retry the operation.

Credential authentication rechecks revocation, expiry, and application environment in the database when recording last use. Revocation after this check still requires gateway admission and active-session cancellation; authentication alone is not execution authorization.

**Authentication format:** `oc_<environment>_<credential-guid>_<base64url-random-secret>`. Validation performs fixed-time hash comparison and checks revocation, expiration, and application environment. The resulting credential identity and scopes are inputs to later gateway admission; successful credential authentication alone does not authorize a capability call.

The parser splits only the first three separators because the base64url secret may itself contain underscores.

**Exposure boundary:** Credential material is returned once by the authenticated management API or displayed once in the authenticated interactive portal at issuance/rotation. It is never placed in static assets or logged by the control plane. The operator must transfer it to server-side secret storage; the portal clears its displayed value when dismissed or when navigating to another application.

The portal associates asynchronous reads and writes with the selected application generation. Results from a prior selection are discarded when navigation or disposal occurs; a credential issued during navigation may need revocation if its one-time value was not displayed.

Credential rows distinguish expired and revoked credentials and label repeated management actions with their fingerprint. Cloud controls use explicit light and dark focus and button styling. A local OIDC browser check covered keyboard focus and credential creation, dismissal, and revocation in light mode at 390px and dark mode at 1280px; broader assistive-technology and deployment verification remains required.

Machine admission now validates a server credential, derives its credential, tenant and application identity and checks its scope before reading current entitlements and selections. A repeatable-read database snapshot rechecks credential expiry, revocation and application binding and evaluates the capability dependency policy. This is a control-plane component only: it does not sign snapshots, implement residency or deployment policy, reserve quota, cancel active streams or authorize gateway dispatch. Credential and selection changes occurring after its snapshot need a separately defined active-call revocation policy.

**Status:** Hash-only persistence, one-time API response, list, rotate, revoke, and credential validation logic are implemented. Gateway integration, credential rate limits/abuse controls, real database revocation-latency proof, and client onboarding UX remain pending.

Issuance and rotation evaluate capability scopes in the same serializable database transaction as the credential write. The credential persistence session loads the capability snapshot through the same EF context and serializable transaction as the credential write, without exposing EF Core to the credential service. A disposable PostgreSQL test verifies tenant and application read isolation and selection projection, alongside the credential issuance and revocation concurrency tests.

## Deferred decisions

The root Compose stack includes a separate `cloud-gateway` process with a local Node adapter around a Worker-compatible request handler. Its liveness endpoint responds while all other requests fail closed with 503 until identity, admission, quota, and worker routing are implemented. This local process does not establish Cloudflare deployment or production readiness.

Plan-grant administration, policy cache/revocation, gateway runtime, worker authentication, receipt signature format, quota authority, retention, and billing are intentionally not decided by this portal/control-plane foundation. Resolve each against the main requirements and source contracts before its implementation; do not infer production behavior from a UI placeholder.

## OC-CLOUD-ADR-008: Initial atomic quota reservations

Application and capability quota buckets use integer units, a bounded period, an explicit hard limit, and separate reserved and consumed totals. No default allowance is inferred when a bucket is missing or periods overlap. A conditional PostgreSQL update reserves capacity atomically; a tenant/application-scoped request ID and 32-byte request fingerprint bind retries, and repeated requests return a duplicate status that does not authorize a second execution. A mismatched fingerprint or key version is a conflict. The canonical HMAC-SHA-256 input uses length-prefixed version, key ID, tenant, application, capability, HTTP method, route, media type, and authorized request bytes. The key is supplied by trusted server-side infrastructure and must be at least 256 bits. Reservations retain the key version for scoped retry lookup; unknown or missing historic key versions must fail closed. Reservations written before fingerprint or key-version support remain held and their retries fail closed as conflicts; finalization remains available. The database binds each reservation to the same tenant and application as its bucket. Finalization moves an amount no greater than the reservation from held to consumed in one transaction and is idempotent. A measured overage remains held for reconciliation rather than being silently discarded.

Expired reservations are not automatically released: without proof that work stopped, automatic release could overspend the hard limit. This is a deliberate fail-closed capacity leak pending a durable recovery and execution-fencing design. The reservation and finalization methods are internal building blocks; they do not yet provide gateway-side fingerprint integration, secret distribution and retention, a usage ledger, billing, reservation cleanup, policy administration, trusted gateway integration, or execution authorization. Before gateway use, an authenticated caller must compute the keyed HMAC using a retained server-side key; storing a plain content hash could disclose low-entropy payloads. Deployment key rotation and mixed-version execution policy remain unresolved. Period/expiry evaluation currently requires synchronized control-plane clocks. A disposable PostgreSQL test covers migration of existing control-plane data, 100 concurrent reservations near the limit, repeated requests and finalization, tenant binding, and retained expired holds.

## OC-CLOUD-ADR-009: Usage capture during quota finalization

A database trigger atomically captures usage on quota finalization, including writes from earlier application versions. Existing finalized reservations are backfilled with legacy provenance; capability and unit remain unknown for these historical records. Finalized reservations reject mutation; usage records reject mutation and truncation. The migration deliberately disallows automatic downgrade to protect recorded usage. This is reported quota usage, not a billing ledger: provider evidence, pricing, corrections, reconciliation, authorization, and production rollout remain outstanding.

Disposable PostgreSQL tests verify legacy backfill, old-writer capture, rollback, and mutation rejection. Production-scale migration behavior remains unverified.
## Tenant quota reads

The authenticated `GET /cloud/api/v1/tenants/{tenantId}/quotas` endpoint returns paginated persisted bucket limits, consumed amounts and outstanding reservations. Only the tenant's owner, administrator or billing reader may read them; membership includes both identity issuer and subject. The persistence query repeats the membership predicate when loading rows. Responses are not cached. Bucket amounts are operational quota counters, not invoices, finalized charges or a complete usage report. Offset pagination can shift if buckets are inserted or removed between requests. Quota policy administration, usage reporting and gateway enforcement remain outstanding.

The organization quota page uses the same authorized query service and displays periods in UTC. It clears previous organization data before loading another organization and ignores stale asynchronous results. The workspace links to it only for the permitted roles; the service remains the authorization boundary.

An in-process Kestrel test with signed authentication cookies and disposable PostgreSQL exercises the quota HTTP route, including anonymous rejection, permitted billing access, cross-tenant and developer denial, paging, and page-size validation. This test uses a test-only cookie issuance endpoint; it does not verify a real identity-provider callback or deployed network.
