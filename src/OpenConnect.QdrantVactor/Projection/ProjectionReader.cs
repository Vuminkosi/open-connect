namespace OpenConnect.QdrantVactor;

/// <summary>Supplies sanitized output by opaque projection reference; implementations must enforce authorization.</summary>
public interface IProjectionReader
{
    Task<string?> ReadSanitizedAsync(string projectionReference, string tenantId, CancellationToken cancellationToken);
}

internal sealed class ProjectionReader : IProjectionReader
{
    public Task<string?> ReadSanitizedAsync(string projectionReference, string tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
