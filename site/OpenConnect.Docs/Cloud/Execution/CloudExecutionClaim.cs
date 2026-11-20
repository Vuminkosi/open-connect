namespace OpenConnect.Docs.Cloud;

public sealed class CloudExecutionClaim
{
    public Guid ReservationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid ClaimId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

public sealed record CloudExecutionClaimResult(string Status, Guid? ClaimId = null);

public interface ICloudExecutionClaimStore
{
    Task<CloudExecutionClaimResult> ClaimAsync(Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, CancellationToken cancellationToken);
}

public sealed class CloudExecutionClaimService(ICloudExecutionClaimStore store)
{
    public Task<CloudExecutionClaimResult> ClaimAsync(Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || applicationId == Guid.Empty || reservationId == Guid.Empty || !CloudDeploymentRegistry.ValidToken(capabilityId, 80)
            ? Task.FromResult(new CloudExecutionClaimResult("invalid_request"))
            : store.ClaimAsync(tenantId, applicationId, reservationId, capabilityId, cancellationToken);
}
