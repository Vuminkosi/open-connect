using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudMemberComponentTests : BunitContext
{
    [Fact]
    public void DirectoryEncodesIdentityValues()
    {
        Configure(new MemberStore([new CloudMemberSummary("https://issuer.example", "<script>alert(1)</script>", "Owner", DateTimeOffset.UtcNow)]));
        var component = Render<TenantMembers>(parameters => parameters.Add(x => x.TenantId, Guid.NewGuid()));
        Assert.Contains("&lt;script&gt;", component.Markup);
        Assert.DoesNotContain("<script>", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InaccessibleDirectoryDoesNotShowMemberData()
    {
        Configure(new MemberStore(null));
        var component = Render<TenantMembers>(parameters => parameters.Add(x => x.TenantId, Guid.NewGuid()));
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

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))));
    }

    private sealed class MemberStore(IReadOnlyList<CloudMemberSummary>? members) : ICloudTenantStore
    {
        public Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(members);
        public Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
