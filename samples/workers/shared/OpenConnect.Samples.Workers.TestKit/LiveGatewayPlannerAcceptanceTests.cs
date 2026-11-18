using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class LiveGatewayPlannerAcceptanceTests
{
    private const string Route = "route.root";
    private const string Completed = "route.root.components-todolistcomponent-razor-tab-completed";
    private const string Active = "route.root.components-todolistcomponent-razor-tab-active";
    private const string Search = "route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description";
    private const string List = "route.root.components-todolistcomponent-razor-list-task-list";
    private const string Region = "route.root.components-todolistcomponent-razor-region-todo-list";

    [LiveGatewayPlannerTheory]
    [Trait("Category", "LiveInference")]
    [InlineData("Open my Todo list.", "navigate", Route, null, null, null)]
    [InlineData("Show completed tasks.", "activate", Completed, null, null, null)]
    [InlineData("Show active tasks.", "activate", Active, null, null, null)]
    [InlineData("Search for report.", "setValue", Search, "report", null, null)]
    [InlineData("Show completed tasks containing report.", "activate", Completed, null, "setValue", Search)]
    [InlineData("Highlight the results.", "highlight", List, null, null, null)]
    [InlineData("Explain the current view.", "inspect", Region, null, null, null)]
    public async Task Real_gateway_planner_produces_authorized_todo_plan(
        string intent, string firstType, string firstTarget, string? firstValue, string? secondType, string? secondTarget)
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!;
        var apiKey = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!;
        var chat = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        using var guarded = new FirewallGuardedChatClient(chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        var provider = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        var currentRoute = firstType == "navigate" ? "route.workspace-guide" : Route;
        var request = new LiveUIInferenceRequest(intent, "acceptance-session", currentRoute, "openconnect.liveui/v1",
            [new(Route, ["navigate"]) { Label = "OpenConnect - Todo Application" },
             new(Completed, ["activate"]) { Label = "Completed" }, new(Active, ["activate"]) { Label = "Active" },
             new(Search, ["setValue", "focus"]) { Label = "Search tasks by title or description..." },
             new(List, ["highlight", "inspect", "scroll"]) { Label = "Task list" },
             new(Region, ["highlight", "inspect", "scroll"]) { Label = "todo-list" }]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await provider.GeneratePlanAsync(request, timeout.Token);

        Assert.Equal("completed", result.Outcome);
        Assert.NotNull(result.Plan);
        Assert.True(new LiveUIActionPlanValidator().Validate(request, result.Plan!, 8, out var reason),
            $"{reason}: {System.Text.Json.JsonSerializer.Serialize(result.Plan)}");
        Assert.Equal(secondType is null ? 1 : 2, result.Plan!.Actions.Count);
        Assert.Equal(new LiveUIPlannedAction(firstType, firstTarget, firstValue), result.Plan.Actions[0]);
        if (secondType is not null)
            Assert.Equal(new LiveUIPlannedAction(secondType, secondTarget!, "report"), result.Plan.Actions[1]);
    }
    [LiveGatewayPlannerFact]
    [Trait("Category", "LiveInference")]
    public async Task Unavailable_worker_falls_back_to_real_firewall_guarded_gateway_planner()
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!;
        var apiKey = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!;
        var chat = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new CountingEgressGate(new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault)))));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        using var guarded = new FirewallGuardedChatClient(chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        var gateway = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var server = RespondToWorkerReadinessThenUnavailableAsync(listener, timeout.Token);
        using var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{address.Port}/") };
        var worker = new LocalWorkerLiveUIInferenceProvider(workerHttp, Options.Create(new LiveUIWorkerClientOptions
        {
            Endpoint = workerHttp.BaseAddress, BearerToken = "acceptance-only-token"
        }));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        settings.Planning.Timeout = TimeSpan.FromMinutes(2);
        var router = new LiveUIInferenceRouter([worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed tasks.", "acceptance-session", Route,
            "openconnect.liveui/v1", [new(Completed, ["activate"]) { Label = "Completed" },
                new(Active, ["activate"]) { Label = "Active" }]);

        try
        {
            var result = await router.GeneratePlanAsync(request, timeout.Token);
            await server;
            Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
            Assert.Equal("completed", result.Outcome);
            Assert.True(gate.Calls > 0);
            Assert.Equal(EgressDestination.ExternalModel, gate.LastDestination);
            Assert.Equal(new LiveUIPlannedAction("activate", Completed), Assert.Single(result.Plan!.Actions));
        }
        finally
        {
            listener.Stop();
        }
    }

    [LiveWorkerAndGatewayFact]
    [Trait("Category", "LiveInference")]
    public async Task Invalid_real_worker_plan_falls_back_to_real_guarded_gateway()
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!;
        var apiKey = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!;
        var workerText = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_ENDPOINT")!;
        var workerToken = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_TOKEN")!;
        if (!Uri.TryCreate(workerText, UriKind.Absolute, out var workerEndpoint) ||
            workerEndpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(workerEndpoint.Host, out var address) ||
            !IPAddress.IsLoopback(address) || !string.IsNullOrEmpty(workerEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(workerEndpoint.Query) || !string.IsNullOrEmpty(workerEndpoint.Fragment))
            throw new InvalidOperationException("Live acceptance requires a credential-free loopback worker endpoint.");
        var chat = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new CountingEgressGate(new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault)))));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        using var guarded = new FirewallGuardedChatClient(chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        var gateway = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        var recorder = new WorkerResponseRecorder { InnerHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false } };
        using var workerHttp = new HttpClient(recorder) { BaseAddress = new Uri(workerEndpoint.AbsoluteUri.TrimEnd('/') + "/") };
        var worker = new LocalWorkerLiveUIInferenceProvider(workerHttp, Options.Create(new LiveUIWorkerClientOptions
        {
            Endpoint = workerEndpoint, BearerToken = workerToken
        }));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        settings.Planning.Timeout = TimeSpan.FromMinutes(2);
        var router = new LiveUIInferenceRouter([worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("View finished tasks containing report - show completed tasks filtered by report", "acceptance-session", "todo.home",
            "openconnect.liveui/v1", [new("todo.tab.completed", ["activate"]) { Label = "Completed" },
                new("todo.tab.active", ["activate"]) { Label = "Active" },
                new("todo.search", ["setValue"]) { Label = "Search tasks" }]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var noFallback = await router.GeneratePlanAsync(request, timeout.Token);
        Assert.Null(noFallback.Plan);
        Assert.Equal("invalid_plan", noFallback.Outcome);
        Assert.Equal(0, gate.Calls);
        Assert.Contains(HttpStatusCode.UnprocessableEntity, recorder.Statuses);

        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var result = await router.GeneratePlanAsync(request, timeout.Token);

        Assert.True(recorder.Statuses.Count(status => status == HttpStatusCode.UnprocessableEntity) >= 2);
        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal("completed", result.Outcome);
        Assert.True(gate.Calls > 0);
        Assert.Equal(EgressDestination.ExternalModel, gate.LastDestination);
        Assert.NotNull(result.Plan);
        Assert.Equal(2, result.Plan!.Actions.Count);
        Assert.Contains(new LiveUIPlannedAction("activate", "todo.tab.completed"), result.Plan.Actions);
        Assert.Contains(new LiveUIPlannedAction("setValue", "todo.search", "report"), result.Plan.Actions);
    }

    private sealed class WorkerResponseRecorder : DelegatingHandler
    {
        public List<HttpStatusCode> Statuses { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Statuses.Add(response.StatusCode);
            return response;
        }
    }

    private sealed class CountingEgressGate(IEgressGate inner) : IEgressGate
    {
        public int Calls { get; private set; }
        public EgressDestination? LastDestination { get; private set; }

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDestination = context.Destination;
            return inner.EvaluateAsync(content, context, cancellationToken);
        }
    }

    private static async Task RespondToWorkerReadinessThenUnavailableAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        foreach (var status in new[] { "200 OK", "503 Service Unavailable" })
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            Assert.StartsWith(status == "200 OK" ? "GET /health/ready " : "POST /v1/liveui/inference/plan ", requestLine);
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        listener.Stop();
    }
}

public sealed class LiveGatewayPlannerFactAttribute : FactAttribute
{
    public LiveGatewayPlannerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")))
            Skip = "Set the explicit real-provider acceptance configuration to run live gateway planning.";
    }
}

public sealed class LiveWorkerAndGatewayFactAttribute : FactAttribute
{
    public LiveWorkerAndGatewayFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_TOKEN")))
            Skip = "Set explicit real-provider and loopback worker acceptance configuration.";
    }
}

public sealed class LiveGatewayPlannerTheoryAttribute : TheoryAttribute
{
    public LiveGatewayPlannerTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")))
            Skip = "Set the explicit real-provider acceptance configuration to run live gateway planning.";
    }
}
