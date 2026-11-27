using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudVoicePrivacyObservation(Guid Id, Guid ApplicationId, Guid RequestId, string Boundary,
    string Outcome, string TraceId, DateTimeOffset OccurredAt, string? ModelFingerprint);
public sealed record CloudVoicePrivacyCursor(DateTimeOffset OccurredAt, Guid Id, string Boundary);
public sealed record CloudVoicePrivacyPage(IReadOnlyList<CloudVoicePrivacyObservation> Observations, CloudVoicePrivacyCursor? Next);

public interface ICloudVoicePrivacyReader
{
    Task<CloudVoicePrivacyPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, CloudVoicePrivacyCursor? before, int limit, CancellationToken cancellationToken);
}

public sealed class EfCloudVoicePrivacyReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock) : ICloudVoicePrivacyReader
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid ApplicationId { get; set; }
        public Guid RequestId { get; set; }
        public string Boundary { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string TraceId { get; set; } = "";
        public DateTimeOffset OccurredAt { get; set; }
        public string? ModelFingerprint { get; set; }
    }

    public async Task<CloudVoicePrivacyPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, CloudVoicePrivacyCursor? before, int limit, CancellationToken cancellationToken)
    {
        if (subject is null || tenantId == Guid.Empty || limit is < 1 or > 100 ||
            before is not null && (before.Id == Guid.Empty || before.Boundary is not ("input" or "output")))
            throw new ArgumentException("Invalid voice privacy observation query.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await db.Memberships.FromSqlInterpolated($"""
            SELECT * FROM cloud.memberships WHERE "TenantId" = {tenantId} AND "SubjectIssuer" = {subject.Issuer}
                AND "SubjectId" = {subject.SubjectId} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var role = membership?.Role;
        CloudVoicePrivacyPage? result = null;
        if (role is "Owner" or "Administrator" or "SecurityAnalyst")
        {
            var hasCursor = before is not null;
            var time = before?.OccurredAt.ToUniversalTime() ?? DateTimeOffset.UnixEpoch;
            var id = before?.Id ?? Guid.Empty;
            var boundary = before?.Boundary ?? "";
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT v."Id", v."ApplicationId", v."RequestId", v."Boundary", v."Outcome", v."TraceId", v."OccurredAt", v."ModelFingerprint"
                FROM (
                    SELECT "Id", "TenantId", "ApplicationId", "RequestId", 'input'::text AS "Boundary", "Outcome", "TraceId", "OccurredAt", NULL::text AS "ModelFingerprint"
                    FROM cloud.voice_privacy_evidence
                    UNION ALL
                    SELECT "Id", "TenantId", "ApplicationId", "RequestId", 'output'::text AS "Boundary", "Outcome", "TraceId", "OccurredAt", "ModelFingerprint"::text AS "ModelFingerprint"
                    FROM cloud.voice_output_privacy_evidence
                ) v
                WHERE v."TenantId" = {tenantId} AND (NOT {hasCursor} OR (v."OccurredAt", v."Id", v."Boundary") < ({time}, {id}, {boundary}))
                ORDER BY v."OccurredAt" DESC, v."Id" DESC, v."Boundary" DESC LIMIT {limit + 1}
                """).ToListAsync(cancellationToken);
            var hasMore = rows.Count > limit;
            if (hasMore)
                rows.RemoveAt(rows.Count - 1);
            var last = rows.LastOrDefault();
            result = new(rows.Select(row => new CloudVoicePrivacyObservation(row.Id, row.ApplicationId, row.RequestId, row.Boundary,
                row.Outcome, row.TraceId, row.OccurredAt, row.ModelFingerprint)).ToArray(),
                hasMore && last is not null ? new(last.OccurredAt, last.Id, last.Boundary) : null);
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "voice.privacy.read",
            Outcome = result is null ? "forbidden" : "succeeded",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
