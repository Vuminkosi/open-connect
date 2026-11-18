# Task 005B evidence

**Revision audited:** `49b9235` before this task's test-marker correction.
**Current correction commit:** pending.

## PASS

- Full non-live solution regression: 1,437 total, 1,436 passed, 0 failed, 1 skipped.
- Clean NuGet consumers from `https://api.nuget.org/v3/index.json` at OpenConnect 1.2.1: net8, net9 and net10 core consumers build and run. Net9/net10 include published `OpenConnect.Agents`; net8 records the published MAF dependency incompatibility.
- Existing Blazor HTTP smoke after correction:

```powershell
./samples/EndToEnd/cases/Run-Blazor.ps1 -RepoRoot . -DbFile <temp> -LogDir <temp> -Port 5399 -StartupTimeoutSec 120
```

Observed: HTTP 200, current `Task Management` marker, port released, process tree stopped, SQLite file removed.

## BLOCKED

A genuine Todo gateway/tool E2E cannot be executed with the existing applications. `samples/Blazor` has the Todo store and client UI but no `MapOpenConnect`; `OpenConnect.Samples.Workers.Host` maps the gateway but has no Todo store or Todo tool registration. Creating a new bridge would violate the task boundary.

## NOT VERIFIED

Playwright desktop/tablet/mobile tests, axe-core, gateway-backed Todo tool invocation, WebSocket replay, gateway restart recovery, optional-worker outage, live privacy/model egress, semantic OpenAPI validation and production deployment.
