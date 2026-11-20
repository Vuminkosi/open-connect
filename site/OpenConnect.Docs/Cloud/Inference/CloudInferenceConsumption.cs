namespace OpenConnect.Docs.Cloud;

public interface ICloudInferenceConsumptionStore
{
    Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken);
}

public sealed class CloudInferenceConsumptionService(ICloudInferenceConsumptionStore store)
{
    public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || workerPayloadFingerprint is not { Length: 32 }
            ? Task.FromResult("invalid_request")
            : store.ConsumeAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint.ToArray(), cancellationToken);
}
