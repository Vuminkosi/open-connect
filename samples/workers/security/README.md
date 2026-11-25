# Security Worker — End-to-End Sample (`samples/workers/security/`)

## Overview

End-to-end sample, OWASP CRS WAF harness, and release acceptance gate for the OpenConnect **Security** worker (`WSE2E-AT-001–008`, `FW-010–FW-110`, `AT-01–AT-17`).

- **Production Dockerfile & Compose:** `workers/security/Dockerfile`, `workers/security/compose.yaml`
- **Canonical Image Repository:** `vuminkosimatibe/openconnect-worker-security` (built locally as `vuminkosimatibe/openconnect-worker-security:e2e-local` during PR/CI runs)
- **Named Persistent Volumes:** `oc_security_models` (model cache), `oc_clamav_db` (ClamAV signature database), `oc_clamav_run` (internal ClamAV Unix socket)
- **Security Boundary:** The Python worker supplies untrusted detection observations only; the trusted .NET host owns identity, tenant authorization, AES-GCM vault keys, flattened PNG redaction, service fencing, and final enforcement.

## Third-Party Infrastructure Provenance & Licenses (FW-020 / FW-060)

| Service | Pinned Image | License | Purpose & Isolation |
|---|---|---|---|
| `clamav` (`--profile malware`) | `clamav/clamav-debian:1.4` | GPL-2.0 | Private malware scanning daemon over `oc-test-private` / Unix socket. No public port. |
| `waf` (`--profile waf`) | `owasp/modsecurity-crs:4.11-nginx-alpine` | Apache-2.0 | OWASP CRS 4.11 reverse proxy in blocking mode (`MODSEC_RULE_ENGINE=On`, `PARANOIA=1`), content-free JSON audit log (`MODSEC_AUDIT_LOG_PARTS=AZ`), SignalR/Blazor WebSocket preservation (`/_blazor`). |
| `waf-backend` (`--profile waf`) | `python:3.12-alpine` | PSF-2.0 | Private demo route group (`/api/waf-demo/echo`, `/api/waf-demo/stats`, `/_blazor/negotiate`) attached **only** to `oc-test-private` with no published host ports (zero direct bypass). |

## Build, Compose & Acceptance Invocation

```bash
# 1. Run offline contract, policy-negative, WAF config, evidence, and 12-scenario cache suite (AT-01..AT-17)
python3 samples/workers/security/tests/test_e2e_acceptance.py

# 2. Validate Compose configuration (T0 static)
sh samples/workers/shared/scripts/run-one.sh --worker security --tier t0

# 3. Start Security Worker + OWASP CRS WAF + private backend (isolated project)
docker compose -p oc-security-hackathon -f samples/workers/security/compose.e2e.yaml --profile waf up -d --build

# 4. (Optional) Also start private ClamAV malware daemon
docker compose -p oc-security-hackathon -f samples/workers/security/compose.e2e.yaml --profile waf --profile malware up -d

# 5. Run live acceptance checks against running WAF & Security Worker
OC_LIVE_E2E=1 OC_WAF_BASE_URL=http://127.0.0.1:8088 python3 samples/workers/security/tests/test_e2e_acceptance.py

# 6. Safe teardown (NEVER use `down -v` — preserves oc_security_models and oc_clamav_db)
docker compose -p oc-security-hackathon -f samples/workers/security/compose.e2e.yaml down --remove-orphans
```

## Connecting `OpenConnect.Docs` Security Playground

The authenticated Blazor **Security Playground** (`/cloud/tenants/{TenantId}/security/playground`, linked from `TenantSecurity.razor`) connects to real configured engines and reports `not_configured` or `unavailable` honestly when an engine is absent:

```json
{
  "OpenConnect": {
    "SecurityWorker": {
      "Endpoint": "http://127.0.0.1:8080",
      "BearerToken": "<set-via-secret-store-or-env>",
      "RequireAuthenticatedTransport": true,
      "AllowInsecureLoopbackHttpInDevelopment": true
    },
    "SecurityPlayground": {
      "WafDemoBaseUrl": "http://127.0.0.1:8088",
      "ApprovedServiceBaseUrl": "http://127.0.0.1:8088"
    }
  }
}
```

## Environment Variables & Secrets Configuration

