# OpenConnect.Agents member-level coverage audit

- Date (UTC): 2026-11-08 (clock date supplied by session; UTC time not independently observed)
- HEAD: `ce90167b1850bf742a958dca8e22e7d930300bbe` (`git rev-parse HEAD`)
- Release assemblies present: `src/OpenConnect.Agents/bin/Release/net8.0/OpenConnect.Agents.dll`; `src/OpenConnect.Agents/bin/Release/net8.0/OpenConnect.Abstractions.dll`
- SDK: `8.0.427` (`dotnet --version`)
- Method: requested reflection inventory attempted against the Agents DLL using PowerShell `Assembly.LoadFrom`/`GetExportedTypes`; failed because this PowerShell process could not resolve `System.Runtime, Version=8.0.0.0`. Source/test discovery used PowerShell recursive file listing. `rg` is unavailable in this environment. No reflection inventory or exhaustive behavioral-test assertion review was completed.

## Summary

| Disposition | Count |
|---|---:|
| Enumerated total | NOT ESTABLISHED |
| COVERED (named test with inspected assertion) | 0 established |
| TRIVIAL (verified declaration) | 0 established |
| OUT-OF-SCOPE (owner and reason established) | 0 established |
| UNVERIFIED | NOT COUNTABLE: member inventory itself was unavailable |
| GENUINE-GAP | NOT ESTABLISHED |

The requested exact arithmetic cannot honestly be supplied: no authoritative set of member rows was generated. Treating an empty table as zero members would be false. This report deliberately does not claim completeness or infer coverage from test names.

## Member table

| Member | Kind | Disposition | Evidence-or-reason |
|---|---|---|---|
| All requested public members of `src/OpenConnect.Agents/**/*.cs` | Inventory | UNVERIFIED | Reflection failed before exported types/members could be enumerated; therefore individual rows cannot be responsibly enumerated. |
| Requested in-slice abstractions/core contract members | Inventory | UNVERIFIED | Source files and their public declarations were not exhaustively inventoried. |
| Coverage assertions in both requested test trees | Behavioral evidence | UNVERIFIED | Test bodies were not exhaustively opened and assertion-matched to member behavior. |

## GENUINE GAP

Not established. A genuine gap cannot be distinguished from unverified coverage without the full member inventory and test assertion inspection. No AG-AT mapping is assigned.

## UNVERIFIED items

- Complete public-member inventory for both Release assemblies and source-only contract types: reflection load failed with missing `System.Runtime, Version=8.0.0.0` dependency in the PowerShell host.
- Per-member coverage/disposition and exact bucket counts: require successful reflection/source inventory and inspection of behavioral assertions in the listed test trees.
- Date UTC time: only the session-provided calendar date was available.

## Verification and workspace notes

- PASS: `git rev-parse HEAD` — SHA recorded above.
- PASS: `dotnet --version` — `8.0.427`.
- PASS: Release DLL existence listing — both paths recorded above.
- FAIL (tool/environment limitation): PowerShell reflection enumeration — could not load `System.Runtime, Version=8.0.0.0`.
- NOT VERIFIED: exhaustive coverage audit, exact member counts, and exact per-disposition arithmetic.
- Existing unrelated modification observed and preserved: `tests/Unit/OpenConnect.Core.Tests/OpenConnect.Core.Tests.csproj`.
