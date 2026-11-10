# ADR-CF-004 — Blazor / Razor Narrower-Support Exception

**Status:** Accepted
**Date:** 2026-11-09
**Supersedes:** none

Closes the CF-REV-007 gap note under CF-REQ-014. Audience: maintainers and reviewers verifying API-compatibility tracking scope.

## Context

API-compat tracking in this repository uses `Microsoft.CodeAnalysis.PublicApiAnalyzers` version 3.3.4 (pinned in `Directory.Packages.props`) together with per-library `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` inventories enforced by `build/scripts/verify-public-api.py`.

Seventeen library projects carry those tracking inventories. The two Blazor UI libraries do not:

- `src/OpenConnect.Components.Blazor/OpenConnect.Components.Blazor.csproj` (Razor SDK, MudBlazor 8.15.0)
- `src/OpenConnect.Navigator.Blazor/OpenConnect.Navigator.Blazor.csproj` (Razor SDK, framework-only, no MudBlazor dependency)

Neither project references `PublicApiAnalyzers`, and no `PublicAPI.*.txt` files exist under either project directory. Both remain multi-targeted for compile via `$(OpenConnectLibraryTargetFrameworks)` like the other libraries; only the API-tracking inventory is absent.

## Decision

The two Blazor/Razor modules are declared as **optional modules with narrower support**: they stay multi-targeted for compile but carry no API-tracking inventory, and no API-parity gate applies to them. This is a scoped exception to the repository-wide API-compat tracking rule, not a change to that rule for the 17 tracked libraries.

## Rationale / Evidence

Two independent implementation batches hit the same analyzer friction when tracking was attempted for Razor-generated code:

- **RS0016** on `*.razor.g.cs` generated symbols (`BuildRenderTree`, `Execute`) — the analyzer demands inventory entries for compiler-generated members that authors do not declare in source.
- **RS0041** oblivious references — generated code triggers nullability-oblivious diagnostics that cannot be resolved by editing handwritten source.

Both diagnostics describe generated symbols, not handwritten public API. Maintaining a hand-curated inventory over generated members would create churn on every Razor compiler output change with no corresponding authored API change. The exclusion is therefore a tooling-limitation decision, verified by inspection of the two `.csproj` files (Razor SDK, no analyzer reference) and the absence of tracking files under the two project directories.

## Consequences

- No API-parity gate (`verify-public-api.py`, RS0016/RS0017) applies to `OpenConnect.Components.Blazor` or `OpenConnect.Navigator.Blazor`.
- MudBlazor compatibility is still verified through the Blazor test suites (`tests/Unit/OpenConnect.Blazor.Tests`, `tests/Unit/OpenConnect.Navigator.Blazor.Tests`, referenced in `cross-framework-ci.yml`); this ADR does not remove test coverage.
- The remaining 17 tracked libraries are unaffected; their tracking inventories and enforcement stay in force.
- Revisit this exception if a future `PublicApiAnalyzers` release gains a supported generated-code exclusion that lets Razor output be tracked cleanly.
