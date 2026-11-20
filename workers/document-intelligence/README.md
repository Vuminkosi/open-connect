# Document intelligence

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

There is no document worker API or deployment image yet. A PDF page renderer
runs in a separate process with byte, page, pixel, output and time limits; it
must run in an OS sandbox with network access denied. The PDF-to-OCR pipeline,
multipage TIFF,
tables, structured fields, indexing, multilingual profiles, and document-level
partial failure are not implemented. Do not advertise `documents.analyze`
until those integrations and their security checks are complete.

Run `uv sync --locked`, `uv run ruff check .` and `uv run pytest -q` from this
directory. An optional compatible language-data candidate is maintained by
[Tesseract](https://github.com/tesseract-ocr/tessdata_fast); each deployment
must pin and approve its exact revision, digest, size, and license and must
provision it during initialization, not during image build or request handling.
