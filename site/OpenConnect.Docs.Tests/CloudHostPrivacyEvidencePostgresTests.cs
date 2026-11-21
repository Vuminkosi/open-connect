using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudHostPrivacyEvidencePostgresTests
{
    [CloudPostgresFact]
    public async Task Replays_are_idempotent_and_credential_revocation_fences_new_reports()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var subject = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(subject, "Host evidence", CancellationToken.None);
        var app = (await tenants.CreateApplicationAsync(subject, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var id = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Credentials.Add(new CloudCredential { Id = id, TenantId = tenant.Id, ApplicationId = app.Id,
                Environment = "Development", KeyHash = new byte[32], Fingerprint = "fingerprint", Scopes = "[]",
                IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var identity = new CloudCredentialIdentity(id, tenant.Id, app.Id, "Development", new HashSet<string>());
        var context = new OpenConnect.Privacy.EgressContext(new(OpenConnect.Abstractions.Ulid.Parse(ToUlid(tenant.Id))),
            new(OpenConnect.Abstractions.Ulid.NewUlid()), new(OpenConnect.Abstractions.Ulid.NewUlid()), OpenConnect.Privacy.EgressDestination.Output);
        var eventId = Guid.NewGuid();
        var evidence = OpenConnect.Privacy.HostPrivacyEvidence.Create(eventId, context,
            new(true, "masked", [new(0, 4, "EMAIL")], OpenConnect.Abstractions.SecurityPolicyAction.Mask, "reason"));
        var json = JsonSerializer.SerializeToElement(new { schemaVersion = "openconnect.privacy-evidence.v1", eventId,
            tenantId = evidence.TenantId, destination = evidence.Destination.ToString(), allowed = evidence.Allowed,
            action = evidence.Action.ToString(), reasonCode = evidence.ReasonCode, categories = evidence.Categories });
        var report = CloudHostPrivacyReportParser.Parse(json, identity);
        var store = new CloudHostPrivacyEvidenceStore(factory);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.AppendAsync(identity, report, CancellationToken.None)));
        Assert.All(results, Assert.True);
        Assert.False(await store.AppendAsync(identity, report with { Allowed = false, ReasonCode = "policy_blocked" }, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var invalidReason in new[] { "allowed", "sensitive_detected" })
            {
                const string emptyCategories = "{}";
                var violation = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO cloud.host_privacy_evidence
                        ("EventId", "TenantId", "ApplicationId", "HostTenantId", "Destination", "Allowed", "Action", "ReasonCode", "CategoryCounts", "ReceivedAt")
                    VALUES ({Guid.NewGuid()}, {tenant.Id}, {app.Id}, {ToUlid(tenant.Id)}, {"Output"}, {false}, {"Block"}, {invalidReason}, CAST({emptyCategories} AS jsonb), clock_timestamp())
                    """));
                Assert.Equal("23514", violation.SqlState);
                Assert.Equal(invalidReason == "allowed" ? "ck_host_privacy_reason_decision" : "ck_host_privacy_reason_evidence", violation.ConstraintName);
            }
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM cloud.host_privacy_evidence WHERE \"EventId\"={eventId}"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cloud.host_privacy_evidence SET \"Allowed\"=false WHERE \"EventId\"={eventId}"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.host_privacy_evidence"));
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var credential = await db.Credentials.SingleAsync(item => item.Id == id);
            credential.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.False(await store.AppendAsync(identity, report with { EventId = Guid.NewGuid() }, CancellationToken.None));
    }

    private static string ToUlid(Guid value)
    {
        UInt128 number = 0;
        foreach (var item in value.ToByteArray(bigEndian: true)) number = (number << 8) | item;
        Span<char> encoded = stackalloc char[26];
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (var i = encoded.Length - 1; i >= 0; i--) { encoded[i] = alphabet[(int)(number & 31)]; number >>= 5; }
        return new string(encoded);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
