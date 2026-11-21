namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUIMeteredConsumptionStore
{
    Task<CloudLiveUIConsumptionResult> ConsumeMeteredAsync(Guid reservationId, Guid claimId, Guid deploymentId,
        byte[] workerPayloadFingerprint, int contextTokens, CancellationToken cancellationToken);
}

/// <summary>Validates a metered worker's single-use LiveUI claim consumption.</summary>
public sealed class CloudLiveUIMeteredConsumption(ICloudLiveUIMeteredConsumptionStore store)
{
    public Task<CloudLiveUIConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId,
        byte[] fingerprint, int contextTokens, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || fingerprint is not { Length: 32 } ||
        contextTokens is < 256 or > 32768
            ? Task.FromResult(new CloudLiveUIConsumptionResult("invalid_request"))
            : store.ConsumeMeteredAsync(reservationId, claimId, deploymentId, fingerprint.ToArray(), contextTokens, cancellationToken);
}
