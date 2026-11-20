using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudMemberComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task OwnerCanChangeAndRemoveMemberThroughDirectory()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", "owner-" + Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject(owner.Issuer, "developer-" + Guid.NewGuid().ToString("N"));
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await store.CreateTenantAsync(owner, "Member UI", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        Services.AddSingleton<ICloudTenantStore>(store);
        Services.AddSingleton<CloudControlPlaneService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(owner.SubjectId));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = owner.Issuer }).Build());
        var component = Render<TenantMembers>(parameters => parameters.Add(x => x.TenantId, tenant.Id));
        component.WaitForAssertion(() => Assert.Contains(developer.SubjectId, component.Markup));
        var row = component.FindAll("li").Single(x => x.TextContent.Contains(developer.SubjectId));
        row.QuerySelector("select")!.Change("Administrator");
        row = component.FindAll("li").Single(x => x.TextContent.Contains(developer.SubjectId));
        row.QuerySelectorAll("button").Single(x => x.TextContent == "Save role").Click();
        component.WaitForAssertion(() => Assert.Contains("Role updated.", component.Markup));
        row = component.FindAll("li").Single(x => x.TextContent.Contains(developer.SubjectId));
        Assert.Contains("Administrator", row.TextContent);
        row.QuerySelectorAll("button").Single(x => x.TextContent == "Remove").Click();
        component.FindAll("button").Single(x => x.TextContent == "Confirm removal").Click();
        component.WaitForAssertion(() => Assert.Contains("Member removed.", component.Markup));
        Assert.DoesNotContain(developer.SubjectId, component.Markup);
    }

    [Fact]
    public void DirectoryEncodesIdentityValues()
    {
        var tenantId = Guid.NewGuid();
        Configure(new MemberStore(tenantId, [new CloudMemberSummary("https://issuer.example", "<script>alert(1)</script>", "Owner", DateTimeOffset.UtcNow, 1)]));
        var component = Render<TenantMembers>(parameters => parameters.Add(x => x.TenantId, tenantId));
        Assert.Contains("&lt;script&gt;", component.Markup);
        Assert.Empty(component.FindAll("script"));
    }

    [Fact]
    public void InaccessibleDirectoryDoesNotShowMemberData()
    {
        var tenantId = Guid.NewGuid();
        Configure(new MemberStore(tenantId, null));
        var component = Render<TenantMembers>(parameters => parameters.Add(x => x.TenantId, tenantId));
        Assert.Contains("not found or you do not have access", component.Markup);
        Assert.DoesNotContain("Identity provider:", component.Markup);
    }

    private void Configure(ICloudTenantStore store)
    {
        Services.AddSingleton(store);
        Services.AddSingleton<CloudControlPlaneService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
    }

    private sealed class TestAuthentication(string subjectId = "owner") : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subjectId)], "test"))));
    }

    private sealed class MemberStore(Guid tenantId, IReadOnlyList<CloudMemberSummary>? members) : ICloudTenantStore
    {
        public Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(members);
        public Task<CloudMemberChangeResult> ChangeMemberAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string? role, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudTenantSummary>>([new CloudTenantSummary(tenantId, "Tenant", "Owner", DateTimeOffset.UtcNow)]);
        public Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
