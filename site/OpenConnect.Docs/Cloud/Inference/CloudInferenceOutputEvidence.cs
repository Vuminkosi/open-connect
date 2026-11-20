namespace OpenConnect.Docs.Cloud;

// Evidence records an inspection decision, not the model output or a signed worker receipt.
public interface ICloudInferenceOutputEvidenceStore
{
    Task<bool> AppendForCompletedAsync(CloudCredentialIdentity identity, Guid requestId, Guid reservationId, Guid claimId,
        string outcome, string traceId, CancellationToken cancellationToken);
}
