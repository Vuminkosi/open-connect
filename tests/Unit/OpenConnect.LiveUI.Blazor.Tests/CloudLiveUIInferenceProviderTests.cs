using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class CloudLiveUIInferenceProviderTests
{
    [Fact]
    public async Task AuthorizedScreenedRequestProducesAValidatedProposal()
    {
        var fixture = new Fixture();
        fixture.Transport.Handler = body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("openconnect.cloud.liveui/v1", json.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(fixture.ExecutionId.ToString("D"), json.RootElement.GetProperty("requestId").GetString());
            var input = json.RootElement.GetProperty("input");
            Assert.Equal("safe intent", input.GetProperty("intent").GetString());
            Assert.Equal("activate", input.GetProperty("context").GetProperty("availableActions")[0].GetProperty("action").GetString());
            return fixture.Response();
        };

        var result = await fixture.Provider.GeneratePlanAsync(fixture.Request);

        Assert.Equal("completed", result.Outcome);
        Assert.Equal("item", Assert.Single(result.Plan!.Actions).TargetId);
        Assert.Contains((EgressDestination.ExternalModel, "safe intent"), fixture.Checked);
        Assert.Contains((EgressDestination.Output, "item"), fixture.Checked);
        Assert.All(fixture.Transport.LastRequest!, value => Assert.Equal(0, value));
        Assert.All(fixture.Transport.LastResponse!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task DeniedHostOrPrivacyNeverDispatches()
    {
        var denied = new Fixture(); denied.Authorizer.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.Provider.GeneratePlanAsync(denied.Request));
        Assert.Equal(0, denied.Transport.Calls);
        var blocked = new Fixture(); blocked.BlockInput = true;
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => blocked.Provider.GeneratePlanAsync(blocked.Request));
        Assert.Equal(0, blocked.Transport.Calls);
    }

    [Fact]
    public async Task UnlistedOrMismatchedActionsNeverReturnAPlan()
    {
        foreach (var response in new[] { "unlisted", "wrong-id", "duplicate-property", "wrong-schema" })
        {
            var fixture = new Fixture(); fixture.Transport.Handler = _ => fixture.Response(response);
            var result = await fixture.Provider.GeneratePlanAsync(fixture.Request);
            Assert.Null(result.Plan); Assert.Equal("invalid_plan", result.Outcome);
        }
    }

    [Fact]
    public async Task OutputPrivacyDenialAndGatewayDenialStopFallback()
    {
        var fixture = new Fixture { BlockOutput = true };
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => fixture.Provider.GeneratePlanAsync(fixture.Request));
        var rejected = new Fixture(); rejected.Transport.Handler = _ => new(403, []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rejected.Provider.GeneratePlanAsync(rejected.Request));
        var quota = new Fixture(); quota.Transport.Handler = _ => new(429, []);
        Assert.Equal("rate_limited", (await quota.Provider.GeneratePlanAsync(quota.Request)).Outcome);
        var replay = new Fixture(); replay.Transport.Handler = _ => new(409, []);
        Assert.Equal("conflict", (await replay.Provider.GeneratePlanAsync(replay.Request)).Outcome);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GeneratePlanAsync(fixture.Request, cancellation.Token));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Fact]
    public async Task ReplayConflictDoesNotFallBackToAnotherProvider()
    {
        var fixture = new Fixture(); fixture.Transport.Handler = _ => new(409, []);
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.GatewayFirst };
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        settings.Worker.Enabled = true;
        var fallback = new Fallback();
        var router = new LiveUIInferenceRouter([fixture.Provider, fallback], new LiveUIActionPlanValidator(), Options.Create(settings));
        var result = await router.GeneratePlanAsync(fixture.Request);
        Assert.Equal("conflict", result.Outcome);
        Assert.Equal(0, fallback.Calls);
    }

    private sealed class Fallback : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.LocalWorker;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available)); }
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveUIInferenceResult(Kind, null, "unused"));
    }

    [Fact]
    public void RegistrationRequiresTheApplicationHostAuthorizer()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference();
        services.AddSingleton<IEgressGate>(new GateForRegistration());
        services.AddCloudLiveUIInferenceProvider(options => { options.Origin = new Uri("https://cloud.example/"); options.Credential = "credential"; });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<CloudLiveUIInferenceProvider>());
    }

    [Fact]
    public async Task StartupRejectsMissingHostAuthorizationBeforeServingRequests()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference();
        services.AddSingleton<IEgressGate>(new GateForRegistration());
        services.AddCloudLiveUIInferenceProvider(options => { options.Origin = new Uri("https://cloud.example/"); options.Credential = "credential"; });
        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>().Single(service => service.GetType().Name == "CloudLiveUIHostStartupValidator");
        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
    }

    private sealed class GateForRegistration : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "allowed"));
    }

    private sealed class Fixture
    {
        private readonly EgressContext _context = new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public List<(EgressDestination, string)> Checked { get; } = [];
        public bool BlockInput { get; set; }
        public bool BlockOutput { get; set; }
        public Authorizer Authorizer { get; }
        public Transport Transport { get; }
        public LiveUIInferenceRequest Request { get; }
        public CloudLiveUIInferenceProvider Provider { get; }

        public Fixture()
        {
            Authorizer = new(_context); Transport = new(); Transport.Handler = _ => Response();
            Request = new("private intent", _context.Session.Value.ToString(), "/items", "v1",
                [new("item", ["activate"]) { Label = "Open" }]) { ExecutionId = ExecutionId.ToString("N") };
            Provider = new(Authorizer, new Gate(this), Transport, Options.Create(new LiveUIInferenceOptions()));
        }

        public CloudLiveUITransportResult Response(string? scenario = null)
        {
            var requestId = scenario == "wrong-id" ? Guid.NewGuid() : ExecutionId;
            var type = scenario == "unlisted" ? "navigate" : "activate";
            var schema = scenario == "wrong-schema" ? "other" : "v1";
            var duplicate = scenario == "duplicate-property" ? ",\"target\":\"item\"" : "";
            var json = $"{{\"schemaVersion\":\"openconnect.cloud.liveui.result/v1\",\"requestId\":\"{requestId:D}\",\"traceId\":\"1234567890abcdef1234567890abcdef\",\"provider\":\"deterministic\",\"plan\":{{\"schemaVersion\":\"{schema}\",\"actions\":[{{\"type\":\"{type}\",\"target\":\"item\"{duplicate}}}]}}}}";
            return new(200, Encoding.UTF8.GetBytes(json));
        }

        private sealed class Gate(Fixture fixture) : IEgressGate
        {
            public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
            {
                fixture.Checked.Add((context.Destination, content));
                var allowed = context.Destination == EgressDestination.Output ? !fixture.BlockOutput : !fixture.BlockInput;
                return ValueTask.FromResult(new EgressDecision(allowed, allowed ? content == "private intent" ? "safe intent" : content : null,
                    [], allowed ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test"));
            }
        }
    }

    private sealed class Authorizer(EgressContext context) : ICloudLiveUIHostAuthorizer
    {
        public bool Allowed { get; set; } = true;
        public ValueTask<EgressContext?> AuthorizeAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Allowed ? context : null);
    }

    private sealed class Transport : ICloudLiveUITransport
    {
        public int Calls { get; private set; }
        public byte[]? LastRequest { get; private set; }
        public byte[]? LastResponse { get; private set; }
        public Func<ReadOnlyMemory<byte>, CloudLiveUITransportResult> Handler { get; set; } = _ => new(503, []);
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<CloudLiveUITransportResult> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(request, out var segment));
            LastRequest = segment.Array;
            var response = Handler(request); LastResponse = response.Body;
            return Task.FromResult(response);
        }
    }
}
