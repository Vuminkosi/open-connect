# Agents acceptance evidence (point-in-time)

Scope: AG-AT-001–040 for the Agents slice ONLY (`src/OpenConnect.Agents` + Abstractions agent enums/DTOs + Core options/validation/binder and their tests). Out of scope: Tools/Sessions/Persistence/Hosting/Transport/Privacy/Observability/Clients/workers/packages/infra/pipelines/live services. Status date: 2026-11-08 (UTC). Baseline: `5739dfa7076b87880d2eb1e3d1f6a43085f4dcc6` (observed HEAD). Method: per-project Release test commands: `dotnet test tests/Unit/OpenConnect.Agents.Tests/OpenConnect.Agents.Tests.csproj -c Release` and `dotnet test tests/Unit/OpenConnect.Core.Tests/OpenConnect.Core.Tests.csproj -c Release`. SDK line: `8.0.427` (recorded by prior map; not re-probed in this refresh). Target: `net8.0`.

## Suite totals (observed this task)

Agents: 345/345 passed, 0 failed, 0 skipped. Core: 35/35 passed, 0 failed, 0 skipped. Compared with the previous map: Agents +2; Core +0.

## Acceptance rows (ID | Spec title (verbatim below) | Verdict | Evidence)

| ID | Spec title | Verdict | Evidence |
| --- | --- | --- | --- |
| AG-AT-001 | Real installed MAF direct agent + tools/session/checkpoint smoke compiles/runs | PARTIAL | Direct/tools smoke, session+history/serialization, isolation, approval surfacing+resume evidence supplied for `MafRealAgentTests`, `MafToolAttachmentTests`, `MafWorkflowSmokeTests`, `MafSessionSerializationTests`, `MafApprovalSmokeTests`, `ChildSessionContinuityTests`, and `HandoffSessionTests`. Task handoff reports no workflow/checkpoint/HITL/conflict types in pinned MAF (compile probes) and no service-managed sessions; these probes were not run again here. |
| AG-AT-002 | Typed enum/config serialization parity and bad value rejection | PASS | `EnumWireParityTests`, `OptionsBindingParityTests`, and `HostStartupValidationTests`; Agents and Core Release suites passed in this task. YAML is BLOCKED per supplied evidence: feed offers `Microsoft.Extensions.Configuration.Yaml` 2.0.0-preview2, not pinned 8.0.0 (NU1102 reported in task handoff). |
| AG-AT-003 | `.AddOpenConnect()` default lead interactive; no unrequested optional packages/workers | PASS | `OptionsTests`, `HostCompositionTests`, and `PackageBoundaryTests`; tests in the Agents and Core suites passed in this task. |
| AG-AT-004 | Template inheritance/variables/revisions and narrowing rules | PASS | `TemplateNarrowingTests`, `TemplateAliasMigrationTests`, `RegistryImmutabilityTests` (Agents suite passed). |
| AG-AT-005 | Malicious agent-runtime.md/path traversal cannot alter security or paths | PASS | `InstructionLoaderAdapterTests`, `TemplateNarrowingTests`, Core `InstructionLoaderTests` (both suites passed). |
| AG-AT-006 | lead depth0 -> child depth1 -> grandchild depth2 permitted | PASS | `AgentCatalogDepthTests`, `DelegationDepthReductionTests`, `DelegationChainTests` (Agents suite passed). |
| AG-AT-007 | depth3, cycles, self-delegation and excessive fan-out rejected before run | PASS | `DelegationGateTests`, reduced-cap tests, and registry immutability tests (Agents suite passed). |
| AG-AT-008 | nested tool/model/time/token budgets enforced, no double counting | PASS | Core `AgentOptionsMappingTests.ToRunBudget_CopiesEveryConfiguredCapAndPreservesRestrictiveValues` and `ToRunBudget_MapsDefaultsToExpectedBudget` verify configured/default option mapping; Agents `OptionsToEnforcementTests.Mapped_total_subagent_limit_is_enforced_by_sequential_runner` verifies mapped `MaxTotalSubagentsPerRun` enforcement (one child completes; second is rejected; ledger total is exhausted). `RunBudgetLedgerTests` (including concurrent case), `ModelBudgetEdgeTests`, and `ExecutionContextBudgetTests` also passed in the Agents suite. `CoverageBackfillTests.SubagentReservation_RejectsQueueAtLimitAndNegativeCountsWithoutDebiting` and `SubagentReservation_RejectsImmediateActiveCountOverConcurrentLimit` verify queue-limit and active-count rejection boundaries without debiting the remaining total-agent budget (Agents Release suite passed). |
| AG-AT-009 | Real MAF sequential/parallel execution and authorized merge | PASS | Runners, `ResultMergerTests`, lead synthesis/merge composition, and `DelegationChainTests` (Agents suite passed). |
| AG-AT-010 | Real MAF handoff multi-turn, actor-specific session consistency | PARTIAL | `ChildSessionContinuityTests` ran in-memory: `Same_key_continues_child_history`, `Different_keys_keep_histories_isolated`, and `Different_children_keep_histories_isolated` establish per-child key/history continuity and isolation; `Null_key_preserves_prompt_only_legacy_behavior` covers legacy no-key behavior. `Whitespace_key_rejected_before_any_trace` and `Exhausted_budget_leaves_no_child_trace` verify rejection ordering before trace creation. Service-managed and cross-restart behavior not proven (no provider/credentials). |
| AG-AT-011 | Child failure/partial result/cancellation propagates with authentic tree | PASS | `ParallelCancellationTests`, `PartialFailureTraceTests`, `RevocationTests`, and denial paths in `ModelBudgetEdgeTests` (Agents suite passed). |
| AG-AT-012 | Interactive identity is current authenticated user and tenant | PARTIAL | `ExecutionContextSnapshotTests` covers context snapshot shape and credential-free guard; host derivation is Hosting-owned. |
| AG-AT-013 | Subagent cannot expand inherited permission; auth revocation denies next effect | PASS | Scope/budget narrowing, grant fields/reason/revision, revocation/expiry/denial tests (Agents suite passed). `ExecutionContextBudgetTests.Request_capped_at_parent_remaining` verifies requested child budget is capped; `Parent_record_immutable_after_child_creation` confirms parent budget state is unchanged; unknown and negative requests are rejected without changing parent state. |
| AG-AT-014 | Operational mode uses workload identity, no ambient user auth | PARTIAL | Operational definitions, boundary predicates, same-adapter proof, and no-ASP.NET-Core-reference guard; supervisor/scheduler are Hosting-owned. |
| AG-AT-015 | Tool registration typed schema, DI and MCP adapters | NOT VERIFIED | Tools-owned; outside this slice. |
| AG-AT-016 | Agent-scoped discovery returns only permitted tools/capabilities | NOT VERIFIED | Tools-owned; Agents-side capability-router allowlist is separately validated. |
| AG-AT-017 | Tool is reauthorized and schema checked before execution | NOT VERIFIED | Tools-owned; outside this slice. |
| AG-AT-018 | Exact-argument approval receipt, expiry, single use and tool revision | NOT VERIFIED | Tools-owned. MAF approval surfacing/resume evidence does not establish policy implementation. |
| AG-AT-019 | Non-idempotent effect uncertain outcome never auto-retried | NOT VERIFIED | Tools-owned; in-slice cancellation propagation is covered under 011. |
| AG-AT-020 | tool.accepted durable ACK before long tool executes | NOT VERIFIED | Tools/Persistence-owned. |
| AG-AT-021 | HTTP task accepted vs tool accepted/started/complete distinct and truthful | NOT VERIFIED | Tools/Hosting-owned. |
| AG-AT-022 | Offline browser/reconnect does not block accepted long tool or duplicate execution | NOT VERIFIED | Tools/Hosting-owned. |
| AG-AT-023 | Measured progress monotonic, coalesced, exact item counts, no fake percent | NOT VERIFIED | Tools-owned. |
| AG-AT-024 | No progress capability => heartbeat only; watchdog liveness/failure correct | NOT VERIFIED | Tools-owned. |
| AG-AT-025 | 100% progress before durable commit stays Finalizing, not completed | NOT VERIFIED | Tools-owned. |
| AG-AT-026 | Cancel request/confirmed/unknown outcome, races and no duplicate remote side effect | NOT VERIFIED | Tools-owned; cancellation propagation is covered under 011. |
| AG-AT-027 | 10-turn conversation persistence/resume after gateway restart, scoped | NOT VERIFIED | Sessions/Persistence-owned; in-memory multi-turn and turn policy do not prove durable restart recovery. |
| AG-AT-028 | MAF session ownership conflicts detected; checkpoint pending HITL restored | PARTIAL | Serialization round-trip supplied as evidence. Task handoff reports no ownership-conflict/checkpoint/HITL types in pinned MAF; durable store/restore are Sessions-owned. Evaluation-only `AgentSessionStore` is unused (MAAI001). |
| AG-AT-029 | Shared context projection excludes secret/tool control messages; narrowed child scope | PASS | `ContextProjectionTests` (quarantine, narrowing, budget, expiry, multi-class, None-mode; Agents suite passed). |
| AG-AT-030 | Parallel agents independent scratchpads and deterministic merge/conflict | PASS | Prompt-only isolation, deterministic merge, quarantine, and composition tests (Agents suite passed). |
| AG-AT-031 | Session memory/compaction provenance preserved, token budget enforced | PASS | `MemoryStoreTests` (write policy, TTL, tombstones, scope, must-retain compaction, budget; Agents suite passed). |
| AG-AT-032 | Cross-session memory consent/delete and Qdrant optionality | NOT VERIFIED | Sessions/Qdrant-owned. |
| AG-AT-033 | Operational schedule/lease/heartbeat/restart with correct workload scopes | NOT VERIFIED | Hosting/Persistence-owned; in-slice definitions and boundary predicates do not verify scheduler operation. |
| AG-AT-034 | Operational -> interactive review; interactive -> operational enqueue authorization | PARTIAL | Transition predicates (including same-mode) supplied as evidence; review/enqueue machinery is Hosting-owned. |
| AG-AT-035 | HTTP/WS typed ACK/progress/replay schema and cursor parity Blazor/TS | NOT VERIFIED | Hosting/Clients-owned. |
| AG-AT-036 | MiniComposer status shows concise ACK/mid-progress with no extra agent turn | NOT VERIFIED | Hosting/Clients-owned. |
| AG-AT-037 | Privacy canary, cross-tenant memory/tool-output and injection negative suite | NOT VERIFIED | Privacy-owned. |
| AG-AT-038 | OTel spans/cost ledger exact billable attempts; no raw content | NOT VERIFIED | Observability/Metering-owned. |
| AG-AT-039 | SQL Server/PG/SQLite outbox fault/concurrency; InMemory non-durable rejected | NOT VERIFIED | Persistence/CI-owned. |
| AG-AT-040 | Clean NuGet/npm package consumers, example compilation, pinned CI and release checks | NOT VERIFIED | Pipeline-owned. |

