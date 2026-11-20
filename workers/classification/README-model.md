# Classification worker: model setup

The worker NEVER downloads models (build time or request time;
`HF_HUB_OFFLINE=1` is enforced). Populate the named volume
`oc_classification_models` out of band:

1. Copy a pinned checkpoint into the volume:
   - `/models/manifest.json` with `modelId`, `revision`, `checksum`,
     `license` (e.g. `apache-2.0`, `mit`, `bsd-3-clause` need no extra
     acceptance; anything else requires `LICENSE_ACCEPTED=1`).
   - `/models/<weights>.bin` or `/models/<weights>.safetensors`.
2. A valid manifest and weights establish asset availability only. The real
   classification inference runtime is not implemented, so `GET /health/ready`
   remains unready with `inference_runtime_unavailable` and does not advertise
   a real capability. Missing assets or an unaccepted license report their
   respective readiness reason.
3. CI/contract tests run with `OPENCONNECT_FAKE_ENGINE=1` (deterministic
   keyword stub tagged `engine:"fake"` — explicitly non-production).
