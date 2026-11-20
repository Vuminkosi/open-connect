# Document intelligence

The optional `documents.fields` capability runs the same malware-screened OCR
pipeline and returns literal `label: value` candidates from recognized lines.
Candidates include page-relative Unicode code-point offsets and indexes into
the returned OCR word spans. Their minimum OCR confidence is not a confidence
in the meaning or correctness of the field. Values are not semantically
validated; downstream applications must review and validate them. The result
reports `field_limit_reached` when more than 1,000 candidates are present and
`field_candidates_skipped` when an oversized or uncited candidate is omitted.

The current component is a single-page English OCR adapter for admitted PNG
and JPEG images. It reuses the shared verified model cache and runs a configured
local Tesseract executable with the pinned `eng.traineddata` asset. It returns
word text, normalized page-frame boxes, confidence, source spans and model
fingerprint. OCR input and output are streamed through the child process;
the adapter does not intentionally retain document content on disk.

The consumer must supply a reviewed, provisioned manifest and executable,
and must run the component in an isolated, resource-limited environment.
`DocumentAnalyzer` requires a clean ClamAV scan of the original bytes before
PDF parsing or OCR; detections, scan failures and unavailable scanners fail
closed. It rejects unrecognized formats and oversized images. English OCR
accuracy depends on document quality and has not been established by a labeled
quality evaluation.

The ClamAV integration uses `clamd`'s `INSTREAM` protocol through an
administrator-controlled Unix socket. Restrict access to that socket and
maintain the signature database as part of deployment. The adapter does not
manage signature updates, validate database freshness, or provide an OS
sandbox. Clamd TCP is unauthenticated and is not used by this adapter.
The daemon must be configured to report encrypted or scan-limit-exceeded
content as an alert and accept streams at least as large as the document input
limit. The client cannot verify those daemon settings; deployment verification
must test them and signature freshness before accepting documents.

For explicit document retention, `MinioDocumentStore` uses a configured MinIO
client and private bucket. It scans the original bytes before upload, generates
an opaque document ID, derives a separate object prefix from the trusted tenant
ID, and verifies size and SHA-256 on retrieval. The host must authenticate and
authorize the tenant and document operation before calling the store and must
protect the returned reference and digest against tampering. A tenant prefix is
not a substitute for document ACLs. Configure protected transport, bucket
access policy, encryption, retention and credential rotation in the MinIO
deployment; the adapter does not provision them.

Deletion removes the referenced object version. Other versions and related
chunks, indexes, caches and application records require their own lifecycle
handling. Synchronous processing does not implicitly retain documents.

The private API exposes `documents.ocr`, not the broader `documents.analyze`
capability. It requires a bearer token and a short-lived request signed with
HMAC-SHA256 over its exact JSON bytes, matching the other private workers. The
trusted host must authenticate and authorize the tenant and apply privacy
policy before signing. Requests include `schemaVersion`, `taskId`, `kind`,
`tenantId`, `issuedAt`, `expiresAt`, and `payload.contentBase64`; responses
contain document text and must be protected as application data. The service
does not provide replay protection or durable task semantics.

The service is disabled by default. It requires a verified model, a responding
ClamAV socket, and a restricted Linux environment: non-root, no-new-privileges,
seccomp filter, a read-only root filesystem, loopback-only networking, and
bounded cgroup memory and process counts. These checks establish minimum
conditions, not a complete sandbox attestation; operators must enforce the
actual sandbox and scanner configuration. There is no deployment image yet.
The PDF-to-OCR pipeline does not yet provide multipage TIFF, tables, structured
fields, indexing, multilingual profiles or document-level partial failure.
Do not advertise `documents.analyze` until those integrations and their
security checks are complete.

Set `OC_DOCUMENT_ENABLED=true` and `OC_DOCUMENT_CONFIG_PATH` to a protected
operator-controlled JSON file containing the shared `manifest`,
`resourceRequest` and `resourceBudget`, plus absolute `tesseractPath` and
`clamSocket` values. Optional `licenseAccepted` and `commercialUse` fields
control license verification. Configure `OC_MODEL_CACHE_DIR`,
`OPENCONNECT_WORKER_TOKEN` and a hex-encoded `OPENCONNECT_WORKER_SIGNING_KEY`
of at least 32 bytes. The resource values are admission inputs, not measured
host availability; enforce the actual limits in the deployment. When disabled,
the service does not load a model or advertise a capability.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` from this
directory. An optional compatible language-data candidate is maintained by
[Tesseract](https://github.com/tesseract-ocr/tessdata_fast); each deployment
must pin and approve its exact revision, digest, size, and license and must
provision it during initialization, not during image build or request handling.