## Slice commits observed in history

- `5739dfa7076b87880d2eb1e3d1f6a43085f4dcc6` — `feat(agents): configured options to run-budget mapping with enforcement proof (AG-AT-008)`
- `4758adcb91e5d9ac5938519c9fe63b9942c07e1f` — `test(agents): child budget capping and parent immutability (AG-REQ-022)`
- `f7afa529bc731467dd522e1d21145dc01800ed84` — `feat(agents): per-actor child session continuity with trace-safe ordering`

## Refresh log

- 2026-11-08 (UTC), HEAD `f36fc0fc0881ec9b8391933af1a855e2f4eeb5ea`: refreshed suite totals and AG-AT-008 evidence after `97b758da1777def02c92f598a9bdaa9c91aa182e` (`docs(agents): refresh acceptance map to HEAD with verified evidence (AG-AT-001-040)`) and `f36fc0fc0881ec9b8391933af1a855e2f4eeb5ea` (`test(agents): ledger queued/active/negative boundary coverage (AG-REQ-004)`). Both Release suites passed; new boundary evidence is from the two `CoverageBackfillTests.SubagentReservation_*` tests named in AG-AT-008.

## Gaps register (with exact evidence pointers)

- MAF 1.27.0 missing workflow/checkpoint/HITL/session-ownership-conflict types (compile probes reported as CS1061/CS1503 in upstream task handoffs; not rerun here). Evaluation-only `AgentSessionStore` deliberately unused — MAAI001.
- Service-managed sessions: no provider credentials in this environment; none requested (deliberate: secret hygiene and CI suitability).
- YAML configuration: NU1102 reported in task handoff; feed offers only 2.0.0-preview2 versus the pinned 8.0.0.
- Host-auth binding, supervisor/scheduler, durable outbox, HTTP/WS, privacy detectors, OTel ledger, provider matrix, releases: owned by their assemblies/pipeline, untouched per slice boundary.
- Pre-existing `tests/Unit/OpenConnect.Core.Tests/OpenConnect.Core.Tests.csproj` worktree modification: `git diff --ignore-space-at-eol` showed no content diff (only a line-ending warning); excluded from all evidence and left untouched.
- Current-run environment discrepancy: `dotnet --sdk-version` failed because this SDK does not recognize that command; `dotnet --version` returned `8.0.427`.

## Maintenance

Point-in-time record. For a full rerun, execute these four commands and re-verify each changed row; never edit verdicts without observed runs:

```powershell
dotnet test tests/Unit/OpenConnect.Agents.Tests/OpenConnect.Agents.Tests.csproj -c Release
dotnet test tests/Unit/OpenConnect.Agents.Tests/OpenConnect.Agents.Tests.csproj -c Release --no-build
dotnet test tests/Unit/OpenConnect.Core.Tests/OpenConnect.Core.Tests.csproj -c Release
dotnet test tests/Unit/OpenConnect.Core.Tests/OpenConnect.Core.Tests.csproj -c Release --no-build
```

In this task, only the two build-including commands in this list were executed. Enumerate test files by `tests/Unit/OpenConnect.Agents.Tests/**/*.cs` and `tests/Unit/OpenConnect.Core.Tests/*.cs` at maintenance time; do not trust this file's lists.
