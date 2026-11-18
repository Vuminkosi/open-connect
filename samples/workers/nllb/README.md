# OpenConnect E2E Sample — `nllb` (`workers/nllb`)

Standalone end-to-end sample and acceptance test suite for the Python NLLB translation worker (`workers/nllb`), covering `WSE2E-AT-015` through `WSE2E-AT-020` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/nllb/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-nllb:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/nllb/compose.e2e.yaml up -d
sh samples/workers/nllb/scripts/smoke.sh
sh samples/workers/nllb/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Shared bearer token (`Authorization: Bearer ...`) |
| `NLLB_FAKE_ENGINE` | `true` | Explicit fake engine mode (`Development`/`Test` only) |
| `NLLB_MODEL_DIR` | `/models` | Path to mounted `oc_nllb_models` volume |
| `NLLB_MODEL_LICENSE_ACCEPTED` | `false` | Must be `true` to load CC-BY-NC-4.0 NLLB weights |
| `NLLB_MAX_TEXT_CHARS` | `10000` | Maximum input character count (`413 input_too_long` above limit) |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_OFFLINE` | `true` | Blocks outbound checkpoint/tokenizer downloads |
| `PORT_NLLB` | `8082` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Model Manifest / Licensing Status

- **Capabilities (`/internal/v1/capabilities`)**: Reports `engine`, `modelRevision`, `tokenizerHash`, and `languagePairs` (132 directional pairs across 12 supported BCP-47/FLORES-200 codes including `eng_Latn`, `fra_Latn`, `spa_Latn`, `deu_Latn`, `por_Latn`, `zul_Latn`, `xho_Latn`, `sna_Latn`, `swh_Latn`, `ara_Arab`, `zho_Hans`, `hin_Deva`).
- **Model License Gate**: NLLB-200 checkpoints carry `CC-BY-NC-4.0` (`commercial_use_allowed: false`). Readiness fails closed with `license_denied` unless `NLLB_MODEL_LICENSE_ACCEPTED=true` and commercial mode is disabled.
- **No Remote Fallback**: Missing local models or `inputRef` payloads never fall back to external translation APIs (`WSE2E-AT-020`).

## 5. Offline Test Procedure

```sh
sh samples/workers/nllb/tests/t1_contract.sh
sh samples/workers/nllb/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Unsupported language pair (`WSE2E-AT-016`)**: POST `fixtures/unsupported-pair.json` -> HTTP `422` (`language_unsupported`).
- **Tokenizer hash mismatch (`WSE2E-AT-017`)**: Corrupt `tokenizer.model` in `/models` -> `/health/ready` returns `{"ready": false, "reason": "tokenizer_hash_mismatch"}`.
- **Missing license acceptance (`WSE2E-AT-018`)**: Set `NLLB_MODEL_LICENSE_ACCEPTED=false` with `NLLB_FAKE_ENGINE=false` -> `license_denied`.
- **Prompt/tool injection (`WSE2E-AT-019`)**: POST `fixtures/injected-tool-command.json` -> treated strictly as literal translation input data.

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/nllb/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_nllb_models volume):
docker compose -f samples/workers/nllb/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_nllb_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `NLLB_FAKE_ENGINE=false`, `OC_MODEL_PROVISIONING=preprovisioned-only`, explicit `NLLB_MODEL_LICENSE_ACCEPTED=true` (for non-commercial deployments), and mTLS/private network ingress from the .NET gateway.
