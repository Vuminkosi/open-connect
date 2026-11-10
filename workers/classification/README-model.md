# Classification worker: model setup

The worker NEVER downloads models (build time or request time;
`HF_HUB_OFFLINE=1` is enforced). Populate the named volume
`oc_classification_models` out of band:

1. Copy a pinned checkpoint into the volume:
   - `/models/manifest.json` with `modelId`, `revision`, `checksum`,
     `license` (e.g. `apache-2.0`, `mit`, `bsd-3-clause` need no extra
     acceptance; anything else requires `LICENSE_ACCEPTED=1`).
   - `/models/<weights>.bin` or `/models/<weights>.safetensors`.
2. `GET /health/ready` reports `ready:true` only when both are present and
   the license is satisfied; otherwise `unready` with `reason`
   `model_unavailable` or `license_denied`.
3. CI/contract tests run with `OPENCONNECT_FAKE_ENGINE=1` (deterministic
   keyword stub tagged `engine:"fake"` — explicitly non-production).
