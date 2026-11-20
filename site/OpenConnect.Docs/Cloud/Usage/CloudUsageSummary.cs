namespace OpenConnect.Docs.Cloud;

public sealed record CloudUsageDailyTotal(DateTime DayUtc, Guid ApplicationId, string? CapabilityId, string? Unit, long Quantity, long Records);
public sealed record CloudUsageSummary(IReadOnlyList<CloudUsageDailyTotal> Totals, bool LimitExceeded);

public interface ICloudUsageSummaryReader
{
    Task<CloudUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed class CloudUsageSummaryService(ICloudUsageSummaryReader reader)
{
    public Task<CloudUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || from >= to || to - from > TimeSpan.FromDays(93))
            throw new ArgumentException("Invalid usage summary period.");
        return reader.GetTenantAsync(subject, tenantId, from.ToUniversalTime(), to.ToUniversalTime(), cancellationToken);
    }
}
