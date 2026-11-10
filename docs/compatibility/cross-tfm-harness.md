# Cross-TFM Smoke Harness

**Purpose.** A checked-in, CI-invocable single-command reproduction of the
twice-verified temp PoC legs **CF-AT-023** (forward: net10.0 host + net8.0
client) and **CF-AT-024** (reverse: net8.0 host + net10.0 client). The harness
lives at `build/scripts/run-cross-tfm-smoke.ps1` and is fully self-contained:
it embeds the minimal host/client sources (derived from the temp PoC, not
dependent on temp leftovers) and scaffolds all four apps per run.

**What it proves (CF-AT-023/024).** Each leg boots a real Kestrel host on
loopback and drives a full authenticated lifecycle from the opposite-TFM
client: unauthenticated health check, anonymous-request 401 boundary proof,
session create (raw HTTP), capabilities read, task create, task poll to
`Completed`, session read-back, event read at cursor 0 (expects the
executor-appended `task.note` event), and exclusive replay at the last cursor
(expects empty). A leg passes only on client exit 0 **plus** the `CLIENT_PASS`
marker line.

## How to run

```powershell
# Cheap checks (no dotnet invocations, no hosts started):
powershell -NoProfile -File build/scripts/run-cross-tfm-smoke.ps1 -DryRun

# Full live run (packs 5 packages, scaffolds + builds 4 apps, runs both legs):
powershell -NoProfile -File build/scripts/run-cross-tfm-smoke.ps1 `
  -Configuration Release -PortBase 5251 -TimeoutSec 180
```

Parameters: `-Configuration` (default `Release`), `-PortBase` (default `5251`;
forward leg uses `PortBase`, reverse uses `PortBase + 1`), `-TimeoutSec`
(default `180`, per-leg host-ready + client budget), `-DryRun` (plan only).

## Expected output

Each leg prints `HOST_READY`, the client lifecycle trace (`HEALTH ok`,
`ANON_STATUS 401`, `SESSION_STATUS`, `TASK_CREATED`, `TASK_READ`,
`EVENTS count=`, `REPLAY_AFTER_* count=0`), and finally:

```text
CLIENT_PASS lifecycle complete: session+task+read+events+cursor
```

Success overall prints `SMOKE_PASS forward+reverse CLIENT_PASS x2.` and exits
0 -- i.e. **CLIENT_PASS x2** (once per leg). Any failure exits non-zero with a
fail-closed message naming the leg and step (`pack`, `scaffold-build`,
`host-ready`, `client`, `client-assert`, `host-stop`, `port-free`).

## Limitations (mirror the PoC caveats)

- **HTTP loopback only.** No TLS, no remote hosts; Kestrel binds
  `127.0.0.1` and the client sets `SocketsHttpHandler.UseProxy = false` so
  loopback traffic bypasses any machine proxy.
- **ULID path.** Fixture credentials are the fixed ULID pair from the PoC
  (`01ARZ3NDEKTSV4RRFFQ69G5FAV` / `01ARZ3NDEKTSV4RRFFQ69G5FAW`) because the
  host `FixtureHandler` validates with `Ulid.Parse`; random per-run GUIDs
  would fail parsing. No real secrets involved.
- **Single tenant.** One tenant/principal pair per run; no multi-tenant or
  cross-tenant isolation coverage.
- **Fixture auth.** The `Fixture` scheme + `AllowAuthorizer` + in-memory
  append-and-complete executor mirror the hosting unit-test fixture; no real
  authentication provider, authorization policy, or worker backend is
  exercised.
- **Local feed.** `dotnet pack` output for Abstractions + Core + Agents +
  Client + Hosting.AspNetCore is consumed via a temp local feed with floating
  (`*`) package references; nuget.org remains a fallback source for
  third-party dependencies.

## CI wiring note

This harness is intended to be called from a future
`cross-framework-integration.yml` job (e.g. a `cross-tfm-smoke` job that runs
the script with `-Configuration Release` after the pack step). The workflow
file itself is **not** edited by this task -- see the harness task contract.
The full live run is post-commit verification, not part of the check-in task.
