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
- [x] Record membership-change authorization denials and concurrency conflicts in append-only audit records; invitation and validation failure coverage remains outstanding.
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
- [x] Add an audited tenant inference model allowlist with revision-checked administration and workspace controls; connect it to fenced inference admission before enabling that route.
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
- [x] Add a bounded private classification execution adapter with pinned model and label validation; public routing is opt-in and remains subject to production release gates.
- [x] Fail closed when classification model assets exist but the real inference runtime is absent; do not advertise or return heuristic output as real inference.
- [x] Add a pinned, offline operator-supplied ONNX classification adapter and reject label-set mismatches before quota reservation; production model selection and real-model acceptance remain outstanding.
- [x] Bind the worker bundle digest to gateway readiness, deployment matching and claim creation; mixed-version deployment isolation and real-model acceptance remain outstanding.
- [x] Exercise the Cloud classification gateway, private control plane, PostgreSQL accounting and actual ONNX worker together using a synthetic local model; production model and deployed acceptance remain outstanding.
- [x] Wire the pinned ONNX bundle and separate worker-side Cloud credentials into the standalone classification Compose configuration; deployment-managed identity remains outstanding.
- [x] Provide an opt-in mutual-TLS classification proxy with a dedicated gateway trust bundle and fixed routes; certificate provisioning, private-network isolation and production deployment verification remain outstanding.
- [x] Add claimed Cloud worker dispatch using the exact fingerprinted payload and per-request key; verify mutual TLS and worker response validation against a simulated worker. The public path now composes admission and settlement when explicitly enabled.
- [x] Add a private classification execution sequence for reservation, payload-bound claim, dispatch and successful settlement; verify control-plane and PostgreSQL integration with a simulated worker. Public admission is now connected behind explicit configuration.
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
- [x] Add a bounded classification-worker mutual-TLS consumption client with no retries after ambiguous outcomes; verify it against the control plane and disposable PostgreSQL. Worker execution remains disconnected pending payload binding.
- [x] Bind a claim to a per-request keyed worker-payload fingerprint and require a matching worker proof on consumption; verify mismatched payloads and legacy unbound claims fail closed. Gateway dispatch remains disconnected.
- [x] Expose paginated tenant quota bucket reads to owners, administrators, and billing readers, with issuer-bound membership and PostgreSQL isolation checks.
- [x] Show paginated quota bucket counters in the authenticated organization workspace, with explicit empty and inaccessible states.
- [x] Add audited, tenant-scoped finalized usage records with UTC filters and keyset pagination in the API and organization workspace; aggregates, billing and browser acceptance remain outstanding.
- [x] Add bounded, audited usage CSV export with role checks and spreadsheet-formula protection; production browser acceptance remains outstanding.
- [x] Add a bounded, audited UTC daily usage breakdown by application, capability and unit; billing and production browser acceptance remain outstanding.
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

- [x] Add an opt-in Cloud classification worker route that validates payload bytes, consumes a worker-bound claim, and executes only after success; reject legacy execution in Cloud mode. Verify with a fake engine over mutual TLS and PostgreSQL.

- [x] Connect the opt-in classification HTTP path through authorization, deployment readiness, privacy admission, reservation, claimed dispatch and settlement; verify with PostgreSQL and a simulated worker. Production readiness, active-call revocation, recovery and real-model acceptance remain outstanding.
- [x] Add classification-specific readiness across the gateway, control plane and deployment-bound worker identity; production release gates and real-model verification remain outstanding.
- [x] Enforce classification deployment concurrency across gateway instances using a database guard; verify concurrent current and older writers, stale snapshots and slot release. Recovery for abandoned holds remains outstanding.
- [x] Add opt-in recovery of expired, unclaimed classification reservations with a claim-insertion database fence, grace period and bounded concurrent batches; claimed or ambiguous executions remain held.
- [x] Add separately opt-in recovery of expired unconsumed classification claims with append-only abandonment evidence and database fencing against late consumption and completion; consumed or ambiguous work remains held and mixed-version rollout requires verification.
- [x] Verify the recovery migration with existing claims and a concurrent pre-upgrade consumption writer on a separate disposable database; production rollout and lock duration remain outstanding.
- [x] Revalidate authorization during classification and abort the gateway worker request on revocation or control-plane failure; verify active revocation with PostgreSQL and a simulated worker. Worker termination and recovery remain outstanding.
- [x] Revalidate deployment and region policy during classification execution and abort the gateway worker request when the match is revoked; worker termination and held-claim reconciliation remain outstanding.


- [x] Add operator-only entitlement grant and revocation API with audited revision-fenced PostgreSQL writes; subscription billing integration remains a separate requirement.

- [x] Provide an operator entitlement workspace with explicit target, grant state, expiration and stale-write feedback.

- [x] Fence classification worker consumption against credential, entitlement, selection and region changes committed after claim issuance; deny legacy claims without a credential binding. Coordinated mixed-version rollout and cancellation after consumption remain outstanding.

- [x] Bind classification consumption to immutable claim-time deployment identity and reject changed model/image metadata or missing evidence; production attestation and coordinated rollout remain outstanding.

