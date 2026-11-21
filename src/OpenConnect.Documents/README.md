# Document worker client

`DocumentWorkerClient` sends `documents.ocr`, `documents.fields`, `documents.layout`, `documents.tables`, and opt-in `documents.analyze` requests to
an explicitly configured Unix socket. It requires a host-provided authorization
callback, evaluates the document modality gate, signs the exact tenant-bound
request, and bounds request and response sizes. A 130-second deadline covers
both worker response headers and body reads, in addition to caller cancellation.
Owned request and temporary response byte buffers are cleared after the call;
managed strings and the returned JSON remain subject to normal runtime lifetime.
The host must authorize the
principal for the tenant, document bytes, and operation; protect the socket from
untrusted proxies and use the same configured privacy options as its firewall.

The returned `JsonDocument` contains sensitive application data and must be
disposed by the caller. The client checks the response envelope, result shape,
limits and OCR source references; applications must review extracted candidates
and apply output privacy policy before further disclosure.
Worker sandbox and ClamAV configuration remain deployment responsibilities.
Layout results group OCR words into source-cited line regions; they are not
table detection or a semantic reading-order model.
Table results are heuristic aligned-row candidates with source-cited cells;
applications must review them and cannot assume they include merged or empty cells.
Combined analysis includes per-page and per-component status; applications must
handle partial results and cannot treat missing pages as complete extraction.

`ExtractionWorkerClient` targets the inference worker's `documents.extract`
route through a protected Unix socket or trusted HTTPS endpoint. Plain HTTP
requires explicit opt-in and a protected network. The host authorizes the tenant, source text and
schema, and the privacy firewall must permit both without transformation. The
client signs the request, bounds its lifetime and validates response fields and
Unicode source citations. Returned candidates require application review;
source citation alone does not establish semantic correctness. The inference
worker must have an approved local extraction provider installed and ready.

`DocumentExtractionWorkflow` composes the two clients: it sends an authorized
document to OCR, joins page text with newline separators, and submits that text
to the separately authorized extraction client. It maps validated extraction
citations back to zero-based page indexes and page-relative Unicode code-point
offsets. A citation crossing a page boundary is rejected. Dispose the returned
`DocumentExtractionResult` to release both JSON documents. Both results may
contain sensitive data; citations identify source text and do not establish
semantic correctness. The host must supply both clients, their authorizers,
privacy policy and reviewed worker configuration.
