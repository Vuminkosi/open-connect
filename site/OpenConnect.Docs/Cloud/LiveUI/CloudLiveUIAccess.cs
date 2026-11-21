namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUIAccessStore
{
    Task<bool> IsClaimAllowedAsync(CloudCredentialIdentity identity, Guid reservationId, Guid claimId, CancellationToken cancellationToken);
}
