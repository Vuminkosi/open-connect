namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUIOutputEvidenceStore
{
    Task<bool> AppendForCompletedAsync(CloudCredentialIdentity identity, Guid requestId, Guid reservationId, Guid claimId,
        string outcome, string traceId, CancellationToken cancellationToken);
}
