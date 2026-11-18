# OpenConnect Worker End-to-End Samples (`samples/workers/`)

End-to-end sample applications and shared verification harness for the **11 canonical OpenConnect workers**, plus the no-twelfth-image `voice-vad-client-server` sample and the optional `all-workers` stack (`docs/plans/workers-samples-e2e-spec.md`).

Each sample builds **only** its canonical production Dockerfile (`workers/<name>/Dockerfile` or `src/OpenConnect.WorkerHosts.<Name>/Dockerfile`) with a local `:e2e-local` tag in PR/CI. Zero build-time model checkpoint downloads, zero registry pushes in PR, and zero hardcoded secrets.

## Shared Harness & Persistence Layout

- `samples/workers/shared/OpenConnect.Samples.Workers.Contracts/` — Canonical worker descriptors, tier/provider validators, `RuntimeModelCacheManager` (§4.1–4.3 12-scenario cache verifier), `WorkerContractAssertions`, and `EvidenceGenerator` (`openconnect.samples.workers.evidence/v1`).
- `samples/workers/shared/OpenConnect.Samples.Workers.Host/` — Real ASP.NET Core OpenConnect sample gateway (`AddOpenConnect`, `AddOpenConnectAgent`, `AddOpenConnectTaskExecution`, `AddMafHostTaskExecutor`, `AddOpenConnectNavigator`, `MapOpenConnect`, `MapOpenConnectNavigator`), `SampleGatewayCoordinator`, `SampleRulesSecurityDetector`, `FakeExternalModelSink`, and `Dockerfile`.
- `samples/workers/shared/OpenConnect.Samples.Workers.TestKit/` — xUnit E2E test suite and runner container (`Dockerfile`).
- `samples/workers/shared/fixtures/{security,translation,audio,image,events,contracts,policy}/` — Synthetic test fixtures across §§5–6.
- `samples/workers/shared/traceability.csv` — Machine-readable crosswalk mapping `WSE2E-AT-001` through `WSE2E-AT-080`.
- `samples/persistence/{postgres,sqlserver,sqlite}/{ApplicationDbContext.cs,Migrations/}` — Sample-owned `ApplicationDbContext` and reviewed migrations using `modelBuilder.ApplyOpenConnectModel()` and `OpenConnectMigrationMode.Manual` (worker containers never execute DDL).

## Canonical 11 Worker Matrix

| ID | Sample Directory | Production Dockerfile / Compose | Canonical Image | Model Volume |
|---|---|---|---|---|
| `SEC` | `security/` | `workers/security/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-security` | `oc_security_models` |
| `CLS` | `classification/` | `workers/classification/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-classification` | `oc_classification_models` |
| `NLLB` | `nllb/` | `workers/nllb/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-nllb` | `oc_nllb_models` |
| `INF` | `inference/` | `workers/inference/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-inference` | `oc_inference_models` |
| `STT` | `transcription/` | `workers/transcription/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-transcription` | `oc_transcription_models` |
| `RTV` | `realtime-voice/` | `workers/realtime-voice/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-realtime-voice` | `oc_realtime_voice_models` |
| `KOK` | `kokoro/` | `workers/kokoro/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-kokoro` | `oc_kokoro_models` |
| `MMS` | `mms-tts/` | `workers/mms-tts/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-mms-tts` | `oc_mms_models` |
| `NAV` | `navigator-playwright/` | `workers/navigator-playwright/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-navigator-playwright` | *(none)* |
| `OPS` | `operational-agents/` | `src/OpenConnect.WorkerHosts.Operational/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-operational-agents` | *(none)* |
| `OUT` | `outbox/` | `src/OpenConnect.WorkerHosts.Outbox/{Dockerfile,compose.yaml}` | `vuminkosimatibe/openconnect-worker-outbox` | *(none)* |

Additional cross-worker samples:
- `voice-vad-client-server/` — Client/Server/Hybrid VAD verification with **no 12th worker image**.
- `all-workers/` — Independent optional profiles and full-stack drill.

## Runner Commands (§2.1)

```bash
# Build all local E2E worker images without model downloads
./samples/workers/shared/scripts/build-all.sh --tag e2e-local

# Run a single worker sample by tier and relational provider
./samples/workers/shared/scripts/run-one.sh security --tier contract --provider sqlite
./samples/workers/shared/scripts/run-one.sh kokoro --tier model-live --manifest /approved/kokoro.manifest.json
./samples/workers/shared/scripts/run-one.sh kokoro --tier offline-revision --manifest /approved/kokoro.manifest.json

# Run across all 11 workers
./samples/workers/shared/scripts/run-all.sh --tier contract --provider sqlite
./samples/workers/shared/scripts/run-all.sh --tier integration --provider postgres
./samples/workers/shared/scripts/run-all.sh --tier integration --provider sqlserver

# Verify no build-time model downloads, no registry pushes, no secrets, no `down -v`
./samples/workers/shared/scripts/verify-no-build-models.sh

# Collect sanitized machine-readable result.json (openconnect.samples.workers.evidence/v1) and summary.md
./samples/workers/shared/scripts/collect-evidence.sh --run-id local-verify-001

# Run .NET shared harness & persistence provider tests
dotnet test samples/workers/shared/OpenConnect.Samples.Workers.TestKit/OpenConnect.Samples.Workers.TestKit.csproj -c Release
```

## Teardown & Evidence Rules

- Ordinary teardown uses `docker compose down --remove-orphans` — **never** `down -v` or `--volumes` (destructive flags are rejected with exit code 2).
- Pass `--retain-on-fail` to preserve the isolated `ocw-e2e-<worker>` project containers when debugging a failing integration run.
- Evidence bundles in `samples/workers/shared/evidence/<run-id>/` emit `result.json` (`openconnect.samples.workers.evidence/v1`) and `summary.md` with initial status `NOT_RUN` where unexecuted and `PASS` only where observed.
