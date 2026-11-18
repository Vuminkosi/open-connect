# G9 Final Reconciliation Report — 2026-11-10

**Action taken:** created new file `docs/evidence/g9-final.md` (no prior file existed; no append). Parent draft: [G9 reconciliation draft](g9-reconciliation-draft.md) (commit `8c006bb`, DRAFT, Gate 7 HARD_BLOCKED — superseded in content but preserved as history; this file does not rewrite it).

- Task: REG-G9-FINAL — G9-final reconciliation doc.
- Audience: orchestrator/master + reviewers.
- Source baseline: canonical repo `C:\DevOps\open-connect`, active branch `master`, HEAD `a2e4383` — **revalidated** via `git rev-parse HEAD` on 2026-11-10 (matches assigned baseline). Commit subjects below revalidated via `git show --oneline -s`; full diffs/logs were **not** re-executed in this pass — counts and commands are cited at the level supplied by the assignment and prior evidence commits.
- Method: read-only inspection only. No builds, tests, E2E, pushes, or publishes executed. No `infra/.env` or secrets read.
- Evidence tiers used: PASS (executed + evidenced) / FAIL / NOT VERIFIED / BLOCKED / NOT RUN. A PASS in this file means the cited commit exists with the described subject **and** the assignment supplies an executed command + counts; it does not mean this author re-ran the check. No aggregate-to-criterion promotion: a green subset never flips a parent gate.

## 1. Per-gate table

| Gate / Claim | Commit | Command + exit (as supplied / inspected) | Counts + exact scope | Result |
|---|---|---|---|---|
| REG-S01 matrix | NOT VERIFIED (no SHA supplied) | Command unconfirmed | No counts supplied; scope unknown | NOT VERIFIED — no pointer found in this pass |
| REG-S02 suite | NOT VERIFIED (no SHA supplied) | Command unconfirmed | **105/105** supplied; exact scope (project/TFM/fixture) unconfirmed | NOT VERIFIED — tally supplied only, no log pointer inspected |
| Registry parity (capabilities locked to endpoint parity) | `ef48dd0` (subject confirmed) | Test command per owning lane; not re-executed here | Scope: registry↔endpoint parity lock; counts per owning lane evidence | COMMITTED (SHA verified) / test outcome NOT VERIFIED by this pass |
| Adapters probe (Durable / A2A fail-closed) | No SHA supplied | Probe command unconfirmed | No counts; missing members per assignment | BLOCKED — missing members; fail-closed claimed, not verified here |
| S08 unit leg | NOT VERIFIED (no SHA supplied) | `dotnet test` leg unconfirmed | **91/91 unit** supplied; scope: S08 unit suite only, not full matrix | NOT VERIFIED — tally supplied only |
| S08 live NATS leg | NOT VERIFIED (no SHA supplied) | Live NATS command unconfirmed; broker digest not found in this pass | **1/1** supplied; scope: single live-NATS case only | NOT VERIFIED — no broker digest pointer inspected |
| S08 DI-bundle rejections (ADR-004) | `a2e4383` (HEAD, ADR-004 standing rules; subject confirmed) | n/a (decision record) | Scope: infra no-bundle rules | COMMITTED / INSPECTED at subject level — rejection behavior itself NOT VERIFIED here |
| S08 CI filters (live NATS excluded from required legs) | `413147c` (subject confirmed) | Workflow text per assignment; not re-executed | Scope: CI required-leg filter config | COMMITTED (SHA verified) / run outcome NOT VERIFIED |
| Python T3 greens ×9 (fake/synthetic fixtures) | Owning-lane commits incl. `6465cc2`, `078bb36`, `d29eed7`, `e8f9e9e`, `e959f37`, `c0642ac`, `f79d454`, `841150a` + navigator fix `9b656d4` (subjects confirmed for sampled SHAs) | Compose-env + smoke-route fixes per commit subjects; T3 runtime pending daemon per subjects | Scope: harness/smoke with **FAKE/synthetic fixtures** — proves harness behavior only, never real-model | COMMITTED / FAKE-LABELED — real-model claim explicitly disallowed |
| .NET-host T3: operational-agents fenced lease live | `602deb9` (subject confirmed) | Live lease proof per owning lane; not re-executed here | Scope: operational-agents fenced lease T3 only | COMMITTED (SHA verified) / outcome NOT VERIFIED by this pass |
| .NET-host T3: outbox relay live with fenced lease (PUBACK proof + unit matrix) | `e5d2c5c` (subject confirmed) | Live relay proof per owning lane; not re-executed here | Scope: outbox relay T3 only; unit matrix **1231 passed / 0 failed, build 0W/0E** on `e5d2c5c` per assignment | COMMITTED (SHA verified) / counts NOT VERIFIED by this pass — recorded as supplied |
| Docker images 1.0.3+latest, Hub-verified digests (outbox/security legs) | No digest pointer found in this pass | Build/push commands unconfirmed | Hub digests not inspected | NOT VERIFIED — do not cite digests until owning lane attaches them |
| Full unit matrix on `e5d2c5c` | `e5d2c5c` (subject confirmed) | `dotnet test` full-matrix command per owning lane; not re-executed | **1231 passed / 0 failed; build 0 warnings / 0 errors** — scope: full unit matrix at that commit only, not E2E/T3/T4 | COMMITTED (SHA verified) / counts NOT VERIFIED by this pass — recorded as supplied |
| Sample README (proven T1+T3 harness status) | `01a8dfe` (subject confirmed) | n/a (docs) | Scope: T1+T3 fake-labeled status; T4 BLOCKED recorded in doc | COMMITTED (SHA verified) |
| net10 default / package READMEs / SourceLink / 10.0.403 / repo URLs / tag-versioned workflows (prior arc) | Prior-arc commits per draft (`103e29b`, `f93f6c3`, `b518877` — subjects confirmed in draft pass) | Pack/verify/attest commands per draft §5–§6; not re-executed | Scope: packaging/release config as inspected in draft | See draft — INSPECTED (config) / NOT VERIFIED (runs); no new evidence in this pass |
| T4 real-model | n/a | n/a | n/a | BLOCKED — no approval/model/fixture pointer |
| Durable / A2A | n/a | n/a | n/a | BLOCKED — missing members; fail-closed, no done claim |
| Voice / MAUI | n/a | n/a | n/a | NOT VERIFIED — no evidence inspected |
| NuGet re-publish | n/a (`dotnet nuget push` must NOT run until armed) | n/a | Needs version bump + Gate 7 approval + armed push | NOT RUN |

