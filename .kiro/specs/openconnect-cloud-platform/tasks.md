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
- [ ] Integrate machine credentials with the Cloud gateway; verify fresh and active requests after revocation are rejected within the specified window.
- [ ] Verify credential onboarding, one-time display, rotation, and revocation in an authenticated browser with configured entitlements and PostgreSQL.
- [x] Add authorized catalog/selection APIs and UI; never place machine credentials in browser assets.
- [ ] Test every effective-entitlement gate and no-worker/no-debit denial behavior.

## Phases 3–7 — Data plane and operations

- [x] Add a separate fail-closed Cloud gateway process to the root Compose stack with liveness only.

- [ ] Implement versioned cloud gateway, authenticated private worker adapters, typed errors, bounded streaming, admission controls, and capability routing.
- [ ] Implement signed, sanitized security receipts and coverage status without replacing application-host privacy enforcement.
- [ ] Implement strongly consistent quota reservations, idempotent usage ledger, reconciliation, and usage reporting.
- [ ] Implement worker deployment registry and capability-specific inference, LiveUI planning, classification, voice, transcription, translation, and separately gated operations.
- [ ] Add independent CI, local development topology, observability, release gates, staging evidence, runbooks, and rollback verification.
- [ ] Pass every applicable acceptance test in the main cloud requirements specification; keep real provider, browser, deployment, and production evidence distinct from unit tests.