- [x] Exercise the Google OIDC authorization-code callback with a simulated signed provider, PKCE, issuer, audience and nonce checks; real Google provider verification remains outstanding.

- [x] Return a generic actionable sign-in failure for OIDC callback errors without exposing provider diagnostics; verify with simulated Google callback failures.

- [x] Require an allowed model and persist its identity in internal inference claims under a policy lock; worker payload binding, token accounting and gateway routing remain outstanding.

- [x] Bind internal inference claims to matching model deployment, allowed region, token-unit reservation, payload fingerprint and concurrency capacity; provider verification, worker enforcement, actual token settlement and public routing remain outstanding.

- [x] Add a private deployment-certificate-bound inference consumption endpoint with locked policy rechecks and single-use persistence; worker integration and token settlement remain outstanding.

- [x] Add idempotent worker-certificate-bound inference token settlement with separate input/output observations, reservation reconciliation and database fencing; trustworthy provider metering and hard pre-dispatch token ceilings remain outstanding.

- [x] Expose inference fingerprint-key lookup, token reservation and claim over pinned mutual TLS and provide Node client methods; actual provider token ceilings and public dispatch remain outstanding.

- [x] Add private inference message privacy inspection with durable metadata-only observations and a Node client; public routing, output inspection and dashboard integration remain outstanding.

- [x] Require latest matching inference privacy evidence at claim creation and worker consumption, ordered against evidence writers; exact inspected-message to worker-payload binding remains outstanding.

- [x] Compose inference privacy, local token-quote contract, reservation and payload-bound claim from one gateway request snapshot; production tokenizer, worker dispatch and public routing remain outstanding.

- [x] Return authoritative model and token reservation to the worker only on first inference consumption; worker-side enforcement remains outstanding.

- [x] Add opt-in inference worker claim consumption and authoritative token-budget enforcement around an injected local runtime; verified production runtime, real-model acceptance and streaming remain outstanding.

- [x] Bind inference worker consumption to deployment model revision and digest as well as model ID; real artifact verification remains outstanding.

- [x] Add a digest-checked offline llama.cpp inference adapter and smoke-test bounded token generation against an existing local GGUF; production runtime wiring, tokenizer parity and streaming remain outstanding.

- [x] Load and close the approved local inference runtime at worker startup with a pinned optional runtime dependency; gateway tokenizer parity and public execution remain outstanding.

- [x] Add a private mTLS inference dispatcher for claimed worker payloads and bounded responses; gateway egress inspection and public routing remain outstanding.

- [x] Implement a gateway-local, digest-bound token quote process using the worker's exact prompt preparation; gateway image packaging and end-to-end route integration remain outstanding.

- [x] Package the local tokenizer dependency and code in the gateway image using an allowlisted root build context; explicit model configuration, route integration and deployment acceptance remain outstanding.

- [x] Add opt-in gateway tokenizer configuration and read-only model access to the all-services Compose file; admission wiring and deployment acceptance remain outstanding.

- [x] Add a separately scoped inference output privacy evaluator; authenticated endpoint, evidence and response-path enforcement remain outstanding.

- [x] Expose private authenticated inference output inspection with append-only evidence bound to a completed, settled claim and its original credential; gateway response-path enforcement remains outstanding.

- [x] Add the gateway client for authenticated inference output inspection and exercise it against the real local endpoint; public response-path integration remains outstanding.

- [x] Compose inference admission, claimed dispatch, access monitoring and output inspection in a fail-closed coordinator; production deployment monitoring and route wiring remain outstanding.

- [x] Add claim-bound inference policy and deployment revalidation with a cancellable gateway access monitor; public route wiring and live execution acceptance remain outstanding.

- [x] Connect the opt-in, non-streaming inference route to authenticated readiness, bounded request admission, claimed execution and output inspection; real end-to-end acceptance and streaming remain outstanding.

- [x] Support a bounded mounted secret file for private inference worker authentication; mutual-TLS worker deployment and full-system validation remain outstanding.

- [x] Define an opt-in mutual-TLS Cloud inference worker in the root Compose stack with a read-only model and secret mounts; real gateway-to-worker acceptance remains outstanding.

- [x] Include enabled inference deployments in the private worker readiness check and verify the disabled case against PostgreSQL.

- [x] Exercise a non-streaming inference request through the local gateway, control plane, PostgreSQL and a real provisioned model worker; production deployment, streaming and the broader Cloud acceptance suite remain outstanding.

- [x] Define the Cloud LiveUI planning envelope and validate worker proposals against the exact target-action allowlist, with shared worker and gateway fixtures; protected execution remains outstanding.

- [x] Add independent, fingerprint-bound LiveUI input privacy inspection and append-only evidence, with mutual-TLS and PostgreSQL verification; claim enforcement and routing remain outstanding.

- [x] Add and verify the gateway's private LiveUI privacy client; connect it to reservation and claim admission before enabling dispatch.

- [x] Add fixed one-plan LiveUI quota reservations and fingerprint-key lookup; token accounting, claimed execution and public routing remain outstanding.

