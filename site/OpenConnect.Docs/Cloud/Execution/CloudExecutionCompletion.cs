namespace OpenConnect.Docs.Cloud;

// A trusted gateway's observation of a terminal worker response. It is not a signed worker receipt.
public sealed class CloudExecutionCompletion
{
    public Guid ReservationId { get; set; }
    public Guid ClaimId { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed record CloudExecutionCompletionResult(string Status);

public interface ICloudExecutionCompletionStore
{
    Task<CloudExecutionCompletionResult> CompleteClassificationAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, string outcome, CancellationToken cancellationToken);
}

public sealed class CloudExecutionCompletionService(ICloudExecutionCompletionStore store)
{
    public Task<CloudExecutionCompletionResult> CompleteClassificationAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, string outcome, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || applicationId == Guid.Empty || reservationId == Guid.Empty || claimId == Guid.Empty || outcome != "completed"
            ? Task.FromResult(new CloudExecutionCompletionResult("invalid_request"))
            : store.CompleteClassificationAsync(tenantId, applicationId, reservationId, claimId, outcome, cancellationToken);
}
