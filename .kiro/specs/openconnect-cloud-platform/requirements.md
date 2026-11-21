# OpenConnect Cloud Platform Requirements

Status: Active implementation specification. The authoritative requirements and acceptance matrix remain in [`docs/plans/openconnect-cloud-requirements-spec.md`](../../../docs/plans/openconnect-cloud-requirements-spec.md).

## Current implementation slice

- **CLOUD-PORTAL-001:** Preserve anonymous documentation and health routes without a Cloud database, identity provider, worker, or billing service.
- **CLOUD-PORTAL-002:** Add a separately styled Cloud area to the existing Blazor portal; public documentation remains the default layout and render mode.
- **CLOUD-IDENTITY-001:** When explicitly enabled, authenticate Cloud users through a configured external OIDC provider using authorization code flow with PKCE and a server-side secure, HttpOnly cookie.
- **CLOUD-IDENTITY-GOOGLE:** Support independently configurable Google OIDC sign-in alongside the generic provider and keep provider identities distinct by issuer and subject.
- **CLOUD-IDENTITY-002:** Reject incomplete OIDC configuration at startup when Cloud identity is enabled. Do not provide a local password fallback.
- **CLOUD-AUTHZ-001:** Require server-side authorization for Cloud pages and sign-out. Sign-out is POST-only and antiforgery protected.
- **CLOUD-TENANT-001:** Persist organizations and memberships; organization bootstrap atomically establishes the authenticated creator as owner.
- **CLOUD-APP-001:** Persist tenant-owned applications with constrained environment values; only tenant owners and administrators can create them.
- **CLOUD-AUDIT-001:** Commit successful organization/application writes with append-only audit records in the same database transaction.
- **CLOUD-API-001:** Scope organization and application reads to the authenticated OIDC issuer and subject; inaccessible tenant identifiers return no tenant data.
- **CLOUD-CATALOG-001:** Keep logical service definitions independent from worker deployment inventory, with explicit contract, billing, dependency, region, modality, and platform availability metadata.
- **CLOUD-ENTITLEMENT-001:** Enable organization capabilities only when a plan grant, platform availability, and dependencies permit it; application selection is a subset of organization selection.
- **CLOUD-CONCURRENCY-001:** Require `If-Match` revisions for selection writes and reject stale concurrent changes.
- **CLOUD-CREDENTIAL-001:** Issue one-time server credentials bound to tenant, application, environment, and non-empty service scopes; store only a one-way hash, and support list, rotation, expiry, and revocation.
- **CLOUD-CREDENTIAL-002:** Credential authentication must not grant service execution; every Cloud call still passes entitlement, selection, policy, region, quota, and deployment admission.
- **CLOUD-TRUTH-001:** Show tenant/application and capability state only from the control-plane store. Usage and firewall evidence remain unavailable until their authoritative APIs and persistence exist; label those gaps explicitly.
- **CLOUD-PORTAL-003:** Keep the existing assistant opt-in and independent from Cloud identity.
- **CLOUD-FEATURE-PARITY-001:** Support all OpenConnect capabilities through the site and Cloud gateway, including those exercised by the Todo sample, with corresponding end-to-end verification. Preserve application-owned execution, authorization, privacy enforcement, and capability-specific admission boundaries.

## Remaining platform requirements

All remaining requirements in the linked cloud requirements specification remain in scope, including membership administration/RBAC management, plan grant administration, gateway integration of issued credentials, admission and private worker routing, signed sanitized evidence, atomic quotas and metering, worker deployment management, speech and translation, credential onboarding UX, CI, deployment, and all 25 acceptance cases. Catalog, selection, or credential authentication alone does not authorize execution or prove production acceptance.
