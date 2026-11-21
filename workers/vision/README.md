# Vision worker

The current component is an offline CPU image-classification adapter. It accepts
bounded single-frame PNG or JPEG inputs, applies a versioned ImageNet center
crop and normalization, and returns ranked class labels with model fingerprint
and taxonomy version. Scores are softmax outputs, not calibrated probabilities
or evidence for medical, financial, identity or damage decisions.

The trusted host must authorize the tenant and document operation and apply
host privacy policy. The private `vision.analyze` API scans original bytes with
ClamAV before image decoding and requires an isolated resource-limited sandbox;
it does not provide durable storage or object handles. The service advertises
the capability only after scanner, sandbox and classifier readiness checks pass.
The model and taxonomy must be approved and provisioned to the verified runtime
cache before use; the adapter makes no downloads.

The ONNX Model Zoo MobileNet v2-12 classification model is a candidate, with
upstream model code and the model card describing Apache 2.0 licensing. The
`mobilenet_initializer` profile pins its model and labels to a single upstream
revision and file-level SHA-256 digests. The profile requires explicit resource
budgets and approved download hosts, and never downloads during construction. Review
the taxonomy and all artifact licensing independently before deployment.
The adapter requires explicit `taxonomy_approved=True` from the operator; the
model manifest's license does not itself approve the taxonomy.
The model and labels, preprocessing, immutable revision and file digests must
be pinned together. A model trained for general ImageNet classes is not a
domain-specific detector and does not establish application accuracy.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` here.

The root Compose `vision` profile provides model initialization, ClamAV and the
isolated worker. Configure `OC_VISION_INIT_CONFIG` and `OC_VISION_SERVE_CONFIG`
with approved manifests and budgets. The serving configuration includes
`modelAsset`, `labelsAsset`, `taxonomyVersion`, `taxonomyApproved`, and the
ClamAV socket path `/run/clamav/clamd.sock`. The defaults are intentionally
unconfigured. Bearer authentication and an HMAC signature over exact request
bytes are required. The trusted host must authorize the signed tenant and apply
privacy policy; the worker does not independently authenticate end users.
No durable idempotency or replay protection is provided. Inference runs in a
bounded single-slot process; a running ONNX call cannot be forcibly cancelled.

The separate opt-in `vision-embed` profile runs a paired image and text encoder
on a private Unix socket, `/run/openconnect/vision-embed.sock`. Set
`OC_VISION_EMBED_INIT_CONFIG` and `OC_VISION_EMBED_SERVE_CONFIG` to reviewed
configuration files. The serving configuration contains the pinned manifest,
resource request and budget, `clamSocket`, `artifactLicenseReviewed: true` and
`projectionApproved: true`. Defaults fail closed. Review the exact artifacts,
license and projection quality before setting either approval. The candidate
profile pins both ONNX encoders and their tokenizer and preprocessing files;
startup verifies the cache and probes both encoders without downloading.

The signed `vision.embed` request accepts either a bounded image or bounded
text, using `payload.modality` of `image` or `text`. Images are scanned before
inference. The response contains a normalized vector and model fingerprint,
dimension and space ID; compare vectors only when those identities match. The
trusted host must authorize the tenant, apply privacy policy and control vector
export before signing. A worker signature is not end-user authorization. The
service has no durable idempotency or replay protection, and a running ONNX
call cannot be forcibly cancelled. Artifact-level licensing, real-model
cross-modal retrieval quality and integration with an ACL-protected index must
be verified for each deployment.

For an already provisioned test cache, set `OC_RUN_CLIP_REAL_TEST=1` and
`OC_CLIP_TEST_CACHE` to the cache directory, then run
`uv run pytest tests/test_clip_real_acceptance.py -q`. The test prevents DNS
lookups, checks normalized output and offline reload, and compares a red image
with two short text prompts. This small fixture is a runtime smoke test, not a
representative retrieval benchmark or artifact license approval.
