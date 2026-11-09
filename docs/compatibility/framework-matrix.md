# Framework Compatibility Matrix

_Generated 2026-11-09 19:30 UTC by `build/scripts/render-compatibility-matrix.py` (schema `openconnect.compatibility/v1`). Do not hand-edit cells; re-run the script. Cells marked `pending` are evidence placeholders, not results._

Repo default TFM(s) (Directory.Build.props): `net8.0`.
See `docs/compatibility/runtime-support-policy.md` for the support policy.

| Package | net8.0 | net9.0 | net10.0 | Notes |
|---|---|---|---|---|
| `OpenConnect.Abstractions` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Agents` | supported (tests: 480x3) | supported (tests: 480x3) | supported (tests: 480x3) |  |
| `OpenConnect.Client` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Components.Blazor` | supported (tests: 14x3) | supported (tests: 14x3) | supported (tests: 14x3) |  |
| `OpenConnect.Components.Contracts` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Core` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Hosting.AspNetCore` | supported (tests: 121x3) | supported (tests: 121x3) | supported (tests: 121x3) |  |
| `OpenConnect.Metering` | supported (tests: 12x3) | supported (tests: 12x3) | supported (tests: 12x3) |  |
| `OpenConnect.Navigator` | supported (tests: 44x3) | supported (tests: 44x3) | supported (tests: 44x3) |  |
| `OpenConnect.Navigator.AspNetCore` | supported (tests: 68x3) | supported (tests: 68x3) | supported (tests: 68x3) |  |
| `OpenConnect.Navigator.Blazor` | supported (tests: 38x3) | supported (tests: 38x3) | supported (tests: 38x3) |  |
| `OpenConnect.Navigator.Operational` | supported (tests: 23x3) | supported (tests: 23x3) | supported (tests: 23x3) |  |
| `OpenConnect.Navigator.OperationalBrowser` | supported (tests: 26x3) | supported (tests: 26x3) | supported (tests: 26x3) |  |
| `OpenConnect.Observability` | supported (tests: 46x3) | supported (tests: 46x3) | supported (tests: 46x3) |  |
| `OpenConnect.Persistence.EFCore` | supported (tests: 105x3) | supported (tests: 105x3) | supported (tests: 105x3) |  |
| `OpenConnect.Privacy` | supported (tests: 3x3) | supported (tests: 3x3) | supported (tests: 3x3) |  |
| `OpenConnect.QdrantVactor` | supported (tests: 24x3) | supported (tests: 24x3) | supported (tests: 24x3) |  |
| `OpenConnect.Tools` | supported (tests: 4x3) | supported (tests: 4x3) | supported (tests: 4x3) |  |
| `OpenConnect.Transport.Nats` | supported (tests: 9x3) | supported (tests: 9x3) | supported (tests: 9x3) |  |

## Legend

- `supported (consumer smoke: pass)` — manifest evidence (pack + per-TFM consumer run) recorded.
- `supported (tests: Nx3)` — manifest `testSuites` evidence: per-TFM suite passed+skipped==total with 0 failed on all 3 TFMs (N per TFM). Consumer smoke keeps precedence where present.
- `targeted — evidence: pending (CF-IMP-003)` — csproj targets the TFM but no CI evidence recorded yet.
- `not targeted` — csproj does not list the TFM.

## Cross-TFM client/host smoke

- forward: client `net8.0` against host `net10.0` → `pass` (commit `9155c71`).
- reverse: client `net10.0` against host `net8.0` → `pass` (commit `9155c71`).
- harness: `build/scripts/run-cross-tfm-smoke.ps1` @ `9155c71` → `SMOKE_PASS`.

## Notes

Contracts.Tests: 143 tests net8.0-by-design (test project targets net8.0 only); recorded as note-only, not a package support row. Cross-TFM harness build/scripts/run-cross-tfm-smoke.ps1 at commit 9155c71 reported SMOKE_PASS (CF-POSTVERIFY-024 exit 0, CLIENT_PASS x2).

## Open questions

- Test-project partition per TFM is reconciled once CF-IMP-003 lands (see CF-IMP-017 handoff).
- Nightly OS/RID matrix expansion is out of scope for CF-IMP-017.
