namespace OpenConnect.QdrantVactor;

public interface IEmbeddingGenerator
{
    ValueTask<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken);
}

public sealed record VectorPoint(
    string Id,
    string TenantId,
    string AgentId,
    string Content,
    ReadOnlyMemory<float> Vector,
    long Revision,
    string? EventId = null,
    string? SessionId = null,
    string? PrivacyClass = "Sanitized",
    DateTimeOffset? CreatedAt = null,
    double? Score = null);

public interface IVectorStore
{
    Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken);
    Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken);
}
