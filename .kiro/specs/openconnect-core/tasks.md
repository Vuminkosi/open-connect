---
inclusion: always
name: OpenConnect Tasks
description: Implementation work packages with dependency graph
---

# OpenConnect Implementation Tasks

**Version:** 1.1-draft
**Status:** Implementation plan
**Last Updated:** 2026-11-06

## Task Format

Each task contains:
- **ID:** Unique identifier (IMP-XXX)
- **Objective:** What to implement
- **Requirements:** OC-* references
- **Dependencies:** Prerequisite tasks
- **Files/Packages:** Affected code
- **Acceptance:** AT-* references
- **Tests:** Required test coverage
- **Security:** Security considerations
- **Definition of Done:** Completion criteria

---

## Stage S0: Audit and Baseline

### IMP-001 — Repository Setup

**Objective:** Initialize clean OpenConnect repository with proper structure and tooling.

**Requirements:** None

**Dependencies:** None

**Files/Packages:**
- `/.gitignore`
- `/global.json`
- `/Directory.Build.props`
- `/Directory.Packages.props`
- `/OpenConnect.slnx`
- `/.editorconfig`
- `/AGENTS.md`
- `/LICENSE`
- `/README.md`

**Acceptance:** AT-001 (partial)

**Tests:**
- Clean checkout builds minimal solution
- Lock files committed and reproducible

**Security:** None

**Definition of Done:**
- [ ] Repository initialized
- [ ] Build succeeds with empty projects
- [ ] All lock files committed
- [ ] CI workflow scaffold created

---

### IMP-002 — Swift-Aid Source Lock

**Objective:** Pin Swift-Aid commit SHA and document extraction inventory.

**Requirements:** None

**Dependencies:** IMP-001

**Files/Packages:**
- `/docs/migration/SOURCE-LOCK.md`
- `/docs/migration/swiftaid-inventory.csv`

**Acceptance:** Checklist §1

**Tests:** None (documentation)

**Security:** Verify no patient data copied

**Definition of Done:**
- [ ] Swift-Aid SHA pinned (5a6bad62f87d9e9dfabbc2d0fa1a0b491f0bada7)
- [ ] Inventory CSV complete
- [ ] No patient/clinical data included

---

### IMP-003 — Legal License Audit

**Objective:** Verify all dependency and model licenses before incorporation.

**Requirements:** None

**Dependencies:** IMP-002

**Files/Packages:**
- `/docs/migration/LICENSE-AUDIT.md`
- `/THIRD-PARTY-NOTICES`

**Acceptance:** Checklist §1

**Tests:** None (documentation)

**Security:** Verify no unlicensed redistribution

**Definition of Done:**
- [ ] All NuGet dependencies reviewed
- [ ] All Python dependencies reviewed
- [ ] Model licenses verified
- [ ] Third-party notices file created

---

### IMP-004 — Contract Governance

**Objective:** Create canonical JSON Schema, OpenAPI, and fixture corpus.

**Requirements:** OC-ARC-001

**Dependencies:** IMP-001

**Files/Packages:**
- `/contracts/openapi/openconnect.v1.yaml`
- `/contracts/jsonschema/*.json`
- `/contracts/fixtures/`

**Acceptance:** AT-031 (partial)

**Tests:**
- Schema validation passes for valid fixtures
- Schema validation rejects invalid fixtures

**Security:** None

**Definition of Done:**
- [ ] OpenAPI spec created
- [ ] JSON schemas created
- [ ] Fixture corpus created
- [ ] Schema validation tests pass

---

### IMP-005 — Threat Model

**Objective:** Document trust boundaries and attacker scenarios.

**Requirements:** OC-PRV-008

**Dependencies:** IMP-001

**Files/Packages:**
- `/docs/architecture/adr/threat-model.md`

**Acceptance:** Checklist §1

**Tests:** None (documentation)

**Security:** Critical for security test planning

**Definition of Done:**
- [ ] Trust boundaries documented
- [ ] Attacker scenarios defined
- [ ] Mitigations mapped to requirements

---

### IMP-006 — CI Baseline

