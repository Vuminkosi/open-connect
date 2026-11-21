namespace OpenConnect.Docs.Cloud;

public interface ICloudVoiceConsumptionStore
{
    Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerFingerprint, CancellationToken cancellationToken);
}

public sealed class CloudVoiceConsumptionService(ICloudVoiceConsumptionStore store)
{
    public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerFingerprint, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || workerFingerprint is not { Length: 32 }
            ? Task.FromResult("invalid_request")
            : store.ConsumeAsync(reservationId, claimId, deploymentId, workerFingerprint.ToArray(), cancellationToken);
}
