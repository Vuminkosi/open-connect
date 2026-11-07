using System.Collections.Concurrent;

namespace OpenConnect.QdrantVactor;

/// <summary>In-memory fake store used until a verified Qdrant persistence adapter is supplied.</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, VectorPoint> _points = new(StringComparer.Ordinal);

    public Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        cancellationToken.ThrowIfCancellationRequested();
        _points.AddOrUpdate(point.Id, point, (_, current) => point.Revision >= current.Revision ? point : current);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        // Tenant and agent predicates are applied before ranking; payload metadata never grants access.
        IReadOnlyList<VectorPoint> result = _points.Values
            .Where(point => point.TenantId == tenantId && point.AgentId == agentId)
            .Select(point => (Point: point, Score: Similarity(vector.Span, point.Vector.Span)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Point.Id, StringComparer.Ordinal)
            .Take(limit).Select(item => item.Point).ToArray();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Fake-store deletion is immediate. A persistent adapter must use tombstones/expiry and revision guards.
        foreach (var pair in _points.Where(pair => pair.Value.TenantId == tenantId && pair.Value.AgentId == agentId && pair.Key == documentId).ToArray())
            _points.TryRemove(pair.Key, out _);
        return Task.CompletedTask;
    }

    private static double Similarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0) return 0;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
