using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Counts recorded Cloud classification checks, not unique requests or host-side inspections.</summary>
public sealed record CloudPrivacyStatistics(long Allowed, long Blocked, long DetectorUnavailable)
{
    public IReadOnlyList<CloudPrivacyCategoryTotal> Categories { get; init; } = [];
    public long CategoryEvidenceUnavailable { get; init; }
}


internal sealed class CloudPrivacyStatisticsAccumulator
{
    private long _allowed, _blocked, _unavailable, _missing;
    private readonly SortedDictionary<string, long> _categories = new(StringComparer.Ordinal);

    public void Add(string outcome, string? categoryCounts)
    {
        switch (outcome)
        {
            case "allowed":
                _allowed++;
                break;
            case "privacy_blocked":
                _blocked++;
                break;
            case "detector_unavailable":
                _unavailable++;
                break;
        }
        var categories = CloudPrivacyCategories.Decode(categoryCounts, outcome);
        if (categories is null)
        { _missing++; return; }
        foreach (var category in categories)
            _categories[category.Category] = checked(_categories.GetValueOrDefault(category.Category) + category.Count);
    }

    public CloudPrivacyStatistics Snapshot() => new(_allowed, _blocked, _unavailable)
    {
        Categories = _categories.Select(item => new CloudPrivacyCategoryTotal(item.Key, item.Value)).ToArray(),
        CategoryEvidenceUnavailable = _missing,
    };
}

public interface ICloudPrivacyStatisticsReader
{
    Task<CloudPrivacyStatistics?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from,
        DateTimeOffset? to, CancellationToken cancellationToken);
}

public sealed class EfCloudPrivacyStatisticsReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock)
    : ICloudPrivacyStatisticsReader
{
    public async Task<CloudPrivacyStatistics?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from,
        DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || from >= to)
            throw new ArgumentException("Invalid privacy statistics query.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(item => item.TenantId == tenantId &&
            item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId);
        var role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        CloudPrivacyStatistics? result = null;
        if (authorized)
        {
            var membership = actor.Where(item => item.Role == "Owner" || item.Role == "Administrator" || item.Role == "SecurityAnalyst");
            var query = db.PrivacyEvidence.AsNoTracking().Where(item => item.TenantId == tenantId && membership.Any());
            if (from is not null)
                query = query.Where(item => item.OccurredAt >= from.Value.ToUniversalTime());
            if (to is not null)
                query = query.Where(item => item.OccurredAt < to.Value.ToUniversalTime());
            var totals = new CloudPrivacyStatisticsAccumulator();
            await foreach (var item in query.Select(item => new { item.Outcome, item.CategoryCounts })
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
                totals.Add(item.Outcome, item.CategoryCounts);
            result = totals.Snapshot();
            // A concurrent membership change must not turn a denied read into an apparent zero-count result.
            role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
            if (!authorized)
                result = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "privacy.statistics.read",
            Outcome = authorized ? "succeeded" : "forbidden",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}

public static class CloudPrivacyStatisticsEndpoints
{
    public static void MapCloudPrivacyStatisticsEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/security/statistics", async (Guid tenantId, DateTimeOffset? from,
            DateTimeOffset? to, ClaimsPrincipal principal, HttpContext context, ICloudPrivacyStatisticsReader reader,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty || from >= to)
                return Results.BadRequest();
            var result = await reader.GetTenantAsync(subject, tenantId, from, to, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
