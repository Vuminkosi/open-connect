# MinIO versioned document storage

This optional adapter reads immutable object versions from a private MinIO
bucket. Configure an `IMinioClient` with scoped credentials, a protected
transport and a request timeout. Construct `MinioVersionedObjectReader` with
that client and the configured bucket, then pass its `OpenVersionAsync` method
to `KnowledgeImageClient.UpsertObjectAsync` after authorizing the object and
target in the host.

The reader accepts only the tenant-scoped object-key format used by the
knowledge indexing pipeline. It requests the exact version, rejects null
versions and responses with mismatched version or size metadata, and limits
each read to 10 MiB. Dispose the returned stream; its backing buffer is cleared
on disposal. `KnowledgeImageClient` separately verifies the expected content
size and SHA-256. The reader does not authorize users, configure a bucket,
create presigned URLs, or delete source data.

`ClamAvScanner` provides a bounded, fail-closed INSTREAM client over a protected
local Unix socket. The deployment must restrict access to the socket and keep
the daemon's signatures and scan limits current. `MinioDocumentWriter` requires
an explicit host retention authorization callback, snapshots the content, scans
that snapshot, and writes to a random tenant-scoped key in a version-enabled
bucket. It reads back the exact reported version and verifies its size and
SHA-256 before returning a reference usable by the knowledge clients. Configure
the MinIO client with scoped credentials, protected transport and timeouts. A
two-minute cancellation deadline covers authorization, scanning and storage;
host callbacks and the storage client must honor cancellation.
The host must bind the authorization callback to the authenticated principal,
document and applicable retention policy, and protect the returned reference.
If an upload succeeds but version or readback verification fails, the operation
returns no reference and may leave an object for authorized reconciliation.
The writer does not implement retries or lifecycle reconciliation.

`MinioDocumentDeleter` submits deletion of one tenant-scoped object version
after a host callback authorizes that exact tenant, object and version. The
host must coordinate concurrent references, retention rules and dependent
stores before granting authorization. A successful call confirms the storage
request was accepted; it does not prove deletion from backups or other stores.
The adapter is not an automatic knowledge-index reconciliation sink.
