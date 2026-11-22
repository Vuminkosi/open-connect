# OpenConnect knowledge client

`KnowledgeEmbeddingClient` sends private embedding requests to a configured
worker after evaluating every text through the host `IEgressGate`. It sends
the sanitized values, signs the exact serialized request, and validates the
response's task ID, model-space metadata, vector dimensions, batch count and
actual vector norm. A 100-second deadline bounds response headers and body reads,
in addition to caller cancellation.
The output gate checks model identity metadata before returning the result.
Vectors themselves are not inspected as text; they remain sensitive application
data and require their own access and retention controls.

The host must authenticate and authorize the principal for the tenant before
constructing the egress context. Configure the destination according to the
actual deployment boundary: it defaults to `ExternalModel`; choose `Worker`
only for a trusted local worker under the host's privacy policy. Plain HTTP
is disabled unless explicitly opted in for an appropriately protected private
transport. Keep the bearer token and signing key in the host secret store.
The client owns its HTTP connection pool, does not follow redirects or use a
system proxy, and should be disposed when the application no longer needs it.

This client does not implement entitlement checks, quotas, network isolation,
retry/idempotency, or indexing. A successful embedding is application data
and must be protected and retained according to tenant policy.

`KnowledgeSearchClient` sends lexical or hybrid search requests to a trusted worker.
Its required host authorization callback must check the authenticated tenant
and principal, application, collection and exact roles. The privacy gate is
applied to the query before signing. Search results contain sensitive source
text and citations. The client checks the response through the output privacy
gate and rejects results that would need transformation, preserving citation
offsets. The caller owns and must dispose the returned JSON document and apply
any additional application policy. This client does not establish entitlements
or authenticate the end user itself.

Hybrid search uses a host-approved embedding provider that receives the
privacy-gated query. The provider must embed that exact query using the same
model space as the index. Truncated, non-normalized and malformed embeddings
are rejected before sending the request.

`KnowledgeIndexClient` submits an immutable versioned object reference and
reads job status. Its host callbacks must authenticate and authorize the
principal, tenant, object, destination scope and ACL, including any required
privacy and retention policy, before the client signs either request. A receipt
indicates queue admission, not successful indexing. The caller owns and must
dispose each returned JSON document.

Index deletion requires its own authorization callback and an expected
generation. Its receipt identifies the index tombstone and reconciliation
state; it does not confirm that the MinIO source object was deleted.
The required egress gate inspects indexing, status and deletion metadata before
it is sent and inspects receipts at the output boundary. Requests are denied if
inspection would alter scope, ACL or object identifiers, since transformed
identifiers could refer to different resources.

`VisionEmbeddingClient` connects to the protected local vision embedding socket.
The host must authorize each image and text query for the authenticated tenant
and principal. Image requests pass a modality policy check; text requests pass
the text egress gate and use its sanitized result. The worker scans image bytes
before inference. Returned vectors are validated for dimension, model identity
and normalization, and remain sensitive data. The output gate inspects model
identity metadata but does not classify the numeric vector as text.

`KnowledgeImageClient` combines the vision client with the private image index.
It authorizes the exact target and image before indexing and the application,
collection and roles before searching. It passes the vision result's model
identity to the index and validates its response. Image writes require the
current parent generation and, for replacement, the current image revision.
Its required egress gate inspects authorized metadata and returned index results;
metadata must remain unchanged to preserve the authorized scope. Image bytes
and vectors also require the separate modality policy and application protection.
`UpsertObjectAsync` also accepts a versioned object reference. The host must
authorize the object and target, and provide a reader bound to its private
MinIO bucket. The optional `OpenConnect.Storage.Minio` package supplies a
versioned reader for this contract. The client derives the tenant-scoped object key, passes the exact
version to the reader, enforces the byte limit, and checks size and SHA-256
before the image is sent for scanning and indexing. It does not configure the
MinIO client or delete source objects.

`VisionAnalysisClient` invokes the local classifier over a protected Unix
socket after host authorization and an image modality privacy check. The worker
scans the image before inference. The client validates the task, model identity,
classification ordering and output privacy before returning a caller-owned JSON
document. Classification labels are generic observations and require application
review. `AnalyzeObjectAsync` authorizes an immutable object reference, derives
the tenant-scoped key and verifies its exact version, size and SHA-256 before
analysis. The host supplies a version-bound reader and remains responsible for
object access, retention and protecting the result.

`KnowledgeRerankClient` submits an explicit candidate set to a private reranking
worker. The host must authorize the authenticated tenant, principal and every
candidate against retrieval ACLs before dispatch; the client does not retrieve
or authorize documents. It snapshots the candidate list, applies the privacy
gate to the query and text, preserves candidate identifiers, and checks the
worker's model identity, ordering and exact result set. Scores are raw logits,
not calibrated probabilities. The caller owns the returned JSON document.
