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
        component.FindAll("button").Single(x => x.TextContent == "Revoke").Click();
        component.FindAll("button").Single(x => x.TextContent == "Confirm revocation").Click();
        Assert.Contains("Invitation revoked.", component.Markup);
        Assert.Contains("Revoked", component.Markup);
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

    [Fact]
    public void OwnerCanInviteGoogleSubjectWhenGoogleIsEnabled()
    {
        var store = Configure(googleEnabled: true);
        var component = Render<InvitationForm>(parameters => parameters.Add(x => x.TenantId, Guid.NewGuid()).Add(x => x.Enabled, true));
        component.Find("#invite-provider").Change("google");
        component.Find("#invite-subject").Change("google-subject");
        component.FindAll("button").Single(x => x.TextContent == "Create invitation").Click();
        Assert.Equal(new CloudSubject(CloudIdentity.GoogleIssuer, "google-subject"), store.Target);
    }

    [Fact]
    public async Task DisabledProviderCannotReceiveInvitations()
    {
        var store = Configure();
        var service = Services.GetRequiredService<CloudInvitationService>();
        var result = await service.CreateAsync(new CloudSubject("https://issuer.example", "owner"), Guid.NewGuid(),
            new CreateCloudInvitationRequest(CloudIdentity.GoogleIssuer, "google-subject", "Developer"), CancellationToken.None);
        Assert.Equal("invalid_request", result.Status);
        Assert.Null(store.Target);
    }

    private FakeStore Configure(bool googleEnabled = false)
    {
        var store = new FakeStore();
        Services.AddSingleton<ICloudInvitationStore>(store);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example", ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Google:Enabled"] = googleEnabled.ToString() }).Build());
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
        public Task AuditInvalidAsync(CloudSubject actor, Guid? tenantId, CloudInvitationOperation operation, CancellationToken cancellationToken) => Task.CompletedTask;
        public CloudSubject? Actor { get; private set; }
        public CloudSubject? Target { get; private set; }
        private CloudInvitationSummary? _invitation;
        public Task<string> CreateAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string role, Guid id, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        { Actor = actor; Target = target; _invitation = new CloudInvitationSummary(id, target.SubjectId, role, expiresAt, null, null, 1); return Task.FromResult("succeeded"); }
        public Task<string> AcceptAsync(CloudSubject actor, Guid id, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken)
        { Actor = actor; return Task.FromResult("succeeded"); }
        public Task<IReadOnlyList<CloudInvitationSummary>?> ListAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudInvitationSummary>?>(_invitation is null ? [] : [_invitation]);
        public Task<CloudInvitationChangeResult> RevokeAsync(CloudSubject actor, Guid tenantId, Guid invitationId, int expectedRevision, CancellationToken cancellationToken) =>
            Task.FromResult(Revoke(expectedRevision));
        private CloudInvitationChangeResult Revoke(int revision)
        {
            _invitation = _invitation! with { RevokedAt = DateTimeOffset.UtcNow, Revision = revision + 1 };
            return new("succeeded", revision + 1);
        }
    }
}
