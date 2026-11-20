namespace OpenConnect.Docs.Cloud;

public sealed record CloudLiveUIConsumptionResult(string Status, string? ModelId = null, string? ModelRevision = null, string? ModelDigest = null);

public interface ICloudLiveUIConsumptionStore
{
    Task<CloudLiveUIConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken);
}

public sealed class CloudLiveUIConsumptionService(ICloudLiveUIConsumptionStore store)
{
    public Task<CloudLiveUIConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || workerPayloadFingerprint is not { Length: 32 }
            ? Task.FromResult(new CloudLiveUIConsumptionResult("invalid_request"))
            : store.ConsumeAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint.ToArray(), cancellationToken);
}
