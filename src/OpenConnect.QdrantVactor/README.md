# OpenConnect.QdrantVactor

Qdrant-backed semantic memory for OpenConnect: vector indexing and agent-scoped
similarity search for recall (`OpenConnect.QdrantVactor`).

## Install

```bash
dotnet add package OpenConnect.QdrantVactor
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.QdrantVactor;

services.AddQdrantVactor(options => { /* Qdrant endpoint + API key (see docs) */ });
services.UseQdrantRestVectorStore(); // swaps InMemoryVectorStore for the Qdrant REST store
```

`AddQdrantVactor` (`ServiceCollectionExtensions.cs:9`) registers the indexer, an
in-memory store, and `IAgentSemanticMemory`; `UseQdrantRestVectorStore` (`:27`)
replaces the store with the Qdrant REST implementation (requires an API key and a
valid endpoint). Agent scope is deny-by-default.

Semantic search returns store relevance scores in descending order; a missing score is represented as zero. The REST store requests vectors because its public `IVectorStore` result includes the vector. If a returned vector has the wrong configured dimension, the search fails with a dimension error instead of silently omitting the result. Error messages do not include vector or document contents.

## Semantic memory identity and existing collections

New `IAgentSemanticMemory` writes derive their storage identity from the tenant, agent and document ID. Reusing a document ID in another scope therefore creates a separate point; writing it again in the same scope replaces that scope's point. The original document ID is returned by search. The REST adapter keeps the scoped source ID in the Qdrant payload, so treat document IDs as data stored in the collection. Indexer-generated point IDs retain their existing derivation.

This changes the storage identity of documents written through `IAgentSemanticMemory`. Existing unscoped points are not automatically moved, overwritten, or deleted. They may still appear in searches, and new deletes target only the new scoped identity. A previous non-GUID logical ID cannot in general be recovered from its Qdrant UUID. Before upgrading an existing collection, use the authoritative source of each document and its tenant and agent to rebuild semantic-memory points in a separate collection, rebuild any required indexer points, validate counts and scoped searches, and then switch the configured collection. Retain the old collection until validation and the project's retention requirements are satisfied. Data already overwritten by an earlier cross-scope collision must be recovered from an authoritative source or backup.

## License

Same terms as the OpenConnect repository.
