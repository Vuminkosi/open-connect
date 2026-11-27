using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public interface ICloudHostPrivacyEvidenceStore
{
    Task<bool> AppendAsync(CloudCredentialIdentity identity, CloudHostPrivacyReport report, CancellationToken cancellationToken);
}

/// <summary>Append-only host evidence storage with credential fencing and idempotent event IDs.</summary>
public sealed class CloudHostPrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudHostPrivacyEvidenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<bool> AppendAsync(CloudCredentialIdentity identity, CloudHostPrivacyReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(report);
        using var categories = JsonDocument.Parse(report.CategoryCounts);
        var body = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "openconnect.privacy-evidence.v1",
            eventId = report.EventId,
            tenantId = report.HostTenantId,
            destination = report.Destination.ToString(),
            allowed = report.Allowed,
            action = report.Action.ToString(),
            reasonCode = report.ReasonCode,
            categories = categories.RootElement,
            traceId = report.TraceId,
            requestId = report.RequestId,
            occurredAt = report.OccurredAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            policyId = report.PolicyId,
            policyRevision = report.PolicyRevision,
            enforcementAuthority = report.EnforcementAuthority,
            integrityDigest = report.IntegrityDigest,
        }, SerializerOptions);
        report = CloudHostPrivacyReportParser.Parse(body, identity);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var credential = connection.CreateCommand())
        {
            credential.Transaction = transaction;
            credential.CommandText = """
                SELECT 1 FROM cloud.credentials WHERE "Id"=@credential AND "TenantId"=@tenant AND "ApplicationId"=@application
                    AND "RevokedAt" IS NULL AND "ExpiresAt" > clock_timestamp() FOR UPDATE
                """;
            Add(credential, "credential", identity.CredentialId);
            Add(credential, "tenant", identity.TenantId);
            Add(credential, "application", identity.ApplicationId);
            if (await credential.ExecuteScalarAsync(cancellationToken) is null)
                return false;
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cloud.host_privacy_evidence
                    ("EventId", "TenantId", "ApplicationId", "HostTenantId", "Destination", "Allowed", "Action", "ReasonCode", "CategoryCounts",
                     "TraceId", "RequestId", "OccurredAt", "PolicyId", "PolicyRevision", "IntegrityDigest", "ReceivedAt")
                VALUES (@event, @tenant, @application, @host, @destination, @allowed, @action, @reason, CAST(@categories AS jsonb),
                     @traceId, @requestId, @occurredAt, @policyId, @policyRevision, @integrityDigest, clock_timestamp())
                ON CONFLICT ("EventId") DO NOTHING
                """;
            Bind(insert, identity, report);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT 1 FROM cloud.host_privacy_evidence WHERE "EventId"=@event AND "TenantId"=@tenant
                    AND "ApplicationId"=@application AND "HostTenantId"=@host AND "Destination"=@destination
                    AND "Allowed"=@allowed AND "Action"=@action AND "ReasonCode"=@reason AND "CategoryCounts"=CAST(@categories AS jsonb)
                    AND "TraceId" IS NOT DISTINCT FROM @traceId
                    AND "RequestId" IS NOT DISTINCT FROM @requestId
                    AND "OccurredAt" IS NOT DISTINCT FROM @occurredAt
                    AND "PolicyId" IS NOT DISTINCT FROM @policyId
                    AND "PolicyRevision" IS NOT DISTINCT FROM @policyRevision
                    AND "IntegrityDigest" IS NOT DISTINCT FROM @integrityDigest
                """;
            Bind(verify, identity, report);
            if (await verify.ExecuteScalarAsync(cancellationToken) is null)
                return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static void Bind(DbCommand command, CloudCredentialIdentity identity, CloudHostPrivacyReport report)
    {
        Add(command, "event", report.EventId);
        Add(command, "tenant", identity.TenantId);
        Add(command, "application", identity.ApplicationId);
        Add(command, "host", report.HostTenantId);
        Add(command, "destination", report.Destination.ToString());
        Add(command, "allowed", report.Allowed);
        Add(command, "action", report.Action.ToString());
        Add(command, "categories", report.CategoryCounts);
        Add(command, "reason", report.ReasonCode);
        Add(command, "traceId", (object?)report.TraceId ?? DBNull.Value);
        Add(command, "requestId", (object?)report.RequestId ?? DBNull.Value);
        Add(command, "occurredAt", report.OccurredAt.HasValue ? report.OccurredAt.Value.ToUniversalTime() : DBNull.Value);
        Add(command, "policyId", (object?)report.PolicyId ?? DBNull.Value);
        Add(command, "policyRevision", (object?)report.PolicyRevision ?? DBNull.Value);
        Add(command, "integrityDigest", (object?)report.IntegrityDigest ?? DBNull.Value);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
