using System.Security.Claims;
using OpenConnect.Abstractions;
using OpenConnect.Docs.AssistantHost.LiveUI;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Docs.AssistantHost.Tests;

public sealed class DocumentationLiveUIPlannerTests
{
    [Fact]
    public async Task ScreensIntentBeforeWorkerAndValidatesReturnedPlan()
    {
        var fixture = new Fixture();
        var result = await fixture.Planner.GenerateAsync(fixture.Envelope("private"), fixture.Owner, CancellationToken.None);
        Assert.Equal(200, result.Status);
        Assert.Equal("[redacted]", fixture.Worker.LastRequest!.Intent);
        Assert.Equal("completed", result.Response!.Result.Outcome);
        Assert.Contains(EgressDestination.Worker, fixture.Firewall.Destinations);
        Assert.Contains(EgressDestination.Output, fixture.Firewall.Destinations);
    }

    [Fact]
    public async Task DifferentVisitorCannotPlanAgainstAnotherSession()
    {
        var fixture = new Fixture();
        var other = Identity(fixture.Session.TenantId.Value, Ulid.NewUlid());
        var result = await fixture.Planner.GenerateAsync(fixture.Envelope("hello"), other, CancellationToken.None);
        Assert.Equal(404, result.Status);
        Assert.Null(fixture.Worker.LastRequest);
    }

    [Fact]
    public async Task DisabledFirewallPreventsWorkerDispatch()
    {
        var fixture = new Fixture();
        fixture.Firewall.Enabled = false;
        var result = await fixture.Planner.GenerateAsync(fixture.Envelope("hello"), fixture.Owner, CancellationToken.None);
        Assert.Equal(403, result.Status);
        Assert.Null(fixture.Worker.LastRequest);
    }

    [Fact]
    public async Task InvalidCorrelationAndDuplicateCapabilitiesNeverReachWorker()
    {
        var fixture = new Fixture();
        var input = fixture.Envelope("hello");
        Assert.Equal(400, (await fixture.Planner.GenerateAsync(input with { RequestId = "invalid" }, fixture.Owner, CancellationToken.None)).Status);
        var repeated = input with { Request = input.Request with { Capabilities = [new("search", ["setValue"]), new("search", ["focus"])] } };
        Assert.Equal(400, (await fixture.Planner.GenerateAsync(repeated, fixture.Owner, CancellationToken.None)).Status);
        Assert.Null(fixture.Worker.LastRequest);
    }

    [Fact]
    public async Task ChangedMetadataBlocksBeforeWorkerAndSensitiveOutputIsNotReturned()
    {
        var fixture = new Fixture();
        var input = fixture.Envelope("hello");
        var badMetadata = input with { Request = input.Request with { RouteId = "private" } };
        Assert.Equal(403, (await fixture.Planner.GenerateAsync(badMetadata, fixture.Owner, CancellationToken.None)).Status);
        Assert.Null(fixture.Worker.LastRequest);
        fixture.Worker.Result = new(LiveUIInferenceProviderKind.LocalWorker,
            new(LiveUISchema.CurrentVersion, [new("setValue", "search", "private")]), "completed");
        Assert.Equal(403, (await fixture.Planner.GenerateAsync(input, fixture.Owner, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task InvalidOrNoncompletedPlansNeverReturnActions()
    {
        var fixture = new Fixture();
        fixture.Worker.Result = new(LiveUIInferenceProviderKind.LocalWorker,
            new(LiveUISchema.CurrentVersion, [new("click", "unapproved")]), "completed");
        var result = await fixture.Planner.GenerateAsync(fixture.Envelope("hello"), fixture.Owner, CancellationToken.None);
        Assert.Equal("invalid_plan", result.Response!.Result.Outcome);
        Assert.Null(result.Response.Result.Plan);
        fixture.Worker.Result = new(LiveUIInferenceProviderKind.LocalWorker,
            new(LiveUISchema.CurrentVersion, [new("setValue", "search", "safe")]), "failed");
        result = await fixture.Planner.GenerateAsync(fixture.Envelope("hello"), fixture.Owner, CancellationToken.None);
        Assert.Equal("invalid_plan", result.Response!.Result.Outcome);
        Assert.Null(result.Response.Result.Plan);
    }

    [Fact]
    public async Task UnexpectedProviderCannotImpersonateLocalWorker()
    {
        var fixture = new Fixture();
        fixture.Worker.Result = new(LiveUIInferenceProviderKind.Gateway, null, "completed");
        var result = await fixture.Planner.GenerateAsync(fixture.Envelope("hello"), fixture.Owner, CancellationToken.None);
        Assert.Equal(503, result.Status);
        Assert.Null(result.Response);
    }

    private static ClaimsPrincipal Identity(Ulid tenant, Ulid principal) => new(new ClaimsIdentity([
        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, principal.ToString())], "test"));

    private sealed class Fixture
    {
        private readonly InMemorySessionStore _sessions = new();
        public FakeFirewall Firewall { get; } = new();
        public FakeWorker Worker { get; } = new();
        public SessionRecord Session { get; }
        public ClaimsPrincipal Owner { get; }
        public DocumentationLiveUIPlanner Planner { get; }

        public Fixture()
        {
            Session = _sessions.Create(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()));
            Owner = Identity(Session.TenantId.Value, Session.PrincipalId.Value);
            Planner = new(_sessions, Firewall, Worker);
        }

        public GatewayLiveUIInferenceRequest Envelope(string intent) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            new(intent, Session.Id.Value.ToString(), "documentation-index", LiveUISchema.CurrentVersion,
                [new("search", ["setValue"])], "manifest"));
    }

    private sealed class FakeWorker : IDocumentationWorkerPlanner
    {
        public LiveUIInferenceRequest? LastRequest { get; private set; }
        public LiveUIInferenceResult Result { get; set; } = new(LiveUIInferenceProviderKind.LocalWorker,
            new(LiveUISchema.CurrentVersion, [new("setValue", "search", "safe")]), "completed");
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken ct) =>
            Task.FromResult(new LiveUIInferenceAvailability(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available));
        public Task<LiveUIInferenceResult> GenerateAsync(LiveUIInferenceRequest request, CancellationToken ct)
        { LastRequest = request; return Task.FromResult(Result); }
    }

    private sealed class FakeFirewall : IPrivacyFirewall
    {
        public bool Enabled { get; set; } = true;
        public List<EgressDestination> Destinations { get; } = [];
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken ct = default)
        {
            Destinations.Add(context.Destination);
            var sanitized = content.Replace("private", "[redacted]", StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(new EgressDecision(true, sanitized, [],
                sanitized == content ? SecurityPolicyAction.Allow : SecurityPolicyAction.Mask, "test"));
        }
    }
}