**Objective:** Establish PR CI workflow with required checks.

**Requirements:** OC-REL-002

**Dependencies:** IMP-001, IMP-004

**Files/Packages:**
- `/.github/workflows/ci.yml`
- `/.github/CODEOWNERS`

**Acceptance:** AT-019 (partial)

**Tests:**
- Failed test blocks protected branch

**Security:** Actions pinned to SHA

**Definition of Done:**
- [ ] CI workflow created
- [ ] Build/test/format jobs defined
- [ ] Branch protection configured
- [ ] CODEOWNERS configured

---

## Stage S1: Core Abstractions

### IMP-010 — Core Abstractions

**Objective:** Define strongly-typed identifiers, interfaces, and enums.

**Requirements:** OC-ARC-001, OC-CFG-010

**Dependencies:** IMP-001, IMP-004

**Files/Packages:**
- `/src/OpenConnect.Abstractions/`
  - `Identifiers.cs`
  - `Enums.cs`
  - `Interfaces.cs`
  - `DTOs.cs`
  - `Errors.cs`

**Acceptance:** AT-001 (partial)

**Tests:**
- Identifier serialization
- Enum wire format conversion
- Interface contracts

**Security:** None (abstractions only)

**Definition of Done:**
- [ ] All identifiers defined
- [ ] All enums defined with wire converters
- [ ] Core interfaces defined
- [ ] DTOs defined
- [ ] Error codes defined
- [ ] Unit tests pass

---

### IMP-011 — Composition Root

**Objective:** Implement `AddOpenConnect()` with default registration.

**Requirements:** OC-CFG-001, OC-CFG-016

**Dependencies:** IMP-010

**Files/Packages:**
- `/src/OpenConnect.Core/`
  - `Configuration/OpenConnectOptions.cs`
  - `Extensions/OpenConnectServiceCollectionExtensions.cs`
- `/src/OpenConnect.Hosting.AspNetCore/`
  - `OpenConnectWebHostExtensions.cs`

**Acceptance:** AT-001, AT-032

**Tests:**
- Default registration creates lead agent
- Default registration enables privacy
- Privacy disabled emits warning
- Invalid configuration rejected at startup

**Security:**
- Privacy default enabled
- Auth/authz never disabled

**Definition of Done:**
- [ ] Options classes implemented
- [ ] DI registration implemented
- [ ] Validation implemented
- [ ] Warning emitted for privacy disabled
- [ ] Unit tests pass

---

### IMP-012 — Runtime Instructions

**Objective:** Load and validate `agent-runtime.md` safely.

**Requirements:** OC-CFG-002

**Dependencies:** IMP-011

**Files/Packages:**
- `/src/OpenConnect.Core/`
  - `Runtime/InstructionLoader.cs`

**Acceptance:** AT-003

**Tests:**
- Valid instructions load
- Path traversal blocked
- Oversized instructions rejected
- Malicious grants ignored

**Security:**
- Path traversal prevention
- Size limits enforced
- No grants from Markdown

**Definition of Done:**
- [ ] Instruction loader implemented
- [ ] Path validation implemented
- [ ] Size limits enforced
- [ ] Security tests pass

---

### IMP-013 — YAML Configuration

**Objective:** Parse and validate `openconnect.yaml` configuration.

**Requirements:** OC-CFG-003

**Dependencies:** IMP-011, IMP-012

**Files/Packages:**
- `/src/OpenConnect.Core/`
  - `Configuration/YamlConfigurationProvider.cs`

**Acceptance:** Configuration validation tests

**Tests:**
- Valid YAML loads
- Unknown keys rejected
- Cycles detected
- References validated

**Security:**
- No secrets in YAML
- No policy weakening

**Definition of Done:**
- [ ] YAML parser implemented
- [ ] Validation implemented
- [ ] Tests pass

---

### IMP-014 — MAF Adapter

**Objective:** Verify actual MAF APIs and create adapter layer.

**Requirements:** OC-ARC-002

**Dependencies:** IMP-010, IMP-011

**Files/Packages:**
- `/src/OpenConnect.Agents/`
  - `MafAdapter/MafAgentFactory.cs`

