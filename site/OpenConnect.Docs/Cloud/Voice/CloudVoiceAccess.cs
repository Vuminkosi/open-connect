namespace OpenConnect.Docs.Cloud;

public interface ICloudVoiceAccessStore
{
    Task<bool> IsClaimAllowedAsync(CloudCredentialIdentity identity, Guid reservationId, Guid claimId, CancellationToken cancellationToken);
}
