# Task 005B Todo and portal quality gates

**Status:** Partial, evidence-gated.

- **TASK005B-REQ-001:** Preserve `samples/Blazor` and its process-local `InMemoryTodoStore`.
- **TASK005B-REQ-002:** Verify the existing Blazor sample starts and serves its current UI without requiring AI infrastructure.
- **TASK005B-REQ-003:** Verify gateway-backed authenticated task, session, tool, persistence and event behavior using the actual gateway application.
- **TASK005B-REQ-004:** Verify portal browser routes, responsive layouts and accessibility with Playwright/axe.
- **TASK005B-REQ-005:** Verify published-package capability subsets for net8/net9/net10 without source substitution.
- **TASK005B-REQ-006:** Validate OpenAPI semantics and endpoint coverage against the actual gateway.

## Current disposition

`TASK005B-REQ-002` is PASS after correcting the stale HTTP marker in `samples/EndToEnd/cases/Run-Blazor.ps1` from the removed `What needs attention?` text to the current `Task Management` heading.

`TASK005B-REQ-003` is BLOCKED for a genuine Todo tool round trip: the existing `samples/workers/shared/OpenConnect.Samples.Workers.Host` is a worker gateway with sample authentication and MAF task execution, but it does not register Todo domain tools or the Blazor `ITodoStore`. The Blazor process also does not map `MapOpenConnect`; it calls a separate gateway. Adding a replacement gateway or Todo tool host would violate the preservation boundary.

`TASK005B-REQ-004` is NOT VERIFIED: no Playwright test project or axe integration exists in the repository.

`TASK005B-REQ-005` is PARTIAL: clean NuGet consumers pass for all three TFMs, but `OpenConnect.Agents` is unavailable on net8 because published 1.2.1 depends on Microsoft.Extensions.DependencyInjection >=10.0.12.

`TASK005B-REQ-006` is PARTIAL: OpenAPI references and JSON Schemas parse, but full semantic validation and route coverage are not yet implemented.