Honesty notes:

- Every numeric tally above marked "supplied" (105/105, 91/91, 1/1, 1231/0, 9× T3) was **not** re-executed or log-checked in this pass. They are recorded, not verified.
- No digests (Hub image, broker, package) are claimed: none were found by inspection in this pass. Any digest cited elsewhere must be re-attached with its pointer before use.
- Fake/synthetic-fixture T3 greens prove harness behavior only and must never be cited as real-model evidence.
- Stale draft `8c006bb` remains the DRAFT record; this file is the final-structure reconciliation, not a release decision. Gate 7 remains HARD_BLOCKED per draft §6 (no approval ref, no publish, no live consumer).

## 2. Worker T3 matrix

| Worker / host | T3 evidence commit | Fixture label | Result (this pass) |
|---|---|---|---|
| classification (Python) | owning lane (`d29eed7` compose env + smoke route fix; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| inference (Python) | owning lane (`f79d454`; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| transcription (Python) | owning lane (`f79d454`-era batch; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| nllb (Python) | owning lane (`e8f9e9e`; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| mms-tts (Python) | owning lane (`e959f37`; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| kokoro (Python) | owning lane (`c0642ac`, `6465cc2`; subjects confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here |
| realtime-voice (Python) | owning lane (`078bb36`; subject confirmed in log listing) | FAKE / synthetic | COMMITTED fix / runtime NOT VERIFIED here (T3 runtime pending daemon per subject) |
| navigator-playwright (Python) | owning lane (`9b656d4`; subject confirmed in log listing) | Facade-aligned; T3 runtime pending daemon | COMMITTED fix / runtime NOT VERIFIED here |
| 9th Python worker (per assignment tally) | No separate SHA isolated in this pass | FAKE / synthetic (per assignment) | NOT VERIFIED — owning lane must name worker + SHA |
| operational-agents (.NET host) | `602deb9` (subject confirmed) | Live fenced lease (per subject) | COMMITTED / outcome NOT VERIFIED here |
| outbox (.NET host) | `e5d2c5c` (subject confirmed) | Live relay + PUBACK (per subject) | COMMITTED / outcome NOT VERIFIED here |
| T4 real-model (any worker) | n/a | Real model required | BLOCKED |

T3 runtime for Python workers is pending daemon availability per the fix commit subjects; do not promote compose/smoke fixes to T3 PASS.

## 3. Cross-TFM / unit matrix

| Suite / leg | Commit | Counts (supplied, scope-qualified) | Result (this pass) |
|---|---|---|---|
| REG-S02 | n/a | 105/105, scope unconfirmed | NOT VERIFIED |
| S08 unit | n/a | 91/91, S08 suite only | NOT VERIFIED |
| Full unit matrix | `e5d2c5c` | 1231 passed / 0 failed; build 0W/0E at that commit | COMMITTED / counts NOT VERIFIED here |
| TFM per-project legs (Abstractions/Core/Agents/Tools/EFCore × net8.0/9.0/10.0) | Prior arc `103e29b` per draft | Supplied tallies per draft §5/ matrices | See draft — NOT VERIFIED (runs) |
| S08 live NATS 1/1 | n/a | Single live case; broker digest missing | NOT VERIFIED |

## 4. Packaging / release evidence

- Prior-arc packaging/release config (SourceLink, IsPackable/sample exclusion, dry-run-only `release.yml`, OIDC, attestation) stands as inspected in the draft (§5–§6); no new run evidence in this pass.
- Docker `1.0.3`+`latest` Hub digests: NOT VERIFIED (no pointer inspected).
- SBOM artifact / NuGet dependency-group audit output: NOT VERIFIED (scripts absent per draft inspection).
- Gate 7 maintainer approval + live publish + live consumer: HARD_BLOCKED / NOT RUN. No `dotnet nuget push` executed or claimed.

## 5. Honest gaps + resumptions

1. REG-S01 matrix pointer and REG-S02 105/105 log pointer — resumption: owning lane attaches commit SHA + executed command + log.
2. S08 91/91 unit log + 1/1 live-NATS log with broker digest — resumption: attach logs + digests.
3. 9th Python T3 worker identity + SHA — resumption: name worker + commit.
4. Python T3 live-runtime proofs (daemon-gated) — resumption: daemon-available run logs per worker.
5. `602deb9` lease + `e5d2c5c` PUBACK logs — resumption: attach owning-lane logs (SHAs already verified).
6. `e5d2c5c` 1231/0 full-matrix log + 0W/0E build log — resumption: attach logs.
7. Docker Hub digests (outbox/security legs) — resumption: attach `docker images --digests` / Hub pointers.
8. Adapters probe (Durable/A2A) members — resumption: supply members, then re-probe fail-closed.
9. Voice/MAUI, T4 real-model, NuGet re-publish (version + Gate 7 + push) — resumption: separate approvals/runs; do not bundle into this doc.

## 6. Matrices update status

**NONE updated.** Existing files `docs/evidence/matrices/gate-matrix.md`, `tfm-matrix.md`, `worker-t0-t4-matrix.md`, `app-e2e-matrix.md` are DRAFT-baseline records of the `8c006bb` pass; they are stale relative to the newer T3 commits (`602deb9`, `e5d2c5c`, Python fix batch, `413147c`, `01a8dfe`, HEAD `a2e4383`), but this slice does not rewrite them without owning-lane log pointers — updating tallies from supplied counts alone would be PASS inflation. Recommended follow-up: owning lanes refresh each matrix cell-by-cell with log pointers, or the orchestrator charters a matrix-refresh slice.

## 7. Follow-ups (not touched — prohibited paths respected)

- `docs/plans/*`, requirements/spec/implementation/checklist root files: untouched; checklist close-out remains with the orchestrator.
- NuGet re-publish: needs version bump + Gate 7 maintainer approval + armed `publish=true` dispatch + live-consumer verification.
- Independent review of this file before any Gate 9 decision.

## 8. Commands and checks run (this slice, read-only)

| Check | Result | Evidence |
|---|---|---|
| `git rev-parse HEAD` + `git log --oneline -15` | PASS — HEAD `a2e438388dd7378b4d0e7c1184caf7c424a04999`, matches assigned `a2e4383` | shell output, 2026-11-10 |
| `git show --oneline -s 8c006bb ef48dd0 602deb9 e5d2c5c 413147c 01a8dfe` | PASS — all six subjects match assigned descriptions | shell output, 2026-11-10 |
| Read `docs/evidence/g9-reconciliation-draft.md` + 4 matrices | INSPECTED — prior DRAFT/NOT VERIFIED statuses confirmed, reused honestly | file reads, 2026-11-10 |
| Relative link check (`g9-reconciliation-draft.md`, `matrices/*.md`) | INSPECTED — links follow existing convention; full link validation NOT VERIFIED by tool | manual convention check |
| `infra/.env` / secrets | NOT READ — prohibited, never accessed | n/a |
| Builds / tests / pushes / publishes | NOT RUN — documentation-only slice | n/a |

## Changed paths

- `docs/evidence/g9-final.md` (created, uncommitted). No staging, commit, push, or branch operation performed.
