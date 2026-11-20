# Document intelligence

The current component is a single-page English OCR adapter for admitted PNG
and JPEG images. It reuses the shared verified model cache and runs a configured
local Tesseract executable with the pinned `eng.traineddata` asset. It returns
word text, normalized page-frame boxes, confidence, source spans and model
fingerprint. OCR input and output are streamed through the child process;
the adapter does not intentionally retain document content on disk.

The consumer must supply a reviewed, provisioned manifest and executable,
and must run the component in an isolated, resource-limited environment. This
component does not itself provide a malware scan or an operating-system
sandbox. It rejects unrecognized formats and oversized images. English OCR
accuracy depends on document quality and has not been established by a labeled
quality evaluation.

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
