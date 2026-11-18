using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Samples.Workers.Host;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class GatewayPlannerEndpointTests
{
    [Fact]
    public async Task PlannerIsDisabledByDefault()
    {
        await using var host = await TestHost.StartAsync(false);
        var session = host.Sessions.Create(new TenantId(host.Tenant), new PrincipalId(host.Principal));
        using var availability = new HttpRequestMessage(HttpMethod.Get, "/openconnect/v1/liveui/inference/planner/availability");
        availability.Headers.Add("X-Test-Tenant", host.Tenant.ToString());
        availability.Headers.Add("X-Test-Principal", host.Principal.ToString());
        using var availabilityResponse = await host.Client.SendAsync(availability);
        var reported = await availabilityResponse.Content.ReadFromJsonAsync<LiveUIInferenceAvailability>();
        Assert.Equal(LiveUIInferenceAvailabilityStatus.Unavailable, reported!.Status);
        using var request = host.Request(session.Id.Value, host.Tenant, host.Principal);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Gate.Calls);
        Assert.Equal(0, host.Chat.Calls);
    }

    [Fact]
    public async Task PlannerEnforcesIdentitySessionAndPrivacyBeforeModelCall()
    {
        await using var host = await TestHost.StartAsync(true);
        var session = host.Sessions.Create(new TenantId(host.Tenant), new PrincipalId(host.Principal));
        using var unauthenticated = host.Request(session.Id.Value, null, null);
        using var unauthenticatedResponse = await host.Client.SendAsync(unauthenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
        foreach (var (tenant, principal) in new[] { (Ulid.NewUlid(), host.Principal), (host.Tenant, Ulid.NewUlid()) })
        {
            using var foreign = host.Request(session.Id.Value, tenant, principal);
            using var foreignResponse = await host.Client.SendAsync(foreign);
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        }
        Assert.Equal(0, host.Gate.Calls);
        Assert.Equal(0, host.Chat.Calls);

        host.Gate.Deny = true;
        using var denied = host.Request(session.Id.Value, host.Tenant, host.Principal);
        using var deniedResponse = await host.Client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.Equal(0, host.Chat.Calls);
        Assert.Equal(EgressDestination.ExternalModel, host.Gate.LastContext!.Destination);
        Assert.Equal(host.Tenant, host.Gate.LastContext.Tenant.Value);
        Assert.Equal(host.Principal, host.Gate.LastContext.Principal.Value);
        Assert.Equal(session.Id.Value, host.Gate.LastContext.Session.Value);

        host.Gate.Deny = false;
        using var allowed = host.Request(session.Id.Value, host.Tenant, host.Principal);
        using var allowedResponse = await host.Client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        var result = await allowedResponse.Content.ReadFromJsonAsync<GatewayLiveUIInferenceResponse>();
        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result!.Result.Provider);
        Assert.Equal("completed", result.Result.Outcome);
        Assert.Equal("todo.completed", Assert.Single(result.Result.Plan!.Actions).TargetId);
        Assert.Equal(1, host.Chat.Calls);

        host.Chat.Target = "unavailable.target";
        using var invalid = host.Request(session.Id.Value, host.Tenant, host.Principal);
        using var invalidResponse = await host.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.OK, invalidResponse.StatusCode);
        var invalidResult = await invalidResponse.Content.ReadFromJsonAsync<GatewayLiveUIInferenceResponse>();
        Assert.Null(invalidResult!.Result.Plan);
        Assert.Equal("invalid_plan", invalidResult.Result.Outcome);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, InMemorySessionStore sessions,
        TestGate gate, TestChat chat, Ulid tenant, Ulid principal) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public InMemorySessionStore Sessions => sessions;
        public TestGate Gate => gate;
        public TestChat Chat => chat;
        public Ulid Tenant => tenant;
        public Ulid Principal => principal;

        public static async Task<TestHost> StartAsync(bool enabled)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["OpenConnect:LiveUI:ExternalPlanner:Enabled"] = enabled ? "true" : null });
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("liveui-inference", limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            }));
            var sessions = new InMemorySessionStore();
            var gate = new TestGate();
            var chat = new TestChat();
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton<IEgressGate>(gate);
            builder.Services.AddSingleton<IPrivacyFirewall>(new TestFirewall());
            builder.Services.AddSingleton<IChatClient>(chat);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapOpenConnectLiveUIInference();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(timeout.Token);
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            return new TestHost(app, client, sessions, gate, chat, Ulid.NewUlid(), Ulid.NewUlid());
        }

        public HttpRequestMessage Request(Ulid session, Ulid? tenant, Ulid? principal)
        {
            var requestId = Guid.NewGuid().ToString("D");
            var executionId = Guid.NewGuid().ToString("N");
            var payload = new LiveUIInferenceRequest("Show completed tasks", session.ToString(), "route.root",
                "openconnect.liveui/v1", [new("todo.completed", ["activate"])])
            { RequestId = requestId, ExecutionId = executionId, ManifestHash = "sha256:test" };
            var request = new HttpRequestMessage(HttpMethod.Post, "/openconnect/v1/liveui/inference/planner/plan")
            { Content = JsonContent.Create(new GatewayLiveUIInferenceRequest(requestId, executionId, payload)) };
            if (tenant.HasValue) request.Headers.Add("X-Test-Tenant", tenant.Value.ToString());
            if (principal.HasValue) request.Headers.Add("X-Test-Principal", principal.Value.ToString());
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var principal = Request.Headers["X-Test-Principal"].ToString();
            if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(principal))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("tenant_id", tenant), new Claim(ClaimTypes.NameIdentifier, principal)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TestGate : IEgressGate
    {
        public bool Deny { get; set; }
        public int Calls { get; private set; }
        public EgressContext? LastContext { get; private set; }
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return ValueTask.FromResult(new EgressDecision(!Deny, Deny ? null : content, [],
                Deny ? SecurityPolicyAction.Block : SecurityPolicyAction.Allow, "test"));
        }
    }

    private sealed class TestFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class TestChat : IChatClient
    {
        public int Calls { get; private set; }
        public string Target { get; set; } = "todo.completed";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var response = System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = "openconnect.liveui/v1",
                actions = new[] { new { type = "activate", targetId = Target } }
            });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
