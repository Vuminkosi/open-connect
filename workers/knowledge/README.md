# Knowledge worker

The current component is an offline CPU ONNX text embedding adapter. It loads
only assets declared in a verified `openconnect-worker-common` cache revision,
uses attention-mask mean pooling and L2 normalization, and identifies the
embedding space by model fingerprint and preprocessing profile. E5 profiles
apply the appropriate `query:` or `passage:` prefix. The adapter enforces
batch, text and token limits and reports truncation per input. ONNX external
tensor data is currently unsupported and rejected.

Tests use synthetic ONNX graphs and tokenizers for runtime mechanics, not
semantic quality. The private endpoint
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

`PostgresKnowledgeIndex` provides an application-controlled PostgreSQL lexical
store for prepared documents. Apply `migrations/0001_knowledge_index.sql`
explicitly to a new, approved database before use; the worker does not apply
migrations on startup. The host must authenticate and authorize the tenant,
application, collection, principal and roles supplied to the store, protect
database credentials and transport, and provide encryption and retention for
the sensitive indexed text, vectors and ACLs. Database access is restricted to
the trusted host; the supplied scope is not itself proof of authorization.

The optional indexing job primitives require the explicit
`migrations/0003_index_jobs.sql`, `migrations/0004_index_workers.sql` and
`migrations/0005_expiration_index.sql`, followed by `migrations/0006_image_vectors.sql`,
migrations. A job stores a tenant-scoped,
versioned object reference and ACL metadata, not document bytes. Claims have
bounded leases and increasing fencing tokens; index publication and job
completion occur in the same transaction, and an expired claimant cannot
publish. Consumers claim only jobs matching their verified embedding space;
they reject a mismatched claim before accessing its object. The processor verifies object size and digest, scans before parsing,
and uses a supplied sandboxed parser and embedding engine. The host must provide
and authorize the private MinIO resolver, ClamAV socket and parser, and must
secure database contents. The opt-in root Compose profile `knowledge-index`
runs a background consumer after its document worker, scanner, MinIO and model
initializer are ready. Configure the database, approved model files, private
bucket and scoped MinIO credentials using the corresponding `OC_KNOWLEDGE_*`,
`OC_DOCUMENT_*`, and `OC_INDEX_*` settings. The consumer defaults to TLS for
MinIO; disabling it requires a protected private network. The bucket must have
versioning enabled. The profile does not apply database migrations, create a
bucket or authorize job submissions. The profile also starts a private, signed
`knowledge.index` submission API.
The trusted host must authorize the tenant, object version, target scope and
requested ACL before signing. The API advertises readiness only while a
consumer with a verified matching model cache, healthy document worker and
scanner, and a versioned MinIO bucket holds a current readiness lease. A new
job with no live worker for its embedding space is rejected; an identical
accepted job ID is idempotent, and a changed payload conflicts. A receipt means
the job was queued, not that indexing succeeded. The signed
`knowledge.index.status` request returns only state, attempt count, generation
and next retry time; it checks tenant, job and document scope, original job
ACL, and current document access. The host must authenticate and authorize the
principal and roles before signing a status request. Status remains available
when the consumer is offline if the database is reachable.

The separate opt-in `OC_INDEX_DELETE_ENABLED=true` switch permits signed
`knowledge.index.delete` requests from the trusted host. The host must authorize
the tenant and document deletion before signing. The expected generation fences
concurrent updates, and the transaction removes index chunks and creates a
tombstone and metadata-only reconciliation event. The receipt reports that
event's state. It does not establish that the original MinIO object or other
dependent stores have been deleted; those require an authorized reconciliation
sink and retention policy.

The `knowledge-index` profile also runs a separate expiry consumer with only a
database connection; it does not load a model or connect to MinIO or OCR. It
processes expired documents in bounded tenant-scoped batches. Each transaction removes indexed chunks and ACLs,
advances the tombstone generation, and writes a deletion reconciliation event.
Search excludes expired documents even when the expiry consumer is offline. Expiry
processing is eventual and does not by itself remove the original MinIO object;
an authorized reconciliation sink and retention policy are still required.

