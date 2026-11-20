namespace OpenConnect.Docs.Cloud;

public sealed record CloudUsageEntry(Guid ReservationId, Guid ApplicationId, Guid RequestId, string? CapabilityId,
    string? Unit, long Quantity, string Provenance, DateTimeOffset RecordedAt);
public sealed record CloudUsageCursor(DateTimeOffset RecordedAt, Guid ReservationId);
public sealed record CloudUsagePage(IReadOnlyList<CloudUsageEntry> Entries, CloudUsageCursor? Next);

public interface ICloudUsageReader
{
    Task<CloudUsagePage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudUsageCursor? before, int limit, CancellationToken cancellationToken);
    Task<CloudUsagePage?> ExportTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed class CloudUsageQueryService(ICloudUsageReader reader)
{
    public Task<CloudUsagePage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudUsageCursor? before, int limit, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || limit is < 1 or > 100 || from >= to || before?.ReservationId == Guid.Empty)
            throw new ArgumentException("Invalid usage query.");
        return reader.GetTenantAsync(subject, tenantId, from?.ToUniversalTime(), to?.ToUniversalTime(),
            before is null ? null : before with { RecordedAt = before.RecordedAt.ToUniversalTime() }, limit, cancellationToken);
    }

    public Task<CloudUsagePage?> ExportTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || from >= to || to - from > TimeSpan.FromDays(93))
            throw new ArgumentException("Invalid usage export period.");
        return reader.ExportTenantAsync(subject, tenantId, from.ToUniversalTime(), to.ToUniversalTime(), cancellationToken);
    }
}
