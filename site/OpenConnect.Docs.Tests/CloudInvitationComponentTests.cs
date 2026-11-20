using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudInvitationComponentTests : BunitContext
{
    [Fact]
    public void OwnerCanCreateAndDismissCode()
    {
        var store = Configure();
        var component = Render<InvitationForm>(parameters => parameters.Add(x => x.TenantId, Guid.NewGuid()).Add(x => x.Enabled, true));
        component.Find("#invite-subject").Change("invited-subject");
        component.FindAll("button").Single(x => x.TextContent == "Create invitation").Click();
        Assert.Equal("invited-subject", store.Target?.SubjectId);
        Assert.StartsWith("oci_", component.Find("code").TextContent);
        component.FindAll("button").Single(x => x.TextContent == "Done").Click();
        Assert.Empty(component.FindAll("code"));
    }

    [Fact]
    public void AcceptanceUsesAuthenticatedSubjectAndClearsCode()
    {
        var store = Configure();
        var component = Render<AcceptInvitation>();
        component.Find("#invitation-code").Change($"oci_{Guid.NewGuid():N}_{new string('a', 43)}");
        component.FindAll("button").Single(x => x.TextContent == "Accept invitation").Click();
        Assert.Equal("current-subject", store.Actor?.SubjectId);
        Assert.Contains("Invitation accepted", component.Markup);
        Assert.Empty(component.FindAll("#invitation-code"));
    }

    private FakeStore Configure()
    {
        var store = new FakeStore();
        Services.AddSingleton<ICloudInvitationStore>(store);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<CloudInvitationService>();
        return store;
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "current-subject")], "test"))));
    }

    private sealed class FakeStore : ICloudInvitationStore
    {
        public CloudSubject? Actor { get; private set; }
        public CloudSubject? Target { get; private set; }
        public Task<string> CreateAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string role, Guid id, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        { Actor = actor; Target = target; return Task.FromResult("succeeded"); }
        public Task<string> AcceptAsync(CloudSubject actor, Guid id, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken)
        { Actor = actor; return Task.FromResult("succeeded"); }
    }
}
