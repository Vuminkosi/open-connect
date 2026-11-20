using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferenceModelPolicyStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferenceModelPolicyStore
{
    private sealed class PolicyRow
    {
        public required string Models { get; set; }
        public int Revision { get; set; }
    }

    public async Task<CloudInferenceModelPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var membership = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId);
        if (!await membership.AnyAsync(cancellationToken)) return new("not_found");
        var rows = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT p."AllowedModels"::text AS "Models", p."Revision" FROM cloud.inference_model_policies p
            WHERE p."TenantId" = {tenantId} AND EXISTS (SELECT 1 FROM cloud.memberships m
                WHERE m."TenantId" = {tenantId} AND m."SubjectIssuer" = {actor.Issuer} AND m."SubjectId" = {actor.SubjectId})
            """).ToListAsync(cancellationToken);
        if (!await membership.AnyAsync(cancellationToken)) return new("not_found");
        if (rows.Count == 0) return new("succeeded", new([], 0));
        var models = Parse(rows[0].Models);
        return models is null ? new("unavailable") : new("succeeded", new(models, rows[0].Revision));
    }

    public async Task<bool> IsAllowedAsync(Guid tenantId, string modelId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQuery<PolicyRow>($"SELECT \"AllowedModels\"::text AS \"Models\", \"Revision\" FROM cloud.inference_model_policies WHERE \"TenantId\" = {tenantId}")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 && Parse(rows[0].Models)?.Contains(modelId, StringComparer.Ordinal) == true;
    }

    public async Task<CloudInferenceModelPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[] models, int revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var membership = await db.Memberships.FromSqlInterpolated($"SELECT * FROM cloud.memberships WHERE \"TenantId\" = {tenantId} AND \"SubjectIssuer\" = {actor.Issuer} AND \"SubjectId\" = {actor.SubjectId} FOR SHARE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (membership is null) { await RecordRejectedAsync(actor, tenantId, "not_found", cancellationToken); return new("not_found"); }
            if (membership.Role is not ("Owner" or "Administrator")) { await RecordRejectedAsync(actor, tenantId, "forbidden", cancellationToken); return new("forbidden"); }
            var rows = await db.Database.SqlQuery<PolicyRow>($"SELECT \"AllowedModels\"::text AS \"Models\", \"Revision\" FROM cloud.inference_model_policies WHERE \"TenantId\" = {tenantId} FOR UPDATE").ToListAsync(cancellationToken);
            if ((rows.Count == 0 ? 0 : rows[0].Revision) != revision) { await RecordRejectedAsync(actor, tenantId, "conflict", cancellationToken); return new("conflict"); }
            var json = JsonSerializer.Serialize(models);
            if (rows.Count == 0)
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.inference_model_policies (\"TenantId\", \"AllowedModels\", \"Revision\") VALUES ({tenantId}, {json}::jsonb, 1)", cancellationToken);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_model_policies SET \"AllowedModels\" = {json}::jsonb WHERE \"TenantId\" = {tenantId}", cancellationToken);
            db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
                ActorRole = membership.Role, Action = "tenant.inference_models.changed", ResourceId = tenantId.ToString("D"), Outcome = "succeeded",
                TraceId = Activity.Current?.TraceId.ToString(), OccurredAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("succeeded", new(models, revision + 1));
        }
        catch (Exception exception) when (IsConflict(exception)) { await RecordRejectedAsync(actor, tenantId, "conflict", cancellationToken); return new("conflict"); }
    }

    public async Task RecordRejectedAsync(CloudSubject actor, Guid tenantId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = null, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
            ActorRole = "AuthenticatedUser", Action = "tenant.inference_models.changed", ResourceId = tenantId.ToString("D"), Outcome = outcome,
            TraceId = Activity.Current?.TraceId.ToString(), OccurredAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string[]? Parse(string json)
    {
        try
        {
            var models = JsonSerializer.Deserialize<string[]>(json);
            return models is not null && models.Length <= 64 && models.All(CloudInferenceModelPolicyService.ValidModelId) &&
                models.Distinct(StringComparer.Ordinal).Count() == models.Length ? models : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }
}
