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

## License

Same terms as the OpenConnect repository.
