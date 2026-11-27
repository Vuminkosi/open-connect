using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class CloudLiveUIHostAuthorizerTests
{
    [Fact]
    public async Task AuthorizesOnlyOwnedCurrentPageCapabilities()
    {
        var fixture = new Fixture();
        Assert.Equal(fixture.Context, await fixture.Authorizer.AuthorizeAsync(fixture.Request));
        Assert.Equal(2, fixture.Sessions.Calls);
        Assert.Null(await fixture.Authorizer.AuthorizeAsync(fixture.Request with { SessionId = "other" }));
        Assert.Null(await fixture.Authorizer.AuthorizeAsync(fixture.Request with { RouteId = "other" }));
        Assert.Null(await fixture.Authorizer.AuthorizeAsync(fixture.Request with { ManifestHash = "old" }));
        Assert.Null(await fixture.Authorizer.AuthorizeAsync(fixture.Request with
        { Capabilities = [new("item", ["delete"]) { Label = "Open" }] }));
    }

    [Fact]
    public async Task RejectsSessionRevocationOrNavigationWhileReadingThePage()
    {
        var revoked = new Fixture();
        revoked.Sessions.RevokeAfterFirst = true;
        Assert.Null(await revoked.Authorizer.AuthorizeAsync(revoked.Request));
        var navigated = new Fixture();
        navigated.Current.ChangeAfterFirst = true;
        Assert.Null(await navigated.Authorizer.AuthorizeAsync(navigated.Request));
    }

    [Fact]
    public async Task RejectsIdentityAndManifestMismatches()
    {
        var identity = new Fixture();
        identity.Sessions.Value = identity.Context with { Destination = EgressDestination.Worker };
        Assert.Null(await identity.Authorizer.AuthorizeAsync(identity.Request));
        var manifest = new Fixture();
        manifest.Manifests.Value = manifest.Manifests.Value! with { ContentHash = "changed" };
        Assert.Null(await manifest.Authorizer.AuthorizeAsync(manifest.Request));
    }

    [Fact]
    public async Task RechecksRealAgentToolsWhenTargetPermissionIsRevoked()
    {
        var identity = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        var session = identity.Session.Value.ToString();
        var target = new LiveUITarget("item", LiveUITargetKind.Button, "Open", new HashSet<LiveUICapability> { LiveUICapability.Activate });
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "", [new LiveUIRoute("current", "/items", "Items", [target])]);
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var manifests = new MutableManifest(manifest);
        var current = new Current(session);
        var permissions = new Permissions();
        var settings = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(manifests, permissions, new UnusedRuntime(), settings, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(current, manifests, permissions, new LiveUIManifestValidator(), orchestrator, settings);
        var page = await tools.GetCurrentPageAsync();
        Assert.NotNull(page);
        var request = new LiveUIInferenceRequest("open", session, "current", manifest.SchemaVersion,
            LiveUIInferenceCapabilities.FromPage(page), manifest.ContentHash);
        var authorizer = new CloudLiveUIHostAuthorizer(current, manifests, new CloudLiveUIPageProvider(tools), new Sessions(identity));
        Assert.NotNull(await authorizer.AuthorizeAsync(request));
        permissions.DenyTarget = true;
        Assert.Null(await authorizer.AuthorizeAsync(request));
    }

    private sealed class Fixture
    {
        public EgressContext Context { get; } = new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        public Current Current { get; }
        public Sessions Sessions { get; }
        public Manifests Manifests { get; } = new();
        public LiveUIInferenceRequest Request { get; }
        public CloudLiveUIHostAuthorizer Authorizer { get; }

        public Fixture()
        {
            var page = new LiveUIAgentPage("current", "/items", "Items", "build", "hash",
                [new("item", "Button", "Open", new HashSet<LiveUICapability> { LiveUICapability.Activate }, false)]);
            Current = new(Context.Session.Value.ToString());
            Sessions = new(Context);
            Request = new("open", Context.Session.Value.ToString(), "current", "v1", LiveUIInferenceCapabilities.FromPage(page), "hash");
            Authorizer = new(Current, Manifests, new Pages(page), Sessions);
        }
    }

    private sealed class Current(string session) : ILiveUIAgentContextAccessor
    {
        private int _calls;
        public bool ChangeAfterFirst { get; set; }
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(new(session, ChangeAfterFirst && ++_calls > 1 ? "other" : "current") { CurrentPath = "/items" });
    }

    private sealed class Sessions(EgressContext context) : ICloudLiveUISessionContextProvider
    {
        public EgressContext? Value { get; set; } = context;
        public bool RevokeAfterFirst { get; set; }
        public int Calls { get; private set; }
        public ValueTask<EgressContext?> GetContextAsync(string sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RevokeAfterFirst && ++Calls > 1 ? null : IncrementIfNeeded());
        private EgressContext? IncrementIfNeeded() { if (!RevokeAfterFirst) Calls++; return Value; }
    }

    private sealed class Pages(LiveUIAgentPage page) : ICloudLiveUIPageProvider
    {
        public ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<LiveUIAgentPage?>(page);
    }

    private sealed class Manifests : ILiveUIManifestProvider
    {
        public LiveUIManifest? Value { get; set; } = new("v1", "build", "hash", []);
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Value);
    }

    private sealed class MutableManifest(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class Permissions : ILiveUIActionAuthorizer
    {
        public bool DenyTarget { get; set; }
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(!DenyTarget || context.Target is null, "fixture"));
    }

    private sealed class UnusedRuntime : ILiveUIRuntime
    {
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Authorization must not execute a browser action.");
    }
}
