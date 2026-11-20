# Vision worker

The current component is an offline CPU image-classification adapter. It accepts
bounded single-frame PNG or JPEG inputs, applies a versioned ImageNet center
crop and normalization, and returns ranked class labels with model fingerprint
and taxonomy version. Scores are softmax outputs, not calibrated probabilities
or evidence for medical, financial, identity or damage decisions.

The caller must first authorize the tenant and document operation, apply host
privacy policy, malware-screen the original image with ClamAV, and run image
decoding and inference in an isolated resource-limited sandbox. This adapter
does not itself establish those boundaries or provide a network API, durable
storage or object handles. No `vision.analyze` capability is advertised yet.
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
