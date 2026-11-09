# Framework Compatibility Matrix

_Generated 2026-11-09 17:01 UTC by `build/scripts/render-compatibility-matrix.py` (schema `openconnect.compatibility/v1`). Do not hand-edit cells; re-run the script. Cells marked `pending` are evidence placeholders, not results._

Repo default TFM(s) (Directory.Build.props): `net8.0`.
See `docs/compatibility/runtime-support-policy.md` for the support policy.

| Package | net8.0 | net9.0 | net10.0 | Notes |
|---|---|---|---|---|
| `OpenConnect.Abstractions` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Agents` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Client` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Components.Blazor` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Components.Contracts` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Core` | supported (consumer smoke: pass) | supported (consumer smoke: pass) | supported (consumer smoke: pass) |  |
| `OpenConnect.Hosting.AspNetCore` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Metering` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Navigator` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Navigator.AspNetCore` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Navigator.Blazor` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Navigator.Operational` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Navigator.OperationalBrowser` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Observability` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Persistence.EFCore` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Privacy` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.QdrantVactor` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Tools` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |
| `OpenConnect.Transport.Nats` | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) | targeted — evidence: pending (CF-IMP-003) |  |

## Legend

- `supported (consumer smoke: pass)` — manifest evidence (pack + per-TFM consumer run) recorded.
- `targeted — evidence: pending (CF-IMP-003)` — csproj targets the TFM but no CI evidence recorded yet.
- `not targeted` — csproj does not list the TFM.

## Open questions

- Test-project partition per TFM is reconciled once CF-IMP-003 lands (see CF-IMP-017 handoff).
- Nightly OS/RID matrix expansion is out of scope for CF-IMP-017.
