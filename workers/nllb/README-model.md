# NLLB worker: model setup

The worker NEVER downloads models (build time or request time;
`HF_HUB_OFFLINE=1` is enforced) and has NO remote fallback. Populate the
named volume `oc_nllb_models` out of band:

1. Copy a pinned NLLB checkpoint into the volume:
   - `/models/manifest.json` with `modelId`, `revision`, `checksum`,
     `tokenizerHash` (sha256 of the tokenizer file), `license`.
   - `/models/<weights>.bin` or `/models/<weights>.safetensors`.
   - `/models/tokenizer.json` (or `sentencepiece.bpe.model`) whose sha256
     MUST equal `tokenizerHash`; a mismatch keeps readiness `unready` with
     `reason: tokenizer_mismatch`.
2. NLLB checkpoints are non-permissively licensed: set `LICENSE_ACCEPTED=1`
   after operator license review, otherwise readiness stays `unready` with
   `reason: license_denied` and execute returns `license_denied`.
3. `GET /health/ready` reports `ready:true` only when manifest + weights +
   tokenizer hash + license (+ pinned neural decoder, when the ML runtime
   lands) are all satisfied.
4. CI/contract tests run with `OPENCONNECT_FAKE_ENGINE=1` (deterministic
   stub tagged `engine:"fake"` — explicitly non-production).
