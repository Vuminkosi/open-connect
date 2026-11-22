# Root Compose worker profiles

`docker-compose-all.yml` contains the worker and supporting services for the
sample stack. Optional profiles include `knowledge`, `analytics`, `documents`,
`storage`, `storage-init`, `vision`, `vision-embed`, `knowledge-index`, `knowledge-image`,
`knowledge-rerank`, `extraction`, `classification`, `nllb`, `mms-tts` and `realtime-voice`.
Use Compose profiles to enable only the capabilities required by an application.
Indexing and extraction services require their own configuration, dependencies,
and verified model assets; their presence in Compose does not establish readiness.
Contributor-model data use is not approved by default. Operators who have
reviewed and accepted the model's terms must explicitly set
`OC_CONTRIBUTOR_DATA_USE_APPROVED=1` before using such a model.

The `documents` profile includes a model initializer, a network-isolated OCR
worker, ClamAV and MinIO. Supply approved JSON configurations using
`OC_DOCUMENT_INIT_CONFIG` and `OC_DOCUMENT_SERVE_CONFIG`; the defaults are
deliberately unconfigured. The serve configuration must refer to
`/usr/local/bin/tesseract` and `/run/clamav/clamd.sock`. Provide
`OPENCONNECT_WORKER_TOKEN` and `OPENCONNECT_WORKER_SIGNING_KEY` using the
deployment's secret management. The initializer provisions a verified model
into the persistent model volume; the serving worker mounts it read-only and
does not download models. The worker's Unix socket is shared with the sample
gateway container, but an application must explicitly configure an authorized
document client before it can make requests.

ClamAV uses a private Unix socket and a persistent signature database; its
updater requires network access. Verify signature freshness, scanner limits and
the deployed sandbox before accepting documents. MinIO also runs under the
`storage` profile. Set `OC_MINIO_ROOT_USER` and `OC_MINIO_ROOT_PASSWORD` using
deployment-managed credentials; the service exits when either is absent.
For a new versioned bucket, set `OC_STORAGE_BOOTSTRAP_BUCKET`,
`OC_STORAGE_BOOTSTRAP_ACCESS_KEY` and `OC_STORAGE_BOOTSTRAP_SECRET_KEY` through
the deployment's secret management, then explicitly run:

```sh
docker compose -f docker-compose-all.yml --profile storage-init run --rm storage-bucket-init
```

The initializer creates and enables versioning only for a new bucket. It refuses
an existing unversioned bucket or one with a bucket policy requiring review; it
does not modify either. The initializer defaults to TLS; using the bundled HTTP
endpoint requires explicit `OC_STORAGE_BOOTSTRAP_TLS=false` on an appropriately
protected development network. Provision scoped application credentials and
independently verify bucket and IAM access policies; absence of a bucket policy
alone does not prove the bucket is private. No host
ports are published for MinIO or ClamAV. The default internal MinIO connection
is HTTP; configure TLS and encryption at rest for deployments handling sensitive
data. The synchronous document worker does not automatically store documents.

The `knowledge-index` profile starts the index API, background consumer, expiry
consumer, document worker, ClamAV and MinIO. Supply a migrated knowledge database,
an approved knowledge model configuration, a private versioned bucket, scoped
storage credentials and worker credentials. The index consumer defaults to TLS
for MinIO (`OC_INDEX_MINIO_TLS=true`), while the included MinIO service serves
HTTP unless a TLS endpoint is separately configured. Configure a trusted TLS
endpoint for protected deployments; for an isolated development stack using the
included HTTP service, set `OC_INDEX_MINIO_TLS=false` explicitly. Index deletion
removes indexed data and queues reconciliation; it does not by itself delete the
original object. The `extraction` profile similarly requires an approved, locally
provisioned model and explicit adapter configuration.

For a MinIO TLS endpoint signed by a private CA, set `OC_MINIO_CA_DIRECTORY`
to a directory containing the trusted CA bundle and configure
`OC_INDEX_MINIO_CA_FILE=/run/minio-ca/ca.pem` and
`OC_STORAGE_BOOTSTRAP_CA_FILE=/run/minio-ca/ca.pem` as applicable. The clients
verify the server certificate and hostname; the server certificate must match
the configured endpoint. A CA file cannot be used with TLS disabled.

The optional `storage-tls` profile provides a private TLS proxy for the bundled
MinIO service. Set `OC_MINIO_TLS_DIRECTORY` to a directory containing
`server.crt` and `server.key`, readable by container user 101. The certificate
must identify `minio-tls`. Set the clients' endpoints to `minio-tls:9443`, keep
TLS enabled and configure the CA bundle as above. The proxy forwards to MinIO
over HTTP on the internal Compose network, so that network and access to the
MinIO service still require protection. The proxy does not publish a host port.

The Compose PostgreSQL service is available to containers as `postgres` on port
`5432`. Set `OC_KNOWLEDGE_SEARCH_DSN` to a libpq connection string for the
intended database and a suitably scoped database role; the profile does not
derive credentials from the PostgreSQL administrator account. Apply the SQL
files in `workers/knowledge/migrations` in numeric order before enabling the index
services. For a new schema, set `OC_KNOWLEDGE_MIGRATION_DSN` to a connection
using a migration role and explicitly run:

```sh
docker compose -f docker-compose-all.yml --profile knowledge-migrate run --rm worker-knowledge-migrate
```

The runner applies
the migrations transactionally, journals their checksums, and refuses an existing
untracked schema or changed migration history. Existing manually migrated schemas
require the deployment's established migration process. Compose does not run
the migration automatically, and an empty DSN does not configure the index.

The classification and NLLB profiles use their existing pre-provisioned model
volume checks. Their initializers do not download or validate model weights.
The MMS and realtime voice initializers retain their existing behavior and do
not imply that model weights are present. The corresponding capabilities must
remain unavailable until their own readiness conditions pass.

Volumes retain application data, model caches and signature databases across
ordinary Compose restarts. Avoid removing volumes unless their data has been
independently reviewed and removal is explicitly authorized. Set
`OC_NETWORK_NAME` when a separate Compose network is needed; the default
network name remains compatible with the sample stack.

For local worker verification, run `uv run --locked python ../common/verify_install.py`
from the worker directory. The shared package is installed from a path and a
reused environment can contain an older copy. If the check reports a mismatch,
refresh it with `uv sync --locked --reinstall-package openconnect-worker-common`
before running the worker tests.
