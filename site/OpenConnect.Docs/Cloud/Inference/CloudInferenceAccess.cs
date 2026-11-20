namespace OpenConnect.Docs.Cloud;

public interface ICloudInferenceAccessStore
{
    Task<bool> IsClaimAllowedAsync(CloudCredentialIdentity identity, Guid reservationId, Guid claimId, CancellationToken cancellationToken);
}
