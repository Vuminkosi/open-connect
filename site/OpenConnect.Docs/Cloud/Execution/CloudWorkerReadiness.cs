namespace OpenConnect.Docs.Cloud;

public interface ICloudWorkerReadinessStore
{
    Task<bool> IsReadyAsync(Guid deploymentId, CancellationToken cancellationToken);
}

public sealed class CloudWorkerReadiness(ICloudWorkerReadinessStore store)
{
    public Task<bool> IsReadyAsync(Guid deploymentId, CancellationToken cancellationToken) =>
        deploymentId == Guid.Empty ? Task.FromResult(false) : store.IsReadyAsync(deploymentId, cancellationToken);
}
