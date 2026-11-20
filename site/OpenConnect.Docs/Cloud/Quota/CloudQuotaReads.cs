namespace OpenConnect.Docs.Cloud;

public sealed record CloudQuotaView(Guid Id, Guid ApplicationId, string CapabilityId, string Unit,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long Limit, long Consumed, long Reserved);

public sealed record CloudQuotaPage(IReadOnlyList<CloudQuotaView> Quotas, int Offset, int? NextOffset);

public interface ICloudQuotaReader
{
    Task<CloudQuotaPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, int offset, int limit, CancellationToken cancellationToken);
}

public sealed class CloudQuotaQueryService(ICloudQuotaReader reader)
{
    public Task<CloudQuotaPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, int offset, int limit, CancellationToken cancellationToken) =>
        reader.GetTenantAsync(subject, tenantId, offset, limit, cancellationToken);
}