The image-vector store requires migration `0006_image_vectors.sql` before
starting updated index services; document writes, deletion and expiry remove
derived image vectors transactionally. Each vector is fenced to a live parent
document generation and inherits that document's tenant and ACL. Image search
accepts only vectors from the same model fingerprint, embedding space and
dimension. The host must authorize all writes and caller identities and must
protect vector storage. This store is a library primitive; it is not yet wired
to object ingestion and does not delete the original object. The separate
`knowledge-image` profile offers private signed `knowledge.image.upsert` and
`knowledge.image.search` operations. The trusted host must authorize the
tenant, document write and caller identity and ensure image vectors came from
an authorized, malware-screened `vision.embed` request before signing. Search
checks the parent document ACL and model space. Updates require both the parent
document generation and, for replacement, the current image revision. Results
contain image identifiers and scores, not image bytes. These operations do not
provide durable request replay protection.

Updates use a document generation captured before processing; conflicting
updates fail instead of overwriting a newer generation. Identical replays are
idempotent, and a source revision plus embedding space cannot be replaced with
different content. Deletion removes indexed chunks and ACLs transactionally,
leaves a generation tombstone, and writes a metadata-only outbox event. A
reconciliation runner can claim these events using tenant-scoped leases after
the explicit `migrations/0002_outbox_delivery.sql` migration. It retries failed
deliveries and blocks later changes to the same document behind a dead-lettered
event. Delivery is at least once: the configured sink must durably deduplicate
events and fence stale document generations, including operations already in
flight when a lease expires. The runner alone does not implement a sink, resolve
authorized object references, delete MinIO objects, or provide an authorized
network endpoint. Lexical and exact-vector hybrid
search materialize authorized documents before selecting candidates. Hybrid
search only compares matching model identifiers, fingerprints, dimensions and
embedding spaces; it uses reciprocal-rank fusion, not calibrated relevance
probabilities. It refuses more than 10,000 authorized chunks or 4 million vector
values and imposes a database statement timeout. `search_and_rerank` passes only
the retrieved ACL-filtered hits to a trusted local reranker, then rechecks access
and source content before returning citations. A changed or revoked hit is
omitted with a warning. The host must still authenticate the principal, validate
roles and tenant scope, and apply its privacy policy before calling this library.
The opt-in private `knowledge.search` endpoint accepts tenant, application,
collection, principal and role assertions only from the authenticated signing
host; a worker credential is not an end-user authorization. Enable it with
`OC_KNOWLEDGE_SEARCH_ENABLED=true` and a protected PostgreSQL connection in
`OC_KNOWLEDGE_SEARCH_DSN`. A ready reranker and the explicitly migrated index
are required for capability discovery. The host must enforce access and
entitlement policy before signing each request. Test against a
disposable local database by applying the migration and setting
`OC_TEST_KNOWLEDGE_PG_DSN` for `tests/test_postgres_index.py`; these tests refuse
non-loopback hosts and database names other than `openconnect_test`.

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

The separate opt-in root Compose profile `knowledge-rerank` runs the same image
with a reranker initializer and serving process. Set `OC_RERANK_INIT_CONFIG`
and `OC_RERANK_SERVE_CONFIG` to approved configuration files; defaults fail
closed. The initializer may provision the pinned cross-encoder on first start,
and the serving process uses the persistent cache read-only. Outside Compose,
`OC_RERANK_ENABLED=true` and `OC_RERANK_CONFIG_PATH` enable the independently
configured capability. The host must apply tenant and document ACL filtering
before signing a `knowledge.rerank` request; worker signatures do not authorize
candidate access. The result returns candidate IDs and raw logits, which are
not calibrated probabilities. Pairs exceeding the token limit are rejected.

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
