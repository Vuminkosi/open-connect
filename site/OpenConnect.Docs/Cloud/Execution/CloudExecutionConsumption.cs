namespace OpenConnect.Docs.Cloud;

// A consumption records permission to start once, not proof that execution completed.
public sealed class CloudExecutionConsumption
{
    public Guid ReservationId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}

public interface ICloudExecutionConsumptionStore
{
    Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken);
}

public sealed class CloudExecutionConsumptionService(ICloudExecutionConsumptionStore store)
{
    public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || workerPayloadFingerprint is not { Length: 32 }
            ? Task.FromResult("invalid_request")
            : store.ConsumeAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint.ToArray(), cancellationToken);
}
