# NLLB worker: model setup

The worker never downloads models and has no remote fallback. Provision a
versioned, active bundle into the model volume out of band.

1. The initial decoder layout requires declared, hash-verified
   `model.safetensors`, `config.json`, `tokenizer.json`, and
   `tokenizer_config.json`. Optional configuration files must also be declared;
   extra or linked files are rejected. Set `OC_NLLB_MODEL_ID` to the model ID.
2. Install the optional pinned runtime with `uv sync --locked --extra local-decoder`,
   or build the container with `OC_NLLB_INSTALL_DECODER=true`. These install
   software dependencies; model assets are supplied separately.
3. Review the checkpoint license. `LICENSE_ACCEPTED=1` records acceptance for
   licenses requiring it; `OC_NLLB_COMMERCIAL_USE` defaults to `true` and rejects
   non-commercial licenses even when accepted. Set it to `false` only for a
   permitted non-commercial deployment.
4. Set `HF_HUB_OFFLINE=1` for a host process. Real readiness requires the
   active bundle, license, pinned runtime and a successful local decoder smoke
   check. The decoder currently runs on CPU;
   an explicit CUDA request fails closed. Flat bundles remain inspectable by
   the manifest helper but are not eligible for decoder execution.
5. CI/contract tests may use `OPENCONNECT_FAKE_ENGINE=1` in a non-production
   environment. This deterministic stub identifies itself as `engine:"fake"`.

The synthetic runtime test uses random model weights to exercise local loading,
generation and the worker HTTP path. Real checkpoint compatibility, translation quality,
the Cloud translation route and deployment acceptance require separate verification.
