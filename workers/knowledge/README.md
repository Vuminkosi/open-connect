# Knowledge worker

The current component is an offline CPU ONNX text embedding adapter. It loads
only assets declared in a verified `openconnect-worker-common` cache revision,
uses attention-mask mean pooling and L2 normalization, and identifies the
embedding space by model fingerprint and preprocessing profile. E5 profiles
apply the appropriate `query:` or `passage:` prefix. The adapter enforces
batch, text and token limits and reports truncation per input. ONNX external
tensor data is currently unsupported and rejected.

Tests use a synthetic ONNX graph and tokenizer. They verify runtime mechanics,
not semantic quality. No production model manifest, model quality acceptance,
model-init service, indexer, reranker, deployment profile, or SDK integration
is provided yet. The private endpoint advertises `knowledge.embed` only after
its configured model passes cache and startup inference checks; production
deployment still requires a reviewed model and end-to-end acceptance.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` from this
directory. Model assets must be provisioned separately through the shared
initializer; request processing performs no downloads.

Set `OC_KNOWLEDGE_ENABLED=true` and `OC_KNOWLEDGE_CONFIG_PATH` to an
administrator-controlled JSON file containing a runtime `manifest` and a
`profile` with `modelAsset`, `tokenizerAsset`, `dimension`, `maxTokens`, and
optional `prefixStyle` (`none` or `e5`) and `padToken`. The optional
`licenseAccepted` and `commercialUse` fields govern license verification.
`OC_MODEL_CACHE_DIR` points at the verified cache. The server does not run
the initializer and does not download model files. Fake engines are rejected.

The private API requires `OPENCONNECT_WORKER_TOKEN` and a hex-encoded
`OPENCONNECT_WORKER_SIGNING_KEY` of at least 32 bytes. Execute requests need
the bearer token and a lowercase `X-OpenConnect-Signature` HMAC-SHA256 over
the exact UTF-8 JSON body. The host must authenticate the user, authorize the
tenant and privacy policy, and sign the request; the signature binds the
claimed tenant but does not independently authorize it. Requests use
`openconnect.v1`, `taskId`, `kind` (`knowledge.embed`), `tenantId`,
`issuedAt`, `expiresAt`, and `payload` containing `inputKind` and `texts`.
The validity window is at most five minutes. This synchronous endpoint is
stateless and does not implement durable idempotency or replay protection.
