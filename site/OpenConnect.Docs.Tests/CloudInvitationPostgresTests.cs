using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
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
        var administrator = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, clock);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Invitation test", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = administrator.Issuer,
                SubjectId = administrator.SubjectId, Role = "Administrator", CreatedAt = clock.Now });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = owner.Issuer, ["OpenConnect:Cloud:Identity:Enabled"] = "true" }).Build();
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
        Assert.Null(await service.ListAsync(target, tenant.Id, CancellationToken.None));
        Assert.Equal("forbidden", (await service.CreateAsync(target, tenant.Id, new CreateCloudInvitationRequest(wrong.Issuer, wrong.SubjectId, "Owner"), CancellationToken.None)).Status);
        Assert.Equal("succeeded", (await tenantStore.ChangeMemberAsync(owner, tenant.Id, target, null, 1, CancellationToken.None)).Status);
        Assert.Equal("reinvitation_unavailable", (await service.CreateAsync(owner, tenant.Id, request, CancellationToken.None)).Status);
        var racing = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var revocable = await service.CreateAsync(owner, tenant.Id, new CreateCloudInvitationRequest(racing.Issuer, racing.SubjectId, "Developer"), CancellationToken.None);
        var invitations = await service.ListAsync(owner, tenant.Id, CancellationToken.None);
        var pending = Assert.Single(invitations!, x => x.SubjectId == racing.SubjectId);
        Assert.Null(await service.ListAsync(wrong, tenant.Id, CancellationToken.None));
        Assert.Null(await service.ListAsync(administrator, tenant.Id, CancellationToken.None));
        Assert.Equal("forbidden", (await service.RevokeAsync(administrator, tenant.Id, pending.Id, 1, CancellationToken.None)).Status);
        Assert.Equal("conflict", (await service.RevokeAsync(owner, tenant.Id, pending.Id, 2, CancellationToken.None)).Status);
        var gate = new InvitationReadGate();
        var gatedOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).AddInterceptors(gate).Options;
        var gated = new CloudInvitationService(new EfCloudInvitationStore(new TestFactory(gatedOptions), clock), config, clock);
        var acceptance = gated.AcceptAsync(racing, revocable.Code, CancellationToken.None);
        try
        {
            await gate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("succeeded", (await service.RevokeAsync(owner, tenant.Id, pending.Id, 1, CancellationToken.None)).Status);
        }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal("invalid_invitation", await acceptance);
        Assert.Equal("invalid_invitation", await service.AcceptAsync(racing, revocable.Code, CancellationToken.None));
        Assert.Equal("invitation_closed", (await service.RevokeAsync(owner, tenant.Id, pending.Id, 2, CancellationToken.None)).Status);
        var expiring = await service.CreateAsync(owner, tenant.Id, new CreateCloudInvitationRequest(wrong.Issuer, wrong.SubjectId, "Developer"), CancellationToken.None);
        clock.Now = clock.Now.AddDays(8);
        Assert.Equal("invalid_invitation", await service.AcceptAsync(wrong, expiring.Code, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "invitation.accepted"));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "invitation.revoked"));
        Assert.False(await verify.Memberships.AnyAsync(x => x.TenantId == tenant.Id && x.SubjectId == racing.SubjectId));
        Assert.NotEqual(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revocable.Code!)),
            (await verify.Invitations.SingleAsync(x => x.Id == pending.Id)).SecretHash);
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

    private sealed class InvitationReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM cloud.invitations", StringComparison.Ordinal))
            {
                ReadStarted.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
