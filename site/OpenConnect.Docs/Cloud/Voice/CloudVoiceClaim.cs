namespace OpenConnect.Docs.Cloud;

public interface ICloudVoiceClaimStore
{
    Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, string modelId, CloudInferenceDeploymentMatch deployment, CancellationToken cancellationToken);
}

public sealed class CloudVoiceClaimService(ICloudVoiceClaimStore store)
{
    public Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, string modelId, CloudInferenceDeploymentMatch deployment, CancellationToken cancellationToken) =>
        identity is null || identity.CredentialId == Guid.Empty || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty ||
        reservationId == Guid.Empty || !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } ||
        workerFingerprint is not { Length: 32 } || !CloudInferenceModelPolicyService.ValidModelId(modelId) || deployment?.IsValid() != true
            ? Task.FromResult(new CloudExecutionClaimResult("invalid_request"))
            : store.ClaimAsync(identity, reservationId, fingerprintKeyId, fingerprint.ToArray(), workerFingerprint.ToArray(), modelId, deployment, cancellationToken);
}
