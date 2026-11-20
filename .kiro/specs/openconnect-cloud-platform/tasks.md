# OpenConnect Cloud Platform Implementation Tasks

Status: In progress. The checkboxes record repository implementation state, not deployment or production acceptance.

## Phase 0 — Baseline and boundary

- [x] Inspect the existing .NET 10 Blazor portal, public routes, opt-in assistant, package pins, and current Cloud absence.
- [x] Record portal, OIDC, control-plane, catalog, and deferred platform decisions in this Kiro spec.
- [ ] Verify Cloudflare runtime fit, worker model requirements, PostgreSQL deployment, identity provider, and region choices before provisioning.

## Phase 1 — Protected portal and control plane

- [ ] Verify Google sign-in with a configured Google OAuth client, including callback, issuer separation, and sign-out.
- [x] Add configurable OIDC authorization-code flow with PKCE and fail-fast configuration validation.
- [x] Add secure server-side cookie authentication and antiforgery-protected sign-out.
- [x] Add authorized Cloud overview and separate Cloud layout without changing the public docs layout.
- [x] Add optional PostgreSQL Cloud schema, tenant membership, application registration, explicit migration, and append-only audit records.
- [x] Scope organization/application read APIs to the OIDC issuer and subject; new organization creator is owner; app registration requires owner/admin role.
- [x] Ensure the overview does not invent entitlement, usage, or evidence records and shows database unavailability explicitly.
- [ ] Add member invitation/removal and role management with immutable audit coverage for denied and failed writes.
- [x] Verify initial subject-bound invitation creation and acceptance through an authenticated local OIDC browser session.
- [x] Verify invitation revocation in an authenticated local OIDC browser and concurrent PostgreSQL acceptance.
- [ ] Verify revocation across mixed-version deployments; implement abuse controls and safe reinvitation after historical reconciliation.
- [x] Extract capability catalog and tenant/application snapshot reads behind a persistence port, retaining policy projection in the capability feature.
- [x] Extract capability selection policy and transaction-aware persistence; verify authorization, entitlement, dependencies, optimistic revision and concurrent writes against disposable PostgreSQL.
- [x] Extract transaction-aware credential persistence so the credential service does not depend directly on EF Core; preserve shared capability snapshots, rollback and revocation checks.
- [ ] Run migration and tenant-isolation verification against disposable PostgreSQL; confirm public docs remain healthy while that database is stopped.
- [ ] Add focused route, OIDC configuration, sign-out, documentation-independence, and tenant isolation tests.

## Phase 2 — Catalog, entitlements, and credentials

- [x] Implement logical capability definitions separately from platform availability, plan grants, and tenant/application selections; qualify unverified modality/region metadata.
- [x] Implement membership-scoped catalog/selection APIs and portal views; require plan grant, availability, dependency, and parent-tenant gates before enabling.
- [x] Require `If-Match` revisions and serializable PostgreSQL transactions for selection writes; commit successful changes with audit records.
- [ ] Implement authoritative plan/subscription grant administration, deployment-driven availability updates, full change audit, and revocation.
- [x] Implement server-to-server credential issuance, one-time no-store response, hash-only persistence, scope/environment/expiry binding, rotation, and revocation.
- [x] Implement credential validation with fixed-time digest comparison, expiry/revocation checks, environment binding, and last-use metadata.
- [x] Add control-plane machine credential and current entitlement evaluation with PostgreSQL isolation and revocation checks.
- [ ] Integrate machine credentials with the Cloud gateway; verify fresh and active requests after revocation are rejected within the specified window.
- [ ] Verify credential onboarding, one-time display, rotation, and revocation in an authenticated browser with configured entitlements and PostgreSQL.
- [x] Add authorized catalog/selection APIs and UI; never place machine credentials in browser assets.
- [ ] Test every effective-entitlement gate and no-worker/no-debit denial behavior.

## Phases 3–7 — Data plane and operations

- [x] Add a separate fail-closed Cloud gateway process to the root Compose stack with liveness and fail-closed readiness.
- [x] Add opt-in pinned mutual-TLS control-plane authorization and a Node client; verify the private call against PostgreSQL.
- [x] Connect the optional private authorization client to local gateway capability requests; verify authenticated and revoked requests through mutual TLS and PostgreSQL.
- [ ] Integrate gateway dispatch with deployment-managed certificates and active-call revocation.

