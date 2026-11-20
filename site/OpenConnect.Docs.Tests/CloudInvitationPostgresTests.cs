using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public class CloudInvitationPostgresTests
{
    [CloudPostgresFact]
    public async Task InvitationsAreBoundExpiringSingleUseAndRejectReinvitation()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var target = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var wrong = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, clock);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Invitation test", CancellationToken.None);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = owner.Issuer }).Build();
        var service = new CloudInvitationService(new EfCloudInvitationStore(factory, clock), config, clock);
        var request = new CreateCloudInvitationRequest(target.Issuer, target.SubjectId, "Developer");
        Assert.Equal("invalid_request", (await service.CreateAsync(owner, tenant.Id, new CreateCloudInvitationRequest("https://other.example", target.SubjectId, "Developer"), CancellationToken.None)).Status);
        Assert.Equal("not_found", (await service.CreateAsync(wrong, tenant.Id, request, CancellationToken.None)).Status);
        var created = await service.CreateAsync(owner, tenant.Id, request, CancellationToken.None);
        Assert.Equal("succeeded", created.Status);
        Assert.NotNull(created.Code);
        Assert.Equal("invalid_invitation", await service.AcceptAsync(wrong, created.Code, CancellationToken.None));
        var results = await Task.WhenAll(service.AcceptAsync(target, created.Code, CancellationToken.None), service.AcceptAsync(target, created.Code, CancellationToken.None));
        Assert.Single(results, x => x == "succeeded");
        Assert.Single(results, x => x == "invalid_invitation");
        Assert.Equal("invalid_invitation", await service.AcceptAsync(target, created.Code, CancellationToken.None));
        var members = await tenantStore.GetMembersAsync(owner, tenant.Id, CancellationToken.None);
        Assert.Contains(members!, x => x.SubjectId == target.SubjectId && x.Role == "Developer");
        Assert.Equal("forbidden", (await service.CreateAsync(target, tenant.Id, new CreateCloudInvitationRequest(wrong.Issuer, wrong.SubjectId, "Owner"), CancellationToken.None)).Status);
        Assert.Equal("succeeded", (await tenantStore.ChangeMemberAsync(owner, tenant.Id, target, null, 1, CancellationToken.None)).Status);
        Assert.Equal("reinvitation_unavailable", (await service.CreateAsync(owner, tenant.Id, request, CancellationToken.None)).Status);
        var expiring = await service.CreateAsync(owner, tenant.Id, new CreateCloudInvitationRequest(wrong.Issuer, wrong.SubjectId, "Developer"), CancellationToken.None);
        clock.Now = clock.Now.AddDays(8);
        Assert.Equal("invalid_invitation", await service.AcceptAsync(wrong, expiring.Code, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "invitation.accepted"));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(created.Code!)),
            (await verify.Invitations.SingleAsync(x => x.TenantId == tenant.Id && x.SubjectId == target.SubjectId)).SecretHash);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
