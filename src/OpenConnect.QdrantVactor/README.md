# OpenConnect.QdrantVactor

Qdrant-backed semantic memory for OpenConnect: vector indexing and agent-scoped
similarity search for recall (`OpenConnect.QdrantVactor`).

## Install

```bash
dotnet add package OpenConnect.QdrantVactor
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Index and query memories through the `OpenConnect.QdrantVactor` namespace, composed
through service registration with your Qdrant endpoint. See the repository docs (`docs/`)
for scoping and authorization notes.

## License

Same terms as the OpenConnect repository.