- [x] Bind LiveUI claims to allowed privacy evidence, approved model/deployment and plan quota with database-enforced capacity; worker consumption and output enforcement remain outstanding.

- [x] Add deployment-certificate-bound single-use LiveUI claim consumption with current policy and payload revalidation; worker integration and settlement remain outstanding.

- [x] Add transactional, idempotent one-plan settlement fenced by worker consumption; token accounting, output inspection and worker integration remain outstanding.

- [x] Add opt-in LiveUI worker claim consumption, model identity checks, allowlisted plan validation and settlement with no automatic Cloud model downloads; gateway integration and real-system acceptance remain outstanding.

- [x] Add authenticated LiveUI output-field inspection and settled-claim-bound append-only evidence; gateway response-path enforcement remains outstanding.

- [x] Add claim-specific LiveUI access revalidation and share the privacy admission rule across claims, consumption and access checks; gateway monitoring remains outstanding.

- [x] Add the gateway's LiveUI quota, claim, access and output privacy control-plane client; protected dispatch and public route remain outstanding.

- [x] Add mutually authenticated LiveUI worker readiness and claimed dispatch with allowlist validation; admission composition and public route remain outstanding.

- [x] Compose LiveUI input inspection, plan reservation and payload-bound claim before worker dispatch; public execution remains outstanding.

- [x] Gate settled LiveUI proposals on output privacy and ongoing claim access; public routing and end-to-end validation remain outstanding.

- [x] Connect opt-in LiveUI routing, readiness, admission and output enforcement; real-system validation and Compose configuration remain outstanding.

- [x] Add an isolated Cloud LiveUI worker and gateway configuration to root Compose; real-system validation remains outstanding.

- [x] Verify deterministic LiveUI request, settlement, privacy evidence and replay through a real worker and disposable PostgreSQL. Model-generated planning through the host is covered with a controlled page/session fixture.

- [x] Publish the versioned LiveUI result schema with valid and invalid fixtures; request-specific host authorization remains required.

- [x] Add a session-bound application-host privacy screen for Cloud LiveUI fields. The host-authorized provider and live host-to-Cloud flow remain outstanding.

- [x] Add an opt-in host Cloud LiveUI provider, server-only HTTPS transport and required host authorization contract with privacy and proposal validation. Actual host wiring and host-to-Cloud acceptance remain outstanding.

- [x] Share and test the current-page inference capability mapping for host-side Cloud authorization; the concrete host authorizer remains outstanding.

- [x] Add the Blazor Cloud host authorization adapter with current-page and session revalidation. A concrete application session resolver and real host-to-Cloud validation remain outstanding.

- [x] Add and test same-host development session ownership resolution for Cloud LiveUI; application wiring and real host-to-Cloud acceptance remain outstanding.

- [x] Exercise the host Cloud provider and privacy firewall through the real gateway, worker and PostgreSQL with a controlled page/session fixture; browser and production acceptance remain outstanding.

- [x] Verify a Qwen-generated allowed LiveUI action through the gateway and private worker, with input/output evidence and plan settlement in disposable PostgreSQL.

- [x] Exercise model-generated LiveUI planning through the .NET host provider, privacy gate, Cloud gateway and private worker; real browser action and receipt verification remain outstanding.

- [x] Add a Cloud LiveUI browser action and receipt acceptance script using the existing controlled conversational fixture; full environment orchestration and production acceptance remain separate.

- [x] Wire Cloud LiveUI into the same-host development sample with server-side configuration and session ownership validation; production identity and deployment are separate.

- [x] Exercise one same-host browser action and verified receipt through Cloud LiveUI, a real model worker and disposable PostgreSQL, with conversational orchestration supplied by a fixture.

- [x] Add bounded native LiveUI token usage capture and verify it against the provisioned model; atomic token reservations and reconciliation remain outstanding.

- [x] Add an additive LiveUI token-reservation schema and a PostgreSQL dual plan/token reservation store with contention, replay, scope and legacy-upgrade tests. Gateway routing, worker settlement and mixed-version enforcement remain outstanding.

- [x] Add atomic completed-plan and token settlement with claim-bound append-only usage and a mixed-version plan-finalization fence; failed/unknown usage and execution-path activation remain outstanding.

- [x] Fence dual-reservation consumption against older workers and unpaired or oversized metered authorizations, with PostgreSQL and legacy-upgrade checks. The worker endpoint remains outstanding.

- [x] Add and verify the private metered LiveUI consumption service and mutual-TLS endpoint, including legacy-worker rejection and concurrent replay. Production registration and worker integration remain outstanding.

- [x] Add and test the private mutual-TLS dual-settlement endpoint with malformed, overage, concurrent and conflicting reports; worker use and production registration remain outstanding.

- [x] Add and test the private dual-reservation endpoint with the existing machine-authorization boundary; production registration and gateway selection remain outstanding.

- [x] Add opt-in metered worker execution, mutual-TLS client calls and fail-closed usage validation. Gateway activation and failed/unknown usage reconciliation remain outstanding.
