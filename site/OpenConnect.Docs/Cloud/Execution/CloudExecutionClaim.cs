namespace OpenConnect.Docs.Cloud;

public sealed class CloudExecutionClaim
{
    public Guid ReservationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid? DeploymentId { get; set; }
    public string? Region { get; set; }
    public byte[]? WorkerPayloadFingerprint { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

public sealed record CloudExecutionClaimResult(string Status, Guid? ClaimId = null);

public interface ICloudExecutionClaimStore
{
    Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint, CloudClassificationDeploymentMatchRequest? deployment, CancellationToken cancellationToken, byte[]? workerPayloadFingerprint = null);
}

public sealed class CloudExecutionClaimService(ICloudExecutionClaimStore store)
{
    public Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint, CloudClassificationDeploymentMatchRequest? deployment, CancellationToken cancellationToken, byte[]? workerPayloadFingerprint = null) =>
        credentialId == Guid.Empty || tenantId == Guid.Empty || applicationId == Guid.Empty || reservationId == Guid.Empty || !CloudDeploymentRegistry.ValidToken(capabilityId, 80) ||
            !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } ||
            (workerPayloadFingerprint is not null && workerPayloadFingerprint.Length != 32) ||
            (capabilityId == "classification.classify" && !CloudClassificationDeploymentService.IsValid(deployment))
            ? Task.FromResult(new CloudExecutionClaimResult("invalid_request"))
            : store.ClaimAsync(credentialId, tenantId, applicationId, reservationId, capabilityId, fingerprintKeyId, fingerprint.ToArray(), deployment, cancellationToken, workerPayloadFingerprint?.ToArray());
}
