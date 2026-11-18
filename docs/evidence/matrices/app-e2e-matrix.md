# App E2E Matrix (DRAFT)

**DRAFT — not final. Publish + live consumer pending. Gate 7 HARD_BLOCKED.**

Parent: [G9 reconciliation draft](../g9-reconciliation-draft.md). E2E-01..12 PASS and packed-smoke PASS were supplied; no run logs were inspected, so they stay NOT VERIFIED. E2E-13/14 NOT VERIFIED is grounded in inspected workflow comments. Case scripts observed 2026-11-10 in `samples/EndToEnd/cases/`: `Run-Minimal.ps1`, `Run-Sqlite.ps1`, `Run-InMemory.ps1`, `Run-Blazor.ps1`, `Run-Navigator.ps1`, `Run-PostgreSql.ps1`, `Run-SqlServer.ps1`. The CI matrix (`ci.yml`, `e2e-per-sample`, windows-latest only, `continue-on-error: true`, informational, never gates) covers cases Minimal, Sqlite, InMemory, Blazor, Navigator, PostgreSql, SqlServer. Per-test ID-to-case mapping was not confirmed by inspection — the mapping below is provisional and must be confirmed by the owning lane.

| Requirement / Acceptance | Test ID | Commit SHA | Command | SDK / Runtime / TFM + MAF | Model / Worker / DB | Result | Evidence pointer | Notes |
|---|---|---|---|---|---|---|---|---|
| App E2E-01 (provisional: Minimal) | E2E-01 (mapping unconfirmed) | n/a | `samples/EndToEnd/cases/Run-Minimal.ps1 -RepoRoot . -DbFile <db> -LogDir <logs>` (unexecuted) | SDK 9.0.320 per `ci.yml` `e2e-per-sample` leg | File DB / no container | NOT VERIFIED | Script exists; no run log inspected | Supplied PASS only; confirm ID↔case mapping |
| App E2E-02 (provisional: Sqlite) | E2E-02 (mapping unconfirmed) | n/a | `Run-Sqlite.ps1 …` (unexecuted) | SDK 9.0.320 | SQLite file-based | NOT VERIFIED | Script exists; no run log inspected | Supplied PASS only |
| App E2E-03 (provisional: InMemory) | E2E-03 (mapping unconfirmed) | n/a | `Run-InMemory.ps1 …` (unexecuted) | SDK 9.0.320 | InMemory (sample-scope only) | NOT VERIFIED | Script exists; no run log inspected | Supplied PASS only |
| App E2E-04 (provisional: Blazor) | E2E-04 (mapping unconfirmed) | n/a | `Run-Blazor.ps1 …` (unexecuted) | SDK 9.0.320 | App host / browser-less leg | NOT VERIFIED | Script exists; no run log inspected | Supplied PASS only |
| App E2E-05 (provisional: Navigator) | E2E-05 (mapping unconfirmed) | n/a | `Run-Navigator.ps1 …` (unexecuted) | SDK 9.0.320 | Navigator sample | NOT VERIFIED | Script exists; no run log inspected | Supplied PASS only |
| App E2E-06..12 (further per-case legs) | E2E-06 … E2E-12 (mapping unconfirmed) | n/a | Per-case `Run-*.ps1` (unexecuted) | SDK 9.0.320 | Per-case fixture | NOT VERIFIED | No run logs inspected | Supplied PASS only; owning lane must attach one log pointer per test |
| App E2E-13 (PostgreSql — container-gated) | E2E-13 | n/a | `Run-PostgreSql.ps1 …` (unexecuted in CI by design) | SDK 9.0.320 | PostgreSql fixture intentionally absent in CI | NOT VERIFIED | `ci.yml` lines 105–114 (PG fixture env must be absent; guard fails the step if leaked) + lines 82–86 (exit-3 NOT VERIFIED path) — inspected 2026-11-10 | Deterministic NOT VERIFIED by workflow design (D-03/D-04/D-05); no image pulls, no live containers, no test secrets |
| App E2E-14 (SqlServer — not invoked) | E2E-14 | n/a | Not invoked (unexecuted by design) | SDK 9.0.320 | SqlServer fixture not provisioned | NOT VERIFIED | `ci.yml` lines 82–86 (exits 3 by design, no invocation, no pull) — inspected 2026-11-10 | Must stay NOT VERIFIED; do not claim coverage |
| Packed smoke (pack + install/run packed sample) | Packed smoke (ID unconfirmed) | n/a | Pack + consumer-style smoke (unexecuted; command unconfirmed) | SDK/TFM unconfirmed | Local feed only (no publish) | NOT VERIFIED | No log inspected | Supplied PASS only; needs log pointer + package file list |

Fake-model vs real-model rule: sample E2E legs use local fixtures/fake endpoints where applicable; none of these rows may be cited as real-model or provider-backed evidence. Provider legs (E2E-13/14) stay NOT VERIFIED until real container-backed logs exist.

Gap summary: E2E-01..12 ID↔case mapping, all run logs, and the packed-smoke log are missing. E2E-13/14 NOT VERIFIED is the only workflow-grounded status in this matrix.
