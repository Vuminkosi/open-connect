---
title: Documents, knowledge and vision
version: v1
summary: Authorize, process, index and search documents through protected workers.
order: 74
---

# Documents, knowledge and vision

The optional `OpenConnect.Documents` and `OpenConnect.Knowledge` libraries connect a trusted .NET host to separate worker services. Their workers and model files must be deployed and configured independently. Authenticate the user in your host, authorize the specific tenant and resource, apply privacy policy, and protect the connection to each worker. Worker tokens and signing keys belong in the host's secret store.

## Process a document

`DocumentWorkerClient` uses a protected Unix socket and accepts `documents.ocr`, `documents.fields`, `documents.layout`, `documents.tables` and opt-in `documents.analyze` operations. Construct it with the socket path, bearer token, signing key, host firewall, privacy options and an authorization callback. The callback must check the authenticated principal, tenant, document and requested operation; a callback that always returns true is not a production policy.

```csharp
// client is a configured DocumentWorkerClient; context comes from authenticated host identity.
using var result = await client.ExecuteAsync(
    context, "documents.ocr", documentBytes, cancellationToken);
// Review the returned OCR and source references before using them downstream.
```

The caller disposes the returned `JsonDocument` and protects it as sensitive data. The client bounds request and response sizes, checks the response and uses a 130-second deadline in addition to cancellation. The deployment must configure the worker sandbox and malware scanner. OCR and extracted fields are candidates, not verified facts. Layout groups source-cited lines; table detection is heuristic and may omit merged or empty cells. Combined analysis can return partial page or component results, which callers must handle explicitly.

## Extract structured fields

`ExtractionWorkerClient` submits authorized source text and a JSON schema to an inference worker over a protected Unix socket or trusted HTTPS endpoint. `DocumentExtractionWorkflow` composes OCR with extraction and maps source citations back to pages. Configure and authorize both clients separately. Review the candidate and its citation; a valid citation identifies source text but does not prove the extracted meaning. Dispose the `DocumentExtractionResult` and protect both its documents. A sample is available at `samples/Blazor` under the opt-in document example; read its README for its development-only prerequisites.

```csharp
using OpenConnect.Documents;

// documentClient, extractionClient and schema are configured and authorized by the host.
var workflow = new DocumentExtractionWorkflow(documentClient, extractionClient);
using var extracted = await workflow.ExecuteAsync(
    context, documentBytes, schema.RootElement, cancellationToken);
foreach (var (field, citation) in extracted.Citations)
{
    Console.WriteLine($"{field}: page {citation?.PageIndex}");
}
```

## Index and search

### Submit and check an indexing job

1. Store an authorized immutable document version in protected storage and retain its reference, digest and access policy.
2. Configure `KnowledgeIndexClient` with a private worker endpoint, signing credentials, egress gate and host authorization callbacks for the exact tenant, object, collection and ACL. Submit the versioned reference with `SubmitAsync`.
3. Poll `StatusAsync` for the job. A submission receipt means admission to the queue, not successful indexing. Check the final status before relying on search.
4. Configure `KnowledgeSearchClient` with a callback authorizing the tenant, principal, application, collection and roles. `SearchAsync` performs lexical search; `SearchHybridAsync` also requires a host-approved query embedding provider in the same model space as the index. Dispose each returned JSON document and protect results and citations.
5. For deletion, authorize the exact scope and provide the expected generation to `DeleteAsync`. Its receipt describes index tombstone and reconciliation state; it does not prove deletion of the original object or backups. Coordinate retention and deletion across all stores.

The knowledge worker's migrations are explicit, and an index deployment can require PostgreSQL, a versioned private MinIO bucket, a scanner, a document worker and approved model files. See `workers/knowledge/README.md` and `samples/Blazor/README.md` in the source repository for deployment-specific setup. Do not claim that chunking or an embedding response alone created a durable index.

```csharp
// submission is a validated KnowledgeIndexSubmission for an authorized object version.
using var receipt = await indexClient.SubmitAsync(context, submission, cancellationToken);
using var status = await indexClient.StatusAsync(
    context, submission.JobId, submission.ApplicationId, submission.CollectionId,
    submission.DocumentId, roleIds, cancellationToken);
// Inspect status before searching. The submission receipt only confirms admission.
```

### Search an authorized collection

```csharp
// searchClient is a configured KnowledgeSearchClient with a host authorizer.
// context.Destination must be EgressDestination.Worker.
using var results = await searchClient.SearchAsync(
    context, applicationId, collectionId, roleIds,
    query: "refund policy", limit: 5, cancellationToken: cancellationToken);
// Read and protect results.RootElement; check citations before presenting passages.
```

## Embeddings, images and reranking

`KnowledgeEmbeddingClient` privacy-screens text before sending it, signs the request and checks model identity, dimension and vector properties. `VisionEmbeddingClient` and `VisionAnalysisClient` require image authorization and a modality policy; object-reference operations also verify the exact version, size and digest. `KnowledgeImageClient` adds index and search operations; `KnowledgeRerankClient` orders an already authorized candidate set. Rerank scores are raw logits, not probabilities. Embeddings and image results remain sensitive data and need access, retention and disclosure controls. Provision matching model spaces and verify worker readiness before use.

```csharp
using OpenConnect.Knowledge;
using OpenConnect.Privacy;

// gate, workerToken and signingKey come from the trusted host.
using var embedder = new KnowledgeEmbeddingClient(
    gate, new Uri("https://knowledge-worker.example"), workerToken, signingKey,
    destination: EgressDestination.Worker);
var workerContext = new EgressContext(tenantId, principalId, sessionId, EgressDestination.Worker);
var embedding = await embedder.EmbedAsync(workerContext, "query", ["refund policy"], cancellationToken);
Console.WriteLine($"Dimension: {embedding.Dimension}");
```

Use this example only for a worker trusted by the host's policy. Authorize the user and scope before creating the context; do not log vectors or credentials.

For MinIO, `OpenConnect.Storage.Minio` offers version-bound reading, scanning and writing helpers. The host supplies scoped credentials, retention authorization and protected transport. A storage receipt does not replace application authorization or cross-store deletion reconciliation.

## Related links

- [Privacy firewall](/docs/v1/privacy)
- [Semantic memory and Qdrant](/docs/v1/semantic-memory)