- [ ] Implement versioned cloud gateway, authenticated private worker adapters, typed errors, bounded streaming, admission controls, and capability routing.
- [ ] Implement signed, sanitized security receipts and coverage status without replacing application-host privacy enforcement.
- [x] Add app/capability quota buckets and atomic idempotent PostgreSQL reservation/finalization primitives; verify contention and tenant binding.
- [x] Bind quota idempotency to a stored fingerprint and fail closed for pre-fingerprint reservation retries.
- [x] Add canonical keyed fingerprint generation and persist a scoped key-version reference for retries.
- [x] Add an authenticated classification retry key-ID lookup and a Node HMAC keyring compatible with the .NET fingerprint vector; reject unavailable retained keys.
- [x] Add a database-managed quota limit revision and verify historical upgrades, older limit writers, stale writes, and counter updates.
- [x] Add scoped, audited operator quota bucket provisioning and revision-checked limit updates, with PostgreSQL concurrency and HTTP authorization tests.
- [x] Define the classification request quota unit independently of its descriptive catalog billing text and verify provisioning against the seeded catalog.
- [x] Add a private mutual-TLS classification quota reservation endpoint and Node client with credential-derived scope and stable retry inputs.
- [x] Return the original reservation expiry with the scoped retry key lookup and verify an exact Node-to-PostgreSQL replay.
- [x] Add a validated secret-file loader for active and retained fingerprint keys; runtime wiring and operational key management remain outstanding.
- [x] Add a bounded private classification execution adapter with pinned model and label validation; public routing remains disabled until fencing, egress and settlement are connected.
- [x] Prevent unexpected classification model failures from logging caller-provided task IDs or exception text that may contain request content.
- [x] Persist an append-only, single-use execution claim per quota reservation and prevent ordinary settlement from racing a claim; worker enforcement and claim-aware settlement remain outstanding.
- [x] Guard claimed reservations against older transactional finalizers, including lock-wait races, and reject an upgrade with already finalized claims.
- [x] Expose the single-use classification claim over pinned mutual TLS with credential-derived scope and a fail-closed Node client.
- [x] Require the original fingerprint and retained key ID when claiming, with mismatches rejected under the reservation lock.
- [x] Order credential revocation and rotation against execution claims using a transaction-held credential lock; test both race outcomes.
- [x] Re-evaluate locked catalog, entitlement, tenant/application selection and dependency state when claiming; test disable and claim ordering.
- [x] Add atomic idempotent full-request accounting for a trusted successful classification completion; failed and ambiguous work remains held for reconciliation.
- [x] Expose successful claim completion over pinned mutual TLS with a scoped Node client; keep revoked-credential and uncertain results held for recovery.
- [x] Add a backward-compatible classification v2 envelope with a bounded deadline and enforce it through the current privacy-admission stage.
- [x] Provide a validated, bounded request-byte processing boundary for future fingerprint admission, clearing the original buffer after success or failure; gateway quota integration remains outstanding.
- [x] Connect validated v2 request bytes to key selection, scoped fingerprint generation and the private reservation client; verify replay, rotation and changed-body conflict through mutual TLS and disposable PostgreSQL. Public routing remains outstanding.
- [x] Add tenant-managed region policies and require both tenant and verified catalog regions during classification deployment matching.
- [x] Add operator-only, audited catalog region declarations with revision checks that account for older writers; declarations are not independent geographic attestation.
- [x] Add organization and operator region policy pages with role checks, stale-write feedback and component tests; real-browser accessibility and responsive verification remain outstanding.
- [x] Bind classification claims to a locked deployment and region policy and preserve deployment/region evidence; legacy unbound claims remain held.
- [x] Add an append-only, deployment-bound single-consumption store and verify concurrent attempts against PostgreSQL; worker invocation remains outstanding.
- [x] Expose claim consumption to deployment-bound worker certificate identities over mutual TLS, rejecting gateway identities and overlapping certificate pins; worker invocation and payload binding remain outstanding.
- [x] Expose paginated tenant quota bucket reads to owners, administrators, and billing readers, with issuer-bound membership and PostgreSQL isolation checks.
- [x] Show paginated quota bucket counters in the authenticated organization workspace, with explicit empty and inaccessible states.
- [ ] Implement key distribution and retention, gateway fingerprint integration, quota admission, execution fencing, overage/orphan reconciliation, idempotent usage ledger, policy administration, and usage reporting.
- [x] Add a separate persistent deployment inventory with opaque endpoint references, model/image digest metadata, multiple deployments per capability, and optimistic revision checks.
- [x] Add a read-only operator inventory API gated by explicitly configured, provider-bound issuer/subject identities.
- [x] Record deployment registry write outcomes with actor and trace metadata, atomically with successful writes; verify concurrent conflicts and failed writes in PostgreSQL.
- [x] Expose operator deployment PUT with configured-identity authorization, antiforgery, bounded JSON, optimistic revisions and sanitized audit; exercise the HTTP and PostgreSQL path.
- [x] Add an operator workspace for catalog-filtered deployment registration and enable/disable actions with audited revision checks.
- [x] Add a private mutual-TLS classification readiness probe that checks real engine and capability advertisement; verify with a simulated worker over mutual TLS.
- [x] Configure and invoke that probe after classification authorization; verify fake-engine rejection against the actual worker behind a local mutual-TLS proxy.
- [x] Add a versioned Cloud classification request schema and OpenAPI description, with bounded post-authorization JSON validation and shared fixtures.
- [x] Add a fail-closed Cloud-side rules check for inline classification text behind pinned mutual TLS and machine authorization; verify the gateway-to-PostgreSQL path with simulated readiness.
- [x] Persist Cloud classification privacy observations as tenant-scoped append-only metadata before returning decisions; fail closed if storage fails.
- [x] Propagate version-00 trace IDs through gateway authorization, readiness and privacy checks and correlate privacy observations in PostgreSQL.
- [x] Require an enabled, exact deployment inventory match through the authenticated control plane before classification readiness probing.
- [x] Expose audited, role-scoped Cloud privacy observations with UTC filtering and keyset pagination in the tenant API and workspace.
- [ ] Implement worker deployment registry and capability-specific inference, LiveUI planning, classification, voice, transcription, translation, and separately gated operations.
- [ ] Add independent CI, local development topology, observability, release gates, staging evidence, runbooks, and rollback verification.
- [ ] Pass every applicable acceptance test in the main cloud requirements specification; keep real provider, browser, deployment, and production evidence distinct from unit tests.
