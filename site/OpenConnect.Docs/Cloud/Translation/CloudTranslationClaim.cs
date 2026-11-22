namespace OpenConnect.Docs.Cloud;

public interface ICloudTranslationClaimStore
{
    Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, CloudTranslationDeploymentMatchRequest deployment, CancellationToken cancellationToken);
}

public sealed class CloudTranslationClaim(ICloudTranslationClaimStore store)
{
    public Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, CloudTranslationDeploymentMatchRequest deployment, CancellationToken cancellationToken) =>
        identity is null || identity.CredentialId == Guid.Empty || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty ||
        reservationId == Guid.Empty || !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } ||
        workerFingerprint is not { Length: 32 } || !CloudTranslationDeploymentMatch.IsValid(deployment)
            ? Task.FromResult(new CloudExecutionClaimResult("invalid_request"))
            : store.ClaimAsync(identity, reservationId, fingerprintKeyId, fingerprint.ToArray(), workerFingerprint.ToArray(), deployment, cancellationToken);
}
