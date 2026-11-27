using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudHostPrivacyObservation(Guid EventId, Guid ApplicationId, string Destination, bool Allowed,
    string Action, DateTimeOffset ReceivedAt, IReadOnlyList<CloudPrivacyCategoryCount> Categories)
{
    public string ReasonCode { get; init; } = "not_recorded";
    public string? TraceId { get; init; }
    public string? RequestId { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public string? PolicyId { get; init; }
    public string? PolicyRevision { get; init; }
    public string? IntegrityDigest { get; init; }
}
public sealed record CloudHostPrivacyStatistics(long OtherDestinations, long OutputDecisions, long Allowed, long Blocked,
    IReadOnlyList<CloudPrivacyCategoryTotal> Categories)
{
    public IReadOnlyList<CloudPrivacyActionTotal> Actions { get; init; } = [];
    public IReadOnlyList<CloudPrivacyReasonTotal> Reasons { get; init; } = [];
    public IReadOnlyList<CloudPrivacyCategoryTotal> OtherDestinationCategories { get; init; } = [];
    public IReadOnlyList<CloudPrivacyCategoryTotal> OutputCategories { get; init; } = [];
}
public sealed record CloudPrivacyActionTotal(string Action, long Count);
public sealed record CloudPrivacyReasonTotal(string ReasonCode, long Count);
internal static class CloudHostPrivacyReasonLabels
{
    public static string Describe(string reason) => reason switch
    {
        "allowed" => "Allowed by the host firewall",
        "inspection_failed" => "Inspection failed; the host blocked the decision",
        "enforcement_unavailable" => "Privacy enforcement was unavailable; the host blocked the decision",
        "sensitive_detected" => "Sensitive content was detected",
        "policy_blocked" => "Blocked by host policy",
        _ => "Not recorded",
    };
}
internal sealed class CloudHostPrivacyStatisticsAccumulator
{
    private long _otherDestinations, _outputDecisions, _allowed, _blocked;
    private readonly SortedDictionary<string, long> _categories = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _otherDestinationCategories = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _outputCategories = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _actions = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _reasons = new(StringComparer.Ordinal);

    public void Add(string destination, bool allowed, string action, string reason, string categoryCounts, long count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        using var categories = JsonDocument.Parse(categoryCounts);
        var parsed = CloudHostPrivacyReportParser.Categories(categories.RootElement);
        checked
        {
            if (destination == "Output")
                _outputDecisions += count;
            else
                _otherDestinations += count;
            if (allowed)
                _allowed += count;
            else
                _blocked += count;
            _actions[action] = _actions.GetValueOrDefault(action) + count;
            _reasons[reason] = _reasons.GetValueOrDefault(reason) + count;
            foreach (var item in parsed)
            {
                _categories[item.Key] = _categories.GetValueOrDefault(item.Key) + item.Value * count;
                var byDestination = destination == "Output" ? _outputCategories : _otherDestinationCategories;
                byDestination[item.Key] = byDestination.GetValueOrDefault(item.Key) + item.Value * count;
            }
        }
    }

    public CloudHostPrivacyStatistics Snapshot() => new(_otherDestinations, _outputDecisions, _allowed, _blocked,
        _categories.Select(item => new CloudPrivacyCategoryTotal(item.Key, item.Value)).ToArray())
    {
        Actions = _actions.Select(item => new CloudPrivacyActionTotal(item.Key, item.Value)).ToArray(),
        Reasons = _reasons.Select(item => new CloudPrivacyReasonTotal(item.Key, item.Value)).ToArray(),
        OtherDestinationCategories = _otherDestinationCategories.Select(item => new CloudPrivacyCategoryTotal(item.Key, item.Value)).ToArray(),
        OutputCategories = _outputCategories.Select(item => new CloudPrivacyCategoryTotal(item.Key, item.Value)).ToArray(),
    };
}
public sealed record CloudHostPrivacyObservationPage(IReadOnlyList<CloudHostPrivacyObservation> Observations, CloudPrivacyObservationCursor? Next)
{
    public CloudHostPrivacyStatistics? Statistics { get; init; }
}

public interface ICloudHostPrivacyObservationReader
{
    Task<CloudHostPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, CloudPrivacyObservationCursor? before,
        int limit, CancellationToken cancellationToken);
}

public interface ICloudHostPrivacyCaseReader
{
    Task<CloudHostPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken);
}

public sealed class CloudHostPrivacyObservationReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock)
    : ICloudHostPrivacyObservationReader, ICloudHostPrivacyCaseReader
{
    public async Task<CloudHostPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || caseId == Guid.Empty)
            throw new ArgumentException("Invalid host privacy case query.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(item => item.TenantId == tenantId &&
            item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId);
        var role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        CloudHostPrivacyObservation? observation = null;
        if (authorized)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e."EventId", e."ApplicationId", e."Destination", e."Allowed", e."Action", e."ReceivedAt", e."CategoryCounts"::text, e."ReasonCode",
                       e."TraceId", e."RequestId", e."OccurredAt", e."PolicyId", e."PolicyRevision", e."IntegrityDigest"
                FROM cloud.host_privacy_evidence e WHERE e."TenantId"=@tenant AND e."EventId"=@id
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId"=e."TenantId"
                    AND m."SubjectIssuer"=@issuer AND m."SubjectId"=@subject AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                """;
            Add(command, "tenant", tenantId);
            Add(command, "id", caseId);
            Add(command, "issuer", subject.Issuer);
            Add(command, "subject", subject.SubjectId);
            await using (var result = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await result.ReadAsync(cancellationToken))
                {
                    using var categories = JsonDocument.Parse(result.GetString(6));
                    var counts = CloudHostPrivacyReportParser.Categories(categories.RootElement)
                        .Select(item => new CloudPrivacyCategoryCount(item.Key, item.Value)).ToArray();
                    observation = new(result.GetGuid(0), result.GetGuid(1), result.GetString(2), result.GetBoolean(3),
                        result.GetString(4), result.GetFieldValue<DateTimeOffset>(5), counts)
                    {
                        ReasonCode = result.GetString(7),
                        TraceId = result.IsDBNull(8) ? null : result.GetString(8),
                        RequestId = result.IsDBNull(9) ? null : result.GetString(9),
                        OccurredAt = result.IsDBNull(10) ? null : result.GetFieldValue<DateTimeOffset>(10),
                        PolicyId = result.IsDBNull(11) ? null : result.GetString(11),
                        PolicyRevision = result.IsDBNull(12) ? null : result.GetString(12),
                        IntegrityDigest = result.IsDBNull(13) ? null : result.GetString(13),
                    };
                }
            }
            role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
            if (!authorized)
                observation = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "privacy.host_case.read",
            ResourceId = caseId.ToString("D"),
            Outcome = !authorized ? "forbidden" : observation is null ? "not_found" : "succeeded",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return observation;
    }

    public async Task<CloudHostPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId,
        CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || before?.Id == Guid.Empty || limit is < 1 or > 100)
            throw new ArgumentException("Invalid host privacy observation query.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(item => item.TenantId == tenantId &&
            item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId);
        var role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        var rows = new List<CloudHostPrivacyObservation>();
        CloudHostPrivacyStatistics? statistics = null;
        if (authorized)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e."EventId", e."ApplicationId", e."Destination", e."Allowed", e."Action", e."ReceivedAt", e."CategoryCounts"::text, e."ReasonCode",
                       e."TraceId", e."RequestId", e."OccurredAt", e."PolicyId", e."PolicyRevision", e."IntegrityDigest"
                FROM cloud.host_privacy_evidence e WHERE e."TenantId"=@tenant
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId"=e."TenantId"
                    AND m."SubjectIssuer"=@issuer AND m."SubjectId"=@subject AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                """ + (before is null ? "" : " AND (e.\"ReceivedAt\", e.\"EventId\") < (@before, @id)") +
                " ORDER BY e.\"ReceivedAt\" DESC, e.\"EventId\" DESC LIMIT @limit";
            Add(command, "tenant", tenantId);
            Add(command, "issuer", subject.Issuer);
            Add(command, "subject", subject.SubjectId);
            Add(command, "limit", limit + 1);
            if (before is not null)
            { Add(command, "before", before.OccurredAt.ToUniversalTime()); Add(command, "id", before.Id); }
            await using (var result = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await result.ReadAsync(cancellationToken))
                {
                    using var categories = JsonDocument.Parse(result.GetString(6));
                    var counts = CloudHostPrivacyReportParser.Categories(categories.RootElement)
                        .Select(item => new CloudPrivacyCategoryCount(item.Key, item.Value)).ToArray();
                    rows.Add(new(result.GetGuid(0), result.GetGuid(1), result.GetString(2), result.GetBoolean(3), result.GetString(4),
                        result.GetFieldValue<DateTimeOffset>(5), counts)
                    {
                        ReasonCode = result.GetString(7),
                        TraceId = result.IsDBNull(8) ? null : result.GetString(8),
                        RequestId = result.IsDBNull(9) ? null : result.GetString(9),
                        OccurredAt = result.IsDBNull(10) ? null : result.GetFieldValue<DateTimeOffset>(10),
                        PolicyId = result.IsDBNull(11) ? null : result.GetString(11),
                        PolicyRevision = result.IsDBNull(12) ? null : result.GetString(12),
                        IntegrityDigest = result.IsDBNull(13) ? null : result.GetString(13),
                    });
                }
            }
            await using (var totals = connection.CreateCommand())
            {
                totals.CommandText = """
                    SELECT e."Destination", e."Allowed", e."CategoryCounts"::text, count(*), e."Action", e."ReasonCode"
                    FROM cloud.host_privacy_evidence e WHERE e."TenantId"=@tenant
                      AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId"=e."TenantId"
                        AND m."SubjectIssuer"=@issuer AND m."SubjectId"=@subject AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                    GROUP BY e."Destination", e."Allowed", e."CategoryCounts", e."Action", e."ReasonCode"
                    """;
                Add(totals, "tenant", tenantId);
                Add(totals, "issuer", subject.Issuer);
                Add(totals, "subject", subject.SubjectId);
                var accumulator = new CloudHostPrivacyStatisticsAccumulator();
                await using (var result = await totals.ExecuteReaderAsync(cancellationToken))
                {
                    while (await result.ReadAsync(cancellationToken))
                        accumulator.Add(result.GetString(0), result.GetBoolean(1), result.GetString(4), result.GetString(5), result.GetString(2), result.GetInt64(3));
                }
                statistics = accumulator.Snapshot();
            }
            role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "privacy.host_observations.read",
            Outcome = authorized ? "succeeded" : "forbidden",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        if (!authorized)
            return null;
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();
        return new(rows, hasMore && last is not null ? new(last.ReceivedAt, last.EventId) : null) { Statistics = statistics };
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