**Acceptance:** AT-003, AT-004

**Tests:**
- MAF compilation smoke
- Real agent creation
- Tool execution trace

**Security:**
- Authorization wrapped
- Privacy gates applied

**Definition of Done:**
- [ ] MAF dependency pinned
- [ ] Adapter compiles against real MAF
- [ ] Agent creation works
- [ ] Events projected correctly

---

### IMP-015 — Capability Registry

**Objective:** Implement dynamic capability registration and lookup.

**Requirements:** OC-ARC-004

**Dependencies:** IMP-010

**Files/Packages:**
- `/src/OpenConnect.Core/`
  - `Capabilities/CapabilityRegistry.cs`

**Acceptance:** AT-009

**Tests:**
- Registration works
- Stale entries removed
- Missing capabilities reported unavailable

**Security:** None

**Definition of Done:**
- [ ] Registry implemented
- [ ] TTL/health implemented
- [ ] Tests pass

---

### IMP-016 — Tool Broker

**Objective:** Implement tool discovery with policy filtering.

**Requirements:** OC-AGT-004, OC-AGT-006

**Dependencies:** IMP-010, IMP-014

**Files/Packages:**
- `/src/OpenConnect.Tools/`
  - `ToolCatalog.cs`
  - `ToolBroker.cs`
  - `ToolAuthorizationService.cs`

**Acceptance:** AT-004

**Tests:**
- Discovery filtered by policy
- Reauthorization before execution
- Unauthorized calls denied

**Security:**
- Authorization enforced
- Schema validation
- Approval binding

**Definition of Done:**
- [ ] Catalog implemented
- [ ] Broker implemented
- [ ] Authorization service implemented
- [ ] Security tests pass

---

### IMP-017 — Host Privacy Enforcement

**Objective:** Implement .NET-side privacy firewall (policy engine, tokenization, egress gates).

**Requirements:** OC-PRV-001–009

**Dependencies:** IMP-010, IMP-011, IMP-016

**Files/Packages:**
- `/src/OpenConnect.Privacy/`
  - `PrivacyFirewall.cs`
  - `Policy/PolicyEngine.cs`
  - `Tokenization/TokenGenerator.cs`
  - `Tokenization/TokenVault.cs`
  - `Gates/EgressGate.cs`

**Acceptance:** AT-001, AT-002, AT-008

**Tests:**
- Privacy default on
- Privacy disabled warning
- Cross-tenant isolation
- Egress blocking

**Security:** Critical

**Definition of Done:**
- [ ] Firewall implemented
- [ ] Policy engine implemented
- [ ] Token vault implemented
- [ ] Egress gates implemented
- [ ] Security tests pass

---

### IMP-018 — Provider Adapter

**Objective:** Create model provider abstraction.

**Requirements:** OC-ARC-002

**Dependencies:** IMP-014, IMP-017

**Files/Packages:**
- `/src/OpenConnect.Agents/`
  - `Providers/ProviderAdapter.cs`

**Acceptance:** Provider tests

**Tests:**
- Chat capability declared
- Streaming capability declared
- Unsupported features fail

**Security:** Privacy gate integrated

**Definition of Done:**
- [ ] Provider adapter implemented
- [ ] Capability declarations work
- [ ] Tests pass

---

### IMP-019 — Runtime Smoke Test

**Objective:** End-to-end minimal host test.

**Requirements:** OC-AGT-003

**Dependencies:** IMP-010–018

**Files/Packages:**
- `/examples/minimal-host/`

**Acceptance:** AT-001

**Tests:**
- Real task execution
- Trace recorded
- No domain dependencies

**Security:**
- Privacy enforced
- Authorization enforced

**Definition of Done:**
- [ ] Minimal host runs
- [ ] Task completes
- [ ] Events recorded

---

## Stage S1/S2: Typed Configuration (IMP-090–099)

### IMP-090 — Enum Contracts

**Objective:** Implement all typed enums with wire format converters.

**Requirements:** OC-CFG-010

**Dependencies:** IMP-010

