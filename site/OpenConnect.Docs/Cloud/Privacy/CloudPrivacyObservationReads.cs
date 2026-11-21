namespace OpenConnect.Docs.Cloud;

public sealed record CloudPrivacyObservation(Guid Id, Guid ApplicationId, Guid RequestId, string Outcome, string TraceId, DateTimeOffset OccurredAt)
{
    public IReadOnlyList<CloudPrivacyCategoryCount>? Categories { get; init; }
}
public sealed record CloudPrivacyObservationCursor(DateTimeOffset OccurredAt, Guid Id);
public sealed record CloudPrivacyObservationPage(IReadOnlyList<CloudPrivacyObservation> Observations, CloudPrivacyObservationCursor? Next);

public interface ICloudPrivacyObservationReader
{
    Task<CloudPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken);
}

public sealed class CloudPrivacyObservationService(ICloudPrivacyObservationReader reader)
{
    public Task<CloudPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || limit is < 1 or > 100 || from >= to || before?.Id == Guid.Empty)
            throw new ArgumentException("Invalid privacy observation query.");
        return reader.GetTenantAsync(subject, tenantId, from?.ToUniversalTime(), to?.ToUniversalTime(),
            before is null ? null : before with { OccurredAt = before.OccurredAt.ToUniversalTime() }, limit, cancellationToken);
    }
}
