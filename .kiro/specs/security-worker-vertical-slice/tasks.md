# Security worker vertical slice tasks

- [x] **SEC-SLICE-TASK-001 Requirements** — Confirmed the rules-only contract and negative cases against `workers-requirements.md` and `workers-samples-e2e-spec.md`.
- [x] **SEC-SLICE-TASK-002 Design** — Reviewed the private worker boundary, fail-closed behavior and evidence redaction.
- [x] **SEC-SLICE-TASK-003 Implementation** — Added `.gitattributes` LF enforcement and normalized the shared/security shell harness in the committed Linux checkout.
- [x] **SEC-SLICE-TASK-004 Tests** — Ran T0 and T1 from a clean Linux checkout with SQLite provider selection and no model provisioning. Both passed.
- [x] **SEC-SLICE-TASK-005 Tests** — Ran an isolated Docker Compose T2/T3 check with `PORT_SECURITY=18080`: readiness 200, authenticated scan 200, unauthenticated scan 401, disabled detector 503. The isolated project was removed with `down --remove-orphans`; the named cache volume was preserved.
- [x] **SEC-SLICE-TASK-006 Evidence** — Evidence is recorded in `docs/release/production-readiness.md`; the image was built locally as `vuminkosimatibe/openconnect-worker-security:e2e-local` and was not pushed.
- [x] **SEC-SLICE-TASK-007 Review** — Reviewed the diff for privacy, tenant boundary, secret handling and destructive cleanup. No production credentials or registry operations were used.

## Evidence ledger

- Source revision: `211ec35`.
- Linux checkout verification: `bash samples/workers/shared/scripts/run-one.sh security --tier static --provider sqlite` and `... --tier contract --provider sqlite` — PASS.
- Standard-library acceptance: `python samples/workers/security/tests/test_e2e_acceptance.py -q` — PASS, 8 tests.
- Docker verification: `docker compose -p oc-audit-security -f samples/workers/security/compose.e2e.yaml up -d --build`, bounded health and HTTP assertions — PASS; cleanup used `down --remove-orphans`.
- Not claimed: model-live, offline-revision with approved model manifests, Docker Hub publishing, production authentication, production migrations, or external registry release.