**Files/Packages:**
- `/src/OpenConnect.Abstractions/Enums.cs`
- `/contracts/jsonschema/enums.v1.json`

**Acceptance:** AT-031

**Tests:**
- Wire format conversion
- Unknown value rejection
- Numeric rejection
- Cross-language parity

**Security:** None (contracts)

**Definition of Done:**
- [ ] All enums implemented
- [ ] Converters implemented
- [ ] Schema parity verified
- [ ] Tests pass

---

### IMP-091 — Options Validation

**Objective:** Implement `IValidateOptions` with comprehensive rules.

**Requirements:** OC-CFG-011–020

**Dependencies:** IMP-090

**Files/Packages:**
- `/src/OpenConnect.Core/Configuration/OpenConnectOptions.cs`
- `/src/OpenConnect.Core/Configuration/OpenConnectOptionsValidator.cs`

**Acceptance:** AT-032

**Tests:**
- Valid configuration accepted
- Invalid mode combinations rejected
- Missing dependencies detected

**Security:**
- Invalid security config rejected
- Unsafe combinations blocked

**Definition of Done:**
- [ ] Validator implemented
- [ ] All validation rules enforced
- [ ] Tests pass

---

### IMP-092 — Interactive Execution Context

**Objective:** Implement authenticated user context for interactive agents.

**Requirements:** OC-AGT-020–027

**Dependencies:** IMP-011, IMP-016

**Files/Packages:**
- `/src/OpenConnect.Agents/`
  - `Execution/AgentExecutionContext.cs`
  - `Execution/InteractivePrincipalProvider.cs`

**Acceptance:** AT-033, AT-034

**Tests:**
- Context derived from auth
- No raw token propagation
- Delegation narrows scope
- Reconnect doesn't expand

**Security:** Critical

**Definition of Done:**
- [ ] Context implementation complete
- [ ] Scope derivation works
- [ ] Security tests pass

---

### IMP-093 — Operational Agent Supervisor

**Objective:** Implement `BackgroundService` for operational agents.

**Requirements:** OC-AGT-028–039

**Dependencies:** IMP-090, IMP-091

**Files/Packages:**
- `/src/OpenConnect.Agents/`
  - `Operational/OperationalAgentSupervisor.cs`
  - `Operational/OperationalAgentRegistry.cs`

**Acceptance:** AT-035, AT-036

**Tests:**
- No HttpContext access
- Workload identity used
- Scheduling works
- Misfire handling

**Security:** Critical

**Definition of Done:**
- [ ] Supervisor implemented
- [ ] Identity separation verified
- [ ] Tests pass

---

### IMP-094 — Durable Operational Runs

**Objective:** Implement PostgreSQL persistence for operational runs.

**Requirements:** OC-AGT-028–039

**Dependencies:** IMP-020, IMP-093

**Files/Packages:**
- `/src/OpenConnect.Persistence.Postgres/`
  - `Migrations/` (operational tables)
- `/src/OpenConnect.Agents/Operational/`

**Acceptance:** AT-037

**Tests:**
- Lease/fencing works
- Crash recovery works
- No duplicate side effects

**Security:** Critical

**Definition of Done:**
- [ ] Schema created
- [ ] Persistence implemented
- [ ] Tests pass

---

### IMP-095 — Operational Control APIs

**Objective:** Implement pause/resume/trigger APIs.

**Requirements:** OC-AGT-028–039

**Dependencies:** IMP-094

**Files/Packages:**
- `/src/OpenConnect.Hosting.AspNetCore/`
  - `Endpoints/OperationalEndpoints.cs`

**Acceptance:** AT-038

**Tests:**
- RBAC enforced
- Review workflow works
- Tenant isolation

**Security:** Critical

**Definition of Done:**
- [ ] APIs implemented
- [ ] Authorization enforced
- [ ] Tests pass

---

### IMP-096 — Detector Loaders

**Objective:** Implement independent detector configuration.

**Requirements:** OC-FW-049–054

**Dependencies:** IMP-080, IMP-081

**Files/Packages:**
- `/src/OpenConnect.Privacy/Detectors/`
- `/workers/security/src/openconnect_security/detectors/`

