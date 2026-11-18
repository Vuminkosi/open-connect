# Gate 9 Draft Reconciliation Report (DRAFT only, not final publish)

**DRAFT — not final. Publish + live consumer pending. Gate 7 HARD_BLOCKED.**

- Task: OC-G9-DRAFT — Gate 9 draft reconciliation report + matrices (DRAFT only, not final publish).
- Audience: product owner / release gate reviewers + engineering.
- Source baseline (canonical repo `C:\DevOps\open-connect`, active branch `master`): HEAD `b518877f4d2cb9d0c18528c7992e46f08842d8a1`, revalidated with `git rev-parse HEAD` on 2026-11-10. Matches the assigned baseline; no divergence.
- Pins confirmed by inspection: SDK `10.0.403` (`global.json`); MAF / Harness `1.27.0` (`Directory.Packages.props`: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Harness`, `Microsoft.Agents.AI.Workflows` `1.27.0`); EF Core per-TFM `8.0.11` / `9.0.22` / `10.0.14` (`Directory.Packages.props`); library TFMs `net8.0;net9.0;net10.0` (`build/supported-frameworks.props`).
- Cited commits: `103e29b` (TFM baseline NU1010 pin + PublicAPI sync + Harness test ref), `f93f6c3` (SourceLink symbols, exclude samples G5), `b518877` (`release.yml` OIDC trusted-publishing, dry-run by default). Commit messages confirmed via `git show --oneline -s`; full diffs not re-inspected in this pass.
- Method: read-only inspection only. No tests, builds, or E2E were re-executed. Supplied counts (TFM test totals, worker T0/T1 tallies, E2E-01..12 results, G5 19+19) are recorded as **DRAFT / NOT VERIFIED** unless a log or commit pointer was confirmed by inspection. This report does NOT present fake-model CI as real-model evidence.
- Approval-gate statement: **final publish and the live consumer run are pending. Gate 7 (maintainer approval via the `production` environment) is HARD_BLOCKED. No `dotnet nuget push` has executed from this workflow state, no package is published, and no live consumer evidence exists.** Nothing in this draft constitutes a release decision.

## Scope map (acceptance criteria → evidence)

| # | Acceptance criterion | Evidence location | Claim status |
|---|---|---|---|
| 1 | Gate matrix G1–G10 with per-gate status + pointer | `matrices/gate-matrix.md` | DRAFT — mixed INSPECTED / NOT VERIFIED |
| 2 | TFM matrix (Abstractions/Core/Agents/Tools/EFCore counts ×3 TFMs, slnx 0W/0E, locked 35/35) | `matrices/tfm-matrix.md` | DRAFT — counts NOT VERIFIED, commands/config INSPECTED |
| 3 | Worker T0 11/11 + T1 11/11 + T2/T3 NOT RUN + T4 BLOCKED | `matrices/worker-t0-t4-matrix.md` | DRAFT — counts NOT VERIFIED, T2/T3/T4 status per assignment |
| 4 | App E2E-01..12 PASS + E2E-13/14 NOT VERIFIED + packed smoke PASS | `matrices/app-e2e-matrix.md` | DRAFT — E2E-01..12 and smoke NOT VERIFIED; E2E-13/14 NOT VERIFIED grounded in workflow comments |
| 5 | G5 19+19 + SBOM + SourceLink + IsPackable | Section 5 below | DRAFT — 19+19 NOT VERIFIED; SourceLink/IsPackable/sample-exclusion INSPECTED; SBOM script absent INSPECTED |
| 6 | G6 release.yml committed dry-run-only, Gate 7 HARD_BLOCKED, no publish, no live consumer | Section 6 below | INSPECTED — workflow file confirms dry-run-only + approval gate |

## Detailed matrices

- [Gate matrix G1–G10](matrices/gate-matrix.md)
- [TFM matrix](matrices/tfm-matrix.md)
- [Worker T0–T4 matrix](matrices/worker-t0-t4-matrix.md)
- [App E2E matrix](matrices/app-e2e-matrix.md)

Each matrix uses the columns: Requirement/Acceptance | Test ID | Commit SHA | Command | SDK/Runtime/TFM + MAF | Model/Worker/DB | Result | Evidence pointer | Notes. Every claimed PASS traces to a log or commit; otherwise the cell reads BLOCKED or NOT VERIFIED.

## 5. G5 packaging evidence (draft)

- **19+19 claim**: DRAFT / NOT VERIFIED. The "19+19" packaging check tally was supplied with the assignment; no log or commit pointer confirming that exact tally was found by inspection in this pass. Do not treat it as verified PASS.
- **SourceLink**: INSPECTED. `Directory.Build.props` sets `IncludeSymbols`, `SymbolPackageFormat snupkg`, `ContinuousIntegrationBuild` (when `CI=true`), `Deterministic`, `EmbedUntrackedSources`, `PublishRepositoryUrl`, and references `Microsoft.SourceLink.GitHub` with `PrivateAssets=all` (build-time only, never ships). Pin `10.0.403` (SDK-aligned) in `Directory.Packages.props` with a comment recording the GHSA-23fw / NU1902 rationale; `NU1902` retained in `WarningsNotAsErrors` for build-time-only advisory noise only.
- **IsPackable / sample exclusion**: INSPECTED. `IsPackable=false` confirmed by repository search on all inspected sample projects (`Minimal`, `InMemory`, `Sqlite`, `PostgreSql`, `SqlServer`, `Blazor`, `Navigator`, `ArchitectureAtlas`, `McpMicroservices ServiceA/ServiceB`, `_shared`) and on test projects (including multi-targeted `net8.0;net9.0;net10.0` test projects). Only shippable `src/` libraries pack.
- **SBOM**: NOT VERIFIED as produced. `release.yml` runs `scripts/generate-sbom.py` only if present; inspection shows `scripts/` contains only `verify.ps1`, `verify.sh`, `validate-contracts.py` — the SBOM script is absent, so the workflow takes the informational skip path ("SBOM script … not present; skipping"). No SBOM artifact pointer exists.
- **NuGet dependency-group audit**: NOT VERIFIED as executed. Same existence guard applies to `scripts/verify-nuget-dependency-groups.py` (absent from `scripts/`); the CI equivalent `build/scripts/verify-nuget-dependency-groups.py` is referenced by `cross-framework-ci.yml` but its execution log was not inspected in this pass.

## 6. G6 release / Gate 7 approval gate (inspected)

`​.github/workflows/release.yml` (committed at `b518877`) is **dry-run-only by default**:

- `workflow_dispatch` input `publish` defaults to `false`; tag pushes (`v*`) always take the dry-run path (pack, verify, attest, list — never push).
- The `Push to nuget.org` step runs only `if: inputs.publish == true`, which additionally requires the `production` environment (required reviewers configured in the GitHub UI, which repo code cannot create) and a nuget.org trusted-publishing policy for owner/repo/workflow/environment/package-pattern `OpenConnect.*`.
- Auth model: GitHub OIDC → short-lived nuget.org API key via `NuGet/login`; no long-lived keys in code. Only the secret *name* `NUGET_USER` appears; no secret values are present or recorded here.
- Provenance: `actions/attest-build-provenance` over `artifacts/packages/*` on every run, including dry runs.

**BLOCKED list (explicit):**

- Gate 7 maintainer approval: HARD_BLOCKED — no approval ref exists.
- `inputs.publish == true` armed dispatch: NOT EXECUTED — no workflow run log inspected or claimed.
- `dotnet nuget push` / live publish: NOT EXECUTED — no package on nuget.org is claimed.
- Live consumer install/compile/run verification: NOT VERIFIED — no consumer evidence exists.
- SBOM artifact and NuGet dependency-group audit output: NOT VERIFIED (scripts absent / logs not inspected).

## Cross-cutting notes

- Do NOT use fake-model CI output as real-model evidence. Where a model, worker, or database is relevant (worker tiers, E2E persistence legs), the matrices record the required fixture explicitly and mark real-model/provider legs NOT VERIFIED unless a provider-backed log pointer exists.
- G1–G6 are INFORMATIONAL dependencies only for this draft; their handoffs were not re-executed and are cited at the claim level supplied, qualified per-cell.
- Verification does not prove canonical commit; commit does not prove release. This DRAFT proves neither publish nor release readiness.
- No secrets, tokens, PII, connection strings, or machine-specific paths are included. `infra/.env` was never read.

## Factual gaps / unverified list

1. TFM per-project counts (56/45/596+1/20/105 ×3 TFMs), `slnx` 0W/0E, locked-mode 35/35 — supplied counts, no inspected log. NOT VERIFIED.
2. Worker T0/T1 11/11 tallies and per-test IDs — no worker-tier test mapping or log inspected (`workers/` directories exist; mapping unconfirmed). NOT VERIFIED.
3. App E2E-01..12 per-test PASS pointers and packed-smoke log — no run logs inspected. NOT VERIFIED (workflow case scripts `Run-*.ps1` exist; results unconfirmed).
4. G5 19+19 tally — no log pointer. NOT VERIFIED.
5. Any CI run URLs, signing/attestation artifacts, product-owner approval refs — absent. NOT VERIFIED / BLOCKED.
6. Full diffs of `103e29b` / `f93f6c3` beyond subject lines — not re-inspected; cited at subject level only.

## Independent review required

A reviewer other than this draft's author must confirm, before any Gate 9 decision: per-cell evidence pointers resolve to real logs/commits/artifacts; no PASS lacks a trace; TFM/worker/E2E counts are either verified against fresh executed logs or remain NOT VERIFIED; Gate 7 remains HARD_BLOCKED with no publish or live-consumer claim.

## Commands and checks run (read-only)

| Command / check | Result | Evidence |
|---|---|---|
| `git rev-parse HEAD` | PASS — `b518877f4d2cb9d0c18528c7992e46f08842d8a1`, matches baseline | shell output, 2026-11-10 |
| `git show --oneline -s 103e29be / f93f6c / b518877` | PASS — subjects match assigned descriptions | shell output, 2026-11-10 |
| `git status --short --branch` | INSPECTED — `master`, ahead 3 (`ahead 3` vs origin), working tree state to be confirmed at commit time by committer lane | shell output, 2026-11-10 |
| Read `global.json`, `Directory.Packages.props`, `Directory.Build.props`, `build/supported-frameworks.props`, `OpenConnect.slnx` | PASS — pins/TFMs/lockfiles as cited above | file reads, 2026-11-10 |
| Read `.github/workflows/release.yml`, `ci.yml`, `cross-framework-ci.yml`, `cross-framework-integration.yml` | PASS — dry-run-only gate, E2E matrix + exit-3 NOT VERIFIED paths, TFM probe design as cited | file reads, 2026-11-10 |
| Search `IsPackable` across repo | PASS — samples + tests excluded from packing | grep output, 2026-11-10 |
| List `scripts/` | PASS — no `generate-sbom.py`, no `verify-nuget-dependency-groups.py` | directory read, 2026-11-10 |
| List `workers/`, `samples/EndToEnd/cases/` | INSPECTED — directories/scripts exist; result mapping not confirmed | directory reads, 2026-11-10 |
| Relative link check of the four matrix links | NOT VERIFIED by tool — links follow the `matrices/<file>.md` convention and must be confirmed at review/integration | manual convention check |
| `infra/.env` | NOT READ — prohibited path, never accessed | n/a |

## Changed paths (new files, uncommitted)

- `docs/evidence/g9-reconciliation-draft.md` (this file)
- `docs/evidence/matrices/gate-matrix.md`
- `docs/evidence/matrices/tfm-matrix.md`
- `docs/evidence/matrices/worker-t0-t4-matrix.md`
- `docs/evidence/matrices/app-e2e-matrix.md`

No staging, commit, push, or branch operation was performed. `docs/evidence/` did not previously exist; parent directories are created by these new files.
