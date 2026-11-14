using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class LiveUIClientInteractionRoundTripTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public async Task Maf_tool_waits_for_host_authorized_browser_action_and_verified_receipt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenConnectTaskExecution();
        using var serviceProvider = services.BuildServiceProvider();
        var sessions = serviceProvider.GetRequiredService<InMemorySessionStore>();
        var session = sessions.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        var manifest = CreateManifest();
        var manifestProvider = new TestManifestProvider(manifest);
        var authorizer = new AllowAuthorizer();
        var runtime = new RecordingRuntime();
        var options = Options.Create(new LiveUIOptions());
        var validator = new LiveUIManifestValidator();
        var orchestrator = new LiveUIOrchestrator(manifestProvider, authorizer, runtime, options, validator);
        var hostTools = new LiveUIAgentTools(new TestContextAccessor(new LiveUIAgentContext("blazor-circuit", "todo.home")),
            manifestProvider, authorizer, validator, orchestrator, options);
        var clientTools = LiveUIClientAgentToolFactory.CreateTools(
            serviceProvider.GetRequiredService<IClientInteractionBroker>(), session.Id, TimeSpan.FromSeconds(2));
        var execute = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            clientTools.Single(tool => tool.Name == "liveui_execute"));

        var toolCall = execute.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        {
            ["targetId"] = "todo.completed",
            ["action"] = "SelectTab"
        })).AsTask();
        var request = await WaitForRequestAsync(sessions, session.Id.Value, "execute");
        Assert.Equal("execute", request.Operation);
        Assert.Equal("todo.completed", request.TargetId);
        Assert.Equal("SelectTab", request.Action);

        var browserReceipt = await hostTools.ExecuteAsync(request.TargetId!, request.Action!, request.Value);
        Assert.Equal(LiveUIActionOutcome.Completed, browserReceipt.Outcome);
        Assert.True(browserReceipt.PostconditionVerified);
        Assert.Equal(LiveUIActionType.SelectTab, runtime.LastContext!.Action.Type);

        var response = new ClientInteractionResponse(request.CorrelationId, "action",
            JsonSerializer.Serialize(browserReceipt, OpenConnectHttpJson.CreateOptions()));
        var httpContext = AuthenticatedContext(serviceProvider);
        var endpointResult = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, httpContext, sessions);
        var httpResponse = await ExecuteAsync(endpointResult, serviceProvider);
        Assert.Equal(StatusCodes.Status200OK, httpResponse.StatusCode);

        var modelResult = Assert.IsType<JsonElement>(await toolCall.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Completed", modelResult.GetProperty("outcome").GetString());
        Assert.True(modelResult.GetProperty("postconditionVerified").GetBoolean());

        var highlight = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            clientTools.Single(tool => tool.Name == "liveui_highlight"));
        var highlightToolCall = highlight.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        {
            ["targetId"] = "todo.completed",
            ["durationMilliseconds"] = 1_500
        })).AsTask();
        var highlightRequest = await WaitForRequestAsync(sessions, session.Id.Value, "highlight");
        Assert.Equal(1_500, highlightRequest.HighlightDurationMilliseconds);

        var highlightReceipt = await hostTools.HighlightAsync(
            highlightRequest.TargetId!, durationMilliseconds: highlightRequest.HighlightDurationMilliseconds);
        Assert.Equal(LiveUIActionOutcome.Completed, highlightReceipt.Outcome);
        Assert.Equal(LiveUIActionType.Highlight, runtime.LastContext!.Action.Type);
        Assert.Equal(1_500, runtime.LastContext.Action.Parameters.HighlightDurationMilliseconds);

        var highlightResponse = new ClientInteractionResponse(highlightRequest.CorrelationId, "action",
            JsonSerializer.Serialize(highlightReceipt, OpenConnectHttpJson.CreateOptions()));
        var highlightEndpointResult = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), highlightResponse, AuthenticatedContext(serviceProvider), sessions);
        var highlightHttpResponse = await ExecuteAsync(highlightEndpointResult, serviceProvider);
        Assert.Equal(StatusCodes.Status200OK, highlightHttpResponse.StatusCode);

        var highlightModelResult = Assert.IsType<JsonElement>(await highlightToolCall.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Completed", highlightModelResult.GetProperty("outcome").GetString());
        Assert.True(highlightModelResult.GetProperty("postconditionVerified").GetBoolean());
    }

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo",
            [
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.Highlight })
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private static async Task<ClientInteractionRequest> WaitForRequestAsync(
        InMemorySessionStore sessions, Ulid sessionId, string operation)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var request = sessions.GetEvents(sessionId, 0)
                .Where(item => item.Type == ClientInteractionEventTypes.Request)
                .Select(item => JsonSerializer.Deserialize<ClientInteractionRequest>(item.Data, OpenConnectHttpJson.CreateOptions()))
                .FirstOrDefault(item => item?.Operation == operation);
            if (request is not null) return request;
            await Task.Delay(10);
        }
        throw new TimeoutException("The LiveUI MAF tool did not append a client request.");
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services) => new()
    {
        RequestServices = services,
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", Tenant),
            new Claim(ClaimTypes.NameIdentifier, Principal)
        ], "test"))
    };

    private static async Task<(int StatusCode, byte[] Body)> ExecuteAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, ((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed class TestManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class TestContextAccessor(LiveUIAgentContext context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(context);
    }

    private sealed class AllowAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "test_allowed"));
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public LiveUIExecutionContext? LastContext { get; private set; }

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "selected", true, "selected"));
        }
    }
}