| Variable | Default (E2E) | Description |
|---|---|---|
| `PORT_SECURITY` | `8080` | Loopback-bound host port mapped to Security Worker `:8080` |
| `PORT_WAF` | `8088` | Loopback-bound host port mapped to OWASP CRS WAF proxy `:8080` |
| `OPENCONNECT_SCAN_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Private bearer token authenticating host-to-worker requests |
| `OC_WORKER_ENGINE` | `real` | Engine mode (`real` for production/demo) |
| `OC_MODEL_DEVICE` | `cpu` | Device selector (`cpu`, `auto`, `cuda`) |
| `OC_MODEL_PROVISIONING` | `first-start-if-missing` | Startup model provisioning policy |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_security_models` volume |
| `OC_MODEL_OFFLINE` | `true` | Set `false` for first-start allowlisted model provisioning; `true` for offline verified startup |
| `OC_SECURITY_RULES_ENABLED` | `true` | Deterministic CPU rules detector (zero weights) |
| `OC_SECURITY_GLINER_ENABLED` | `false` | Optional semantic PII detector (`urchade/gliner_small-v2.1`) |
| `OC_SECURITY_DEBERTA_ENABLED` | `false` | Optional prompt-injection detector (`protectai/deberta-v3-base-prompt-injection-v2`) |
| `OC_SECURITY_PADDLEOCR_ENABLED` | `false` | Optional local OCR detector (`PaddlePaddle/PP-OCRv5_mobile`) |
| `OC_SECURITY_YUNET_ENABLED` | `false` | Optional face-region detector (`opencv/face_detection_yunet`) |
| `OC_SECURITY_MALWARE_ENABLED` | `false` | Optional ClamAV malware scanning via `OC_SECURITY_CLAMAV_SOCKET` |

## Fixtures (`fixtures/`)

- `request.json` / `expected.json`: Legacy `/scan` contract parity fixture (AT-01).
- `rules-pii.json` & `unicode-normalization.json`: Synthetic PII and Unicode scalar offset alignment (AT-04).
- `overlap-priority.json`: Deterministic overlap resolution (`block > redact > mask > review > allow`).
- `prompt-injection.json`: Benign vs synthetic prompt-injection fixtures requiring fail-closed handling (AT-08).
- `malicious-detector-output.json`: Compromised worker responses rejected by host schema/policy (AT-06).
- `face-and-ocr.png` & `synthetic-customer-card.png`: Synthetic PNG fixtures with face-like/text-like regions and EXIF/tEXt canary chunk for flattened raster redaction verification (AT-05).
- `unsupported-image.bin`: Malformed binary rejected before image decode (AT-06).
- `clean-sample.txt`: Clean synthetic file fixture for malware gate verification; harmless EICAR test string is assembled dynamically in test code (AT-07).
- `waf-cases.json`: Fixed benign, CRS SQLi/XSS block, and Blazor SignalR negotiate cases (AT-09).
- `service-fencing-cases.json`: Approved service vs denied SSRF/metadata/userinfo/cross-tenant destinations (AT-10).
- `evidence-isolation-cases.json`: `openconnect.security-evidence.v1` valid event + rejected raw PII/forged tenantId cases (AT-13).
- `forged-token.json` & `policy-coverage.yaml`: Cross-tenant vault token negatives and declarative coverage matrix.

## Acceptance Matrix Coverage (AT-01 – AT-17)

| ID | Scenario | Automated Coverage |
|---|---|---|
| AT-01 | Legacy `/scan` + SDK registrations | `test_wse2e_at_001_rules_only_starts_with_zero_weights` |
| AT-02 | Worker auth & truthful readiness | `test_wse2e_at_005_optional_detectors_report_truthful_readiness` |
| AT-03 | Runtime model cache (12 scenarios) | `test_wse2e_at_008_runtime_model_cache_12_scenarios` |
| AT-04 | Rules + Unicode offsets + semantic PII | `test_wse2e_at_003_rules_pii_unicode_offsets_and_encrypted_vault` |
| AT-05 | Image privacy & metadata scrubbing | `test_wse2e_at_007_vault_tenant_isolation_and_image_pixel_exif_redaction` |
| AT-06 | Invalid image / missing coverage fail-closed | `test_wse2e_at_004_missing_semantic_or_image_detector_fails_closed`, `test_wse2e_at_006_malicious_detector_output_and_overlap_priority` |
| AT-07 | Malware clean / EICAR / digest binding | `test_at_07_malware_fixtures_and_digest_binding` |
| AT-08 | Prompt injection fail-closed | `test_wse2e_at_002_fail_closed_blocks_external_sink_zero_bytes` |
| AT-09 | OWASP CRS WAF blocking & Blazor preservation | `test_at_09_waf_crs_configuration_and_private_network_isolation`, `test_live_waf_and_worker_acceptance` |
| AT-10 | Service fencing allow/deny/SSRF/tenant | `test_at_10_service_fencing_destination_policy` |
| AT-13 | Evidence v1 schema & tenant isolation | `test_at_13_evidence_v1_schema_and_isolation_contract` |
| AT-15/17 | Playground UI & Compose volume preservation | `test_at_15_and_17_playground_ui_and_deployment_invariants` |
