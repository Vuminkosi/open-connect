using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Explicit sanitized privacy evidence coverage counts without fabricating a security score.</summary>
public sealed record CloudSecurityCoverage(
    long CloudEvidenceCount,
    long HostEvidenceCount,
    long CorrelatedTraceCount,
    long CloudTracesMissingHostReceipt,
    string CoverageStatus);

public sealed record CloudSecurityCoverageLookupResult(string Status, CloudSecurityCoverage? Coverage = null);

public interface ICloudSecurityCoverageReader
{
    Task<CloudSecurityCoverageLookupResult> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
}

public static class CloudSecurityCoverageClassifier
{
    public const string NoObservations = "no_observations";
    public const string CloudOnly = "cloud_only";
    public const string PartialHostCoverage = "partial_host_coverage";
    public const string FullHostCoverage = "full_host_coverage";

    public static CloudSecurityCoverage Classify(
        long cloudEvidenceCount,
        long hostEvidenceCount,
        long correlatedTraceCount,
        long cloudTracesMissingHostReceipt)
    {
        if (cloudEvidenceCount < 0 || hostEvidenceCount < 0 || correlatedTraceCount < 0 || cloudTracesMissingHostReceipt < 0)
            throw new ArgumentOutOfRangeException(nameof(cloudEvidenceCount), "Coverage counts cannot be negative.");

        string status;
        if (cloudEvidenceCount == 0 && hostEvidenceCount == 0)
        {
            status = NoObservations;
        }
        else if (cloudEvidenceCount > 0 && hostEvidenceCount == 0)
        {
            status = CloudOnly;
        }
        else if (cloudTracesMissingHostReceipt == 0)
        {
            status = FullHostCoverage;
        }
        else
        {
            status = PartialHostCoverage;
        }

        return new CloudSecurityCoverage(
            cloudEvidenceCount,
            hostEvidenceCount,
            correlatedTraceCount,
            cloudTracesMissingHostReceipt,
            status);
    }

    public static CloudSecurityCoverage FromTraces(
        long cloudEvidenceCount,
        long hostEvidenceCount,
        IEnumerable<string?> cloudTraceIds,
        IEnumerable<string?> hostTraceIds)
    {
        ArgumentNullException.ThrowIfNull(cloudTraceIds);
        ArgumentNullException.ThrowIfNull(hostTraceIds);

        var distinctCloudTraces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var traceId in cloudTraceIds)
        {
            if (!string.IsNullOrWhiteSpace(traceId))
                distinctCloudTraces.Add(traceId);
        }

        var distinctHostTraces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var traceId in hostTraceIds)
        {
            if (!string.IsNullOrWhiteSpace(traceId))
                distinctHostTraces.Add(traceId);
        }

        long correlated = 0;
        long missing = 0;
        foreach (var traceId in distinctCloudTraces)
        {
            if (distinctHostTraces.Contains(traceId))
                correlated++;
            else
                missing++;
        }

        return Classify(cloudEvidenceCount, hostEvidenceCount, correlated, missing);
    }
}

public static class CloudSecurityCoverageReader
{
    public static async Task<CloudSecurityCoverageLookupResult> GetTenantAsync(
        IDbContextFactory<CloudControlPlaneDbContext> factory,
        TimeProvider clock,
        CloudSubject subject,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(subject);
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Invalid security coverage query.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(item =>
            item.TenantId == tenantId && item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId);
        var role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";

        CloudSecurityCoverage? coverage = null;
        if (authorized)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH authorized_tenant AS (
                    SELECT 1 FROM cloud.memberships m
                    WHERE m."TenantId" = @tenant
                      AND m."SubjectIssuer" = @issuer
                      AND m."SubjectId" = @subject
                      AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst')
                    LIMIT 1
                ),
                cloud_counts AS (
                    SELECT
                        count(*)::bigint AS cloud_evidence_count,
                        count(DISTINCT e."TraceId") FILTER (WHERE e."TraceId" IS NOT NULL AND e."TraceId" <> '')::bigint AS distinct_cloud_traces,
                        count(DISTINCT e."TraceId") FILTER (
                            WHERE e."TraceId" IS NOT NULL AND e."TraceId" <> '' AND EXISTS (
                                SELECT 1 FROM cloud.host_privacy_evidence h
                                WHERE h."TenantId" = @tenant AND h."TraceId" = e."TraceId"
                            )
                        )::bigint AS correlated_trace_count
                    FROM cloud.privacy_evidence e
                    WHERE e."TenantId" = @tenant AND EXISTS (SELECT 1 FROM authorized_tenant)
                ),
                host_counts AS (
                    SELECT count(*)::bigint AS host_evidence_count
                    FROM cloud.host_privacy_evidence h
                    WHERE h."TenantId" = @tenant AND EXISTS (SELECT 1 FROM authorized_tenant)
                )
                SELECT
                    c.cloud_evidence_count,
                    h.host_evidence_count,
                    c.correlated_trace_count,
                    GREATEST(c.distinct_cloud_traces - c.correlated_trace_count, 0)::bigint AS missing_host_receipts
                FROM cloud_counts c
                CROSS JOIN host_counts h
                """;
            Add(command, "tenant", tenantId);
            Add(command, "issuer", subject.Issuer);
            Add(command, "subject", subject.SubjectId);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    coverage = CloudSecurityCoverageClassifier.Classify(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3));
                }
            }

            // Re-verify role after read to prevent TOCTOU downgrade from turning a denied read into zero counts.
            role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
            if (!authorized)
                coverage = null;
        }

        var status = authorized ? "succeeded" : role is null ? "not_found" : "forbidden";
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "privacy.coverage.read",
            Outcome = status == "succeeded" ? "succeeded" : "forbidden",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);

        return new CloudSecurityCoverageLookupResult(status, coverage);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
