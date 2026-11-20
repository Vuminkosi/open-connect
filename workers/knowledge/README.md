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
private worker endpoint, model-init service, indexer, reranker, deployment
profile, or SDK integration is provided yet. Do not advertise `knowledge.embed`
as available until those integrations and their security checks are complete.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` from this
directory. Model assets must be provisioned separately through the shared
initializer; request processing performs no downloads.
