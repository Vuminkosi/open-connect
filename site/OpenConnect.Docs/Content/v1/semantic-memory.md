---
title: Semantic memory and Qdrant
version: v1
summary: Configure agent-scoped semantic memory and understand collection migration.
order: 75
---

# Semantic memory and Qdrant

`OpenConnect.QdrantVactor` supplies semantic memory and an optional Qdrant REST store. Its default registrations include an in-memory store, a deterministic embedding stub and a deny-by-default agent scope authorizer. These defaults are useful for composition and tests; they are not a production semantic search deployment.

## Configure a store

Install `OpenConnect.QdrantVactor`, provision a protected Qdrant collection and supply its endpoint, scoped API key, collection name and vector dimension. Register an embedding generator compatible with the collection and a host authorizer that checks the actual tenant and agent. For example, the following is a registration outline; values must come from trusted configuration:

```csharp
using OpenConnect.QdrantVactor;

builder.Services.AddQdrantVactor(options =>
{
    options.Enabled = true;
    options.Endpoint = qdrantEndpoint;
    options.ApiKey = qdrantApiKey;
    options.CollectionName = "application-memory";
    options.VectorSize = embeddingDimension;
    options.SubscribeToAgentEvents = false;
});
builder.Services.UseQdrantRestVectorStore();
```

The REST store requires an endpoint and API key. Use protected transport and a secret store, and ensure the embedding generator and existing collection have the same vector dimension and model space. The default authorizer denies agent scope; replace it with an application policy before expecting authorized operations. Registering the adapter does not create a secure Qdrant deployment or supply an application-specific embedding model.

## Index and search

Resolve `IAgentSemanticMemory` from the host and use authenticated tenant and agent scope. New writes derive storage identity from tenant, agent and document ID; a write with the same ID in the same scope replaces that scope's point. Search returns relevance scores and original document IDs. Treat stored text, identifiers and vectors as sensitive, and authorize retrieval as well as indexing.

```csharp
using OpenConnect.Abstractions;

// memory is the configured IAgentSemanticMemory; scope comes from host authorization.
await memory.UpsertAsync(new MemoryDocument(documentId, tenantId, agentId, content), cancellationToken);
var result = await memory.SearchAsync(
    new MemorySearchRequest(tenantId, agentId, query, Limit: 5), cancellationToken);
// Apply the application's disclosure and retention policy before presenting result.Matches.
```

Deletion is separately authorized: use `DeleteAsync` with a `MemoryDeletionRequest` containing the exact tenant, agent and document ID. Do not treat deletion of the vector as deletion of the original source or its backups.

When `SubscribeToAgentEvents` is enabled, register an `IAgentEventSubscriptionV2` and a custom `IProjectionReader` that retrieves authorized, sanitized content. Startup validation rejects a missing subscription or the built-in empty reader in that configuration. Enabling subscriptions is not a substitute for a privacy policy.

## Existing collections

Older unscoped points are not automatically migrated or deleted by the scoped identity scheme. Before upgrading an existing collection, rebuild from the authoritative source into a separate collection with the correct tenant and agent scope, verify counts and scoped searches, and switch only after validation. Retain the old collection according to the application's recovery and retention policy. See the package README for migration limitations and detailed behavior.

## Related links

- [Documents, knowledge and vision](/docs/v1/documents-and-knowledge)
- [Privacy firewall](/docs/v1/privacy)
