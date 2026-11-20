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
indexer, reranker, or SDK integration is provided yet. The private endpoint
advertises `knowledge.embed` only after its configured model passes cache and
startup inference checks; production
deployment still requires a reviewed model and end-to-end acceptance.

`chunk_pages` is a deterministic, in-memory preparation primitive. It returns
bounded page-local text spans and content hashes, with chunk identities scoped
to the tenant, application, collection, document revision, embedding space and
chunking configuration. It neither authorizes access nor persists text. A host
must authorize and sanitize content before chunking; durable indexing, ACL
prefiltering, deletion propagation and search are not implemented by this
primitive. Do not advertise `knowledge.index` based on chunking alone.
`prepare_document` can embed these chunks through the existing passage engine
in bounded batches and rejects truncation, invalid vectors and model-space
changes. Its in-memory result is sensitive application data and is not a
durable index or an authorization boundary.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` from this
directory. Model assets must be provisioned separately through the shared
initializer; request processing performs no downloads.

The optional `OC_RUN_REAL_MODEL_TESTS=1 uv run pytest -q
tests/test_real_model_acceptance.py` test downloads pinned upstream MiniLM
assets into pytest's temporary directory and verifies offline restart and a
small similarity example. Run it only where the model license and download
are approved. Its resource budgets are test fixtures, not deployment sizing
or a quality benchmark.

The root Compose file offers the opt-in `knowledge` profile. Set
`OC_KNOWLEDGE_INIT_CONFIG` and `OC_KNOWLEDGE_SERVE_CONFIG` to paths for the
reviewed initialization and serving configuration files. The default files
are intentionally empty and fail closed. The init service writes the named
`oc_knowledge_models` volume; the serving service mounts it read-only and
uses offline settings. Both run as non-root and expose no host port. Normal
Compose lifecycle commands do not remove named volumes. Do not use volume
removal when the cached model revisions must be retained.

Set `OC_KNOWLEDGE_ENABLED=true` and `OC_KNOWLEDGE_CONFIG_PATH` to an
administrator-controlled JSON file containing a runtime `manifest` and a
`profile` with `modelAsset`, `tokenizerAsset`, `dimension`, `maxTokens`, and
optional `prefixStyle` (`none` or `e5`) and `padToken`. The optional
`licenseAccepted` and `commercialUse` fields govern license verification.
`OC_MODEL_CACHE_DIR` points at the verified cache. The server does not run
the initializer and does not download model files. Fake engines are rejected.
The required admission settings are described in [MODEL-CONFIG.md](MODEL-CONFIG.md).

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
