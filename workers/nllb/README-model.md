# NLLB worker: model setup

The worker NEVER downloads models (build time or request time;
`HF_HUB_OFFLINE=1` is enforced) and has NO remote fallback. Populate the
named volume `oc_nllb_models` out of band:

1. Copy a pinned NLLB checkpoint into the volume:
   - `/models/manifest.json` with `modelId`, `revision`, `checksum` (SHA-256
     of the weights file), `tokenizerHash` (SHA-256 of the tokenizer file),
     and `license`.
   - `/models/<weights>.bin` or `/models/<weights>.safetensors`.
   - `/models/tokenizer.json` (or `sentencepiece.bpe.model`) whose sha256
     MUST equal `tokenizerHash`; a mismatch keeps readiness `unready` with
     `reason: tokenizer_mismatch`.
2. NLLB checkpoints are non-permissively licensed: set `LICENSE_ACCEPTED=1`
   after operator license review, otherwise readiness stays `unready` with
   `reason: license_denied` and execute returns `license_denied`.
3. The real decoder is not implemented yet. Even when model files, hashes and
   license checks pass, `GET /health/ready` remains unready and the worker does
   not advertise real translation capability. Installing neural libraries
   alone does not enable translation.
4. CI/contract tests run with `OPENCONNECT_FAKE_ENGINE=1` (deterministic
   stub tagged `engine:"fake"` — explicitly non-production).
