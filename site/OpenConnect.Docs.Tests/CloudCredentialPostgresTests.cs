using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public class CloudCredentialPostgresTests
{
    [CloudPostgresFact]
    public async Task IssuanceAndConcurrentScopeDisableUseOneSnapshot()
    {
        var connection = Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE");
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Credential test", CancellationToken.None);
        var app = (await tenantStore.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capabilityId = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capabilityId, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capabilityId, PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var normal = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var stranger = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        Assert.Equal("not_found", (await normal.CreateAsync(stranger, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None)).Status);
        Assert.True((await normal.ListAsync(stranger, tenant.Id, app.Id, CancellationToken.None)).Forbidden);
        var rolledBackId = Guid.NewGuid();
        await using (var session = await new EfCloudCredentialStore(factory).OpenAsync(transactional: true, CancellationToken.None))
        {
            session.Add(new CloudCredential { Id = rolledBackId, TenantId = tenant.Id, ApplicationId = app.Id,
                Environment = "Development", KeyHash = new byte[32], Fingerprint = "rollback", Scopes = "[]", IssuedAt = DateTimeOffset.UtcNow });
            session.AddAudit(new CloudSubject(null!, owner.SubjectId), tenant.Id, "Owner", "test.failure", rolledBackId, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<DbUpdateException>(() => session.CommitAsync(CancellationToken.None));
        }
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.Credentials.AnyAsync(x => x.Id == rolledBackId));
        var noExpiry = await normal.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], null, CancellationToken.None);
        Assert.Equal("invalid_expiration", noExpiry.Status);
        var initial = await normal.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        Assert.Equal("succeeded", initial.Status);
        Assert.NotNull(await normal.ValidateAsync(initial.Secret, CancellationToken.None));
        var altered = initial.Secret![..^1] + (initial.Secret[^1] == 'A' ? 'B' : 'A');
        Assert.Null(await normal.ValidateAsync(altered, CancellationToken.None));
        Assert.Null(await normal.ValidateAsync(initial.Secret.Replace("oc_dev_", "oc_prd_", StringComparison.Ordinal), CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var storedHash = (await db.Credentials.AsNoTracking().SingleAsync(x => x.Id == initial.Credential!.Id)).KeyHash;
            Assert.Equal(32, storedHash.Length);
            Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(initial.Secret), storedHash);
        }
        var raceCredential = await normal.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        var credentialGate = new SelectionReadGate("FROM cloud.credentials");
        var credentialOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).AddInterceptors(credentialGate).Options;
        IDbContextFactory<CloudControlPlaneDbContext> credentialFactory = new TestFactory(credentialOptions);
        var rotating = new CloudCredentialService(new EfCloudCredentialStore(credentialFactory), TimeProvider.System)
            .RotateAsync(owner, tenant.Id, app.Id, raceCredential.Credential!.Id, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        try
        {
            await credentialGate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("succeeded", await normal.RevokeAsync(owner, tenant.Id, app.Id, raceCredential.Credential.Id, CancellationToken.None));
        }
        finally { credentialGate.Resume.TrySetResult(); }
        var rejected = false;
        try { rejected = (await rotating).Status == "credential_revoked"; }
        catch (Exception exception) when (HasSerializationFailure(exception)) { rejected = true; }
        Assert.True(rejected);
        Assert.Null(await normal.ValidateAsync(raceCredential.Secret, CancellationToken.None));
        var validationCredential = await normal.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        var validationGate = new SelectionReadGate("FROM cloud.credentials");
        var validationOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).AddInterceptors(validationGate).Options;
        IDbContextFactory<CloudControlPlaneDbContext> validationFactory = new TestFactory(validationOptions);
        var validating = new CloudCredentialService(new EfCloudCredentialStore(validationFactory), TimeProvider.System)
            .ValidateAsync(validationCredential.Secret, CancellationToken.None);
        try
        {
            await validationGate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("succeeded", await normal.RevokeAsync(owner, tenant.Id, app.Id, validationCredential.Credential!.Id, CancellationToken.None));
        }
        finally { validationGate.Resume.TrySetResult(); }
        Assert.Null(await validating);
        var rotation = await normal.RotateAsync(owner, tenant.Id, app.Id, initial.Credential!.Id, null, CancellationToken.None);
        Assert.Equal("invalid_expiration", rotation.Status);
        Assert.NotNull(await normal.ValidateAsync(initial.Secret, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
            await db.Credentials.Where(x => x.Id == initial.Credential.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, (DateTimeOffset?)null));
        Assert.Null(await normal.ValidateAsync(initial.Secret, CancellationToken.None));
        var gate = new SelectionReadGate();
        var gatedOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).AddInterceptors(gate).Options;
        IDbContextFactory<CloudControlPlaneDbContext> gatedFactory = new TestFactory(gatedOptions);
        var service = new CloudCredentialService(new EfCloudCredentialStore(gatedFactory), TimeProvider.System);
        var issuance = service.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        try
        {
            await gate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await factory.CreateDbContextAsync();
            await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capabilityId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
        }
        finally { gate.Resume.TrySetResult(); }

        try { Assert.Equal("succeeded", (await issuance).Status); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        Assert.True(gate.ReadInTransaction);
        var denied = await normal.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        Assert.Equal("capability_disabled", denied.Status);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private static bool HasSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } ||
        exception.InnerException is not null && HasSerializationFailure(exception.InnerException);

    private sealed class SelectionReadGate(string queryMarker = "FROM cloud.application_capability_selections") : DbCommandInterceptor
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadInTransaction { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(queryMarker, StringComparison.Ordinal))
            {
                ReadInTransaction = eventData.Context?.Database.CurrentTransaction is not null;
                ReadStarted.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
