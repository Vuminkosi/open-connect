# all-workers — Aggregate E2E Compose & Verification Stack (§6.2, `WSE2E-IMP-023`)

Runs and orchestrates all 11 OpenConnect canonical workers side-by-side with independent Docker Compose profiles, distinct model volume bindings, and private network isolation.

---

## 1. Stack Composition & Profile Matrix

Every worker service is configured with canonical image names `vuminkosimatibe/openconnect-worker-<name>:${OC_IMAGE_TAG:-e2e-local}` and independent Compose profiles:

| Worker | Role | Model Volume | Compose Profiles | Port |
|---|---|---|---|---|
| `security` | Rule & regex firewall, redaction | `oc_security_models` | `["security", "all", "model-workers"]` | `8080` |
| `classification` | Safety & content classifier | `oc_classification_models` | `["classification", "all", "model-workers"]` | `8081` |
| `nllb` | Neural translation | `oc_nllb_models` | `["nllb", "all", "model-workers"]` | `8082` |
| `inference` | Local small-model reasoning | `oc_inference_models` | `["inference", "all", "model-workers"]` | `8083` |
| `transcription` | Audio transcription (Whisper) | `oc_transcription_models` | `["transcription", "all", "model-workers"]` | `8084` |
| `realtime-voice` | Low-latency voice & VAD | `oc_realtime_voice_models` | `["realtime-voice", "all", "model-workers"]` | `8085` |
| `kokoro` | TTS audio synthesizer | `oc_kokoro_models` | `["kokoro", "all", "model-workers"]` | `8086` |
| `mms-tts` | Multilingual speech synthesizer | `oc_mms_models` | `["mms-tts", "all", "model-workers"]` | `8087` |
| `navigator-playwright` | Sandboxed browser automation | **None** | `["navigator-playwright", "all", "ops-workers"]` | `8088` |
| `operational-agents` | Operational tour supervisor | **None** | `["operational-agents", "all", "ops-workers"]` | `8089` |
| `outbox` | Relational outbox JetStream relay | **None** | `["outbox", "all", "ops-workers"]` | `8090` |

---

## 2. Invariants & Isolation Guarantees

1. **Volume Separation**: Only the 8 Python workers define and mount named model volumes. Non-model workers (`navigator-playwright`, `operational-agents`, `outbox`) define no model volumes and perform no weight caching (`WSE2E-IMP-023`).
2. **Network Isolation**: All services join `oc-runtime-private` with `internal: true`. Outbox and operational workers communicate only with their defined relational and transport backends.
3. **Exclusivity Enforcement**: Standalone operational and outbox dispatchers run with their respective embedded services disabled (`EmbeddedEnabled=false`, `EmbeddedDispatcherEnabled=false`).
4. **Independent Profile Drill**: Any single worker or sub-group can be inspected and run independently without starting the entire cluster:
   ```bash
   sh samples/workers/all-workers/scripts/run-profile-drill.sh security
   sh samples/workers/all-workers/scripts/run-profile-drill.sh ops-workers
   ```

---

## 3. Test & Verification Commands

```bash
# T0 static validation & T1 topology/isolation checks
sh samples/workers/all-workers/tests/t1_contract.sh

# Smoke check
sh samples/workers/all-workers/scripts/smoke.sh

# T2 bring-up of all workers (requires Docker daemon)
COMPOSE_PROJECT_NAME=ocw-e2e-all docker compose -f samples/workers/all-workers/compose.e2e.yaml --profile all up -d --build

# Safe teardown (never 'down -v')
COMPOSE_PROJECT_NAME=ocw-e2e-all docker compose -f samples/workers/all-workers/compose.e2e.yaml down --remove-orphans
```
