using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudMachineAdmissionPostgresTests
{
    [CloudPostgresFact]
    public async Task CredentialScopeAndLiveEntitlementsAreRequired()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Machine admission", CancellationToken.None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capabilityId = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capabilityId, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capabilityId, PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var issued = await credentials.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        Assert.Equal("succeeded", issued.Status);
        var reader = new EfCloudMachineCapabilityReader(factory, TimeProvider.System);
        var admission = new CloudMachineAuthorizationService(credentials, reader, TimeProvider.System);
        Assert.Equal("unauthorized", (await admission.AuthorizeAsync("invalid", capabilityId, CancellationToken.None)).Code);
        Assert.Equal("forbidden", (await admission.AuthorizeAsync(issued.Secret, "other.capability", CancellationToken.None)).Code);
        var accepted = await admission.AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None);
        Assert.True(accepted.Authorized);
        Assert.Equal(tenant.Id, accepted.Identity!.TenantId);
        Assert.Equal(app.Id, accepted.Identity.ApplicationId);
        var raceCredential = await credentials.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        var gate = new GatedReader(reader);
        var pending = new CloudMachineAuthorizationService(credentials, gate, TimeProvider.System)
            .AuthorizeAsync(raceCredential.Secret, capabilityId, CancellationToken.None);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, app.Id, raceCredential.Credential!.Id, CancellationToken.None));
        }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal("unauthorized", (await pending).Code);
        var expiringCredential = await credentials.CreateAsync(owner, tenant.Id, app.Id, [capabilityId], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        var expiryGate = new GatedReader(reader);
        var pendingExpiry = new CloudMachineAuthorizationService(credentials, expiryGate, TimeProvider.System)
            .AuthorizeAsync(expiringCredential.Secret, capabilityId, CancellationToken.None);
        try
        {
            await expiryGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await factory.CreateDbContextAsync();
            await db.Credentials.Where(x => x.Id == expiringCredential.Credential!.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        finally { expiryGate.Resume.TrySetResult(); }
        Assert.Equal("unauthorized", (await pendingExpiry).Code);
        var unavailable = await new CloudMachineAuthorizationService(credentials, new FailingReader(), TimeProvider.System)
            .AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None);
        Assert.False(unavailable.Authorized);
        Assert.Equal("capability_unavailable", unavailable.Code);
        Assert.Null(unavailable.Identity);
        Assert.Null(await reader.GetCurrentAsync(accepted.Identity with { TenantId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Null(await reader.GetCurrentAsync(accepted.Identity with { ApplicationId = Guid.NewGuid() }, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
            await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capabilityId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
        Assert.Equal("capability_disabled", (await admission.AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None)).Code);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capabilityId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
            await db.TenantEntitlements.Where(x => x.TenantId == tenant.Id && x.CapabilityId == capabilityId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, false));
        }
        Assert.Equal("capability_not_entitled", (await admission.AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None)).Code);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Credentials.Where(x => x.Id == issued.Credential!.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal("unauthorized", (await admission.AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None)).Code);
        Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, app.Id, issued.Credential!.Id, CancellationToken.None));
        Assert.Equal("unauthorized", (await admission.AuthorizeAsync(issued.Secret, capabilityId, CancellationToken.None)).Code);
        Assert.Null(await reader.GetCurrentAsync(accepted.Identity, CancellationToken.None));
    }

    private sealed class GatedReader(ICloudMachineCapabilityReader inner) : ICloudMachineCapabilityReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await inner.GetCurrentAsync(identity, cancellationToken);
        }
    }

    private sealed class FailingReader : ICloudMachineCapabilityReader
    {
        public Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("private diagnostic");
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