**Acceptance:** AT-039

**Tests:**
- Disabled = no load
- Enabled = capability advertised
- Independent toggles

**Security:** Critical

**Definition of Done:**
- [ ] Detector configuration works
- [ ] Independent toggles verified
- [ ] Tests pass

---

### IMP-097 — Coverage-Aware Policy

**Objective:** Implement coverage-based policy decisions.

**Requirements:** OC-FW-051

**Dependencies:** IMP-096

**Files/Packages:**
- `/src/OpenConnect.Privacy/Policy/CoverageEvaluator.cs`

**Acceptance:** AT-040

**Tests:**
- Required coverage evaluated
- Missing coverage blocks
- No silent fallback

**Security:** Critical

**Definition of Done:**
- [ ] Coverage evaluator implemented
- [ ] Tests pass

---

### IMP-098 — Lean Docker Profiles

**Objective:** Create minimal Docker configuration.

**Requirements:** OC-FW-054

**Dependencies:** IMP-037

**Files/Packages:**
- `/infra/compose.yaml`
- `/workers/security/Dockerfile`

**Acceptance:** AT-041

**Tests:**
- Rules-only runs
- No bundled models
- Resource limits

**Security:** Critical

**Definition of Done:**
- [ ] Docker files created
- [ ] Tests pass

---

### IMP-099 — CI and Distribution

**Objective:** Complete CI for typed configuration.

**Requirements:** OC-REL-002

**Dependencies:** IMP-090–098

**Files/Packages:**
- `/.github/workflows/ci.yml` (extended)

**Acceptance:** AT-042

**Tests:**
- Enum parity across languages
- Agent mode tests
- Detector toggle tests

**Security:** Critical

**Definition of Done:**
- [ ] CI jobs added
- [ ] All tests pass

---

## Dependency Graph

```
IMP-001 (Repo Setup)
  ├─→ IMP-002 (Source Lock)
  │     └─→ IMP-003 (License Audit)
  ├─→ IMP-004 (Contracts)
  │     └─→ IMP-006 (CI Baseline)
  │           └─→ IMP-099 (CI Extended)
  └─→ IMP-005 (Threat Model)

IMP-010 (Abstractions)
  ├─→ IMP-011 (Composition)
  │     ├─→ IMP-012 (Instructions)
  │     │     └─→ IMP-013 (YAML Config)
  │     └─→ IMP-014 (MAF Adapter)
  │           └─→ IMP-018 (Providers)
  │                 └─→ IMP-019 (Smoke Test)
  ├─→ IMP-090 (Enums)
  │     └─→ IMP-091 (Validation)
  │           ├─→ IMP-092 (Interactive)
  │           │     └─→ [Security Tests]
  │           └─→ IMP-093 (Operational)
  │                 └─→ IMP-094 (Durable Ops)
  │                       └─→ IMP-095 (Control APIs)
  └─→ IMP-015 (Capabilities)

IMP-016 (Tool Broker)
  └─→ IMP-017 (Privacy Firewall)
        ├─→ IMP-096 (Detectors)
        │     └─→ IMP-097 (Coverage)
        │           └─→ IMP-098 (Docker)
        └─→ IMP-080–089 (Security Worker)

IMP-020–029 (Persistence & Transport)
  └─→ [Required for durable features]
```

---

## Stage Priorities

### P0 — Foundation (Required for MVP)

- IMP-001–006 (Audit and baseline)
- IMP-010–019 (Core and MAF)
- IMP-090–099 (Typed configuration)
- IMP-080–089 (Security worker - partial)

### P1 — Demonstration

- IMP-040–049 (Blazor and clients)
- VoiceDesk application
- PrivateDocs application
- Selected workers

### P2 — Extended Framework

- Additional workers
- Advanced features
- Full operational scheduling
- GPU variants

---

## References

- Requirements: `.kiro/specs/openconnect-core/requirements.md`
- Design: `.kiro/specs/openconnect-core/design.md`
- Checklist: `docs/checklist.md`
- Source documents: `docs/plans/*.md`
