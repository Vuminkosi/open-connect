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
    Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken);
}

public sealed class CloudExecutionConsumptionService(ICloudExecutionConsumptionStore store)
{
    public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty
            ? Task.FromResult("invalid_request")
            : store.ConsumeAsync(reservationId, claimId, deploymentId, cancellationToken);
}
