namespace OpenConnect.Docs.Cloud;

public sealed record CloudInferenceConsumptionResult(string Status, long? ReservedTokens = null, string? ModelId = null, string? ModelRevision = null, string? ModelDigest = null);

public interface ICloudInferenceConsumptionStore
{
    Task<CloudInferenceConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken);
}

public sealed class CloudInferenceConsumptionService(ICloudInferenceConsumptionStore store)
{
    public Task<CloudInferenceConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || workerPayloadFingerprint is not { Length: 32 }
            ? Task.FromResult(new CloudInferenceConsumptionResult("invalid_request"))
            : store.ConsumeAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint.ToArray(), cancellationToken);
}
