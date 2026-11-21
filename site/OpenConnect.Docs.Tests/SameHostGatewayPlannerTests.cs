using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class SameHostGatewayPlannerTests
{
    [Fact]
    public async Task Planner_uses_the_owned_session_as_the_privacy_scope()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        var gate = new CapturingGate();
        var chat = new RecordingChatClient();
        var planner = new SameHostGatewayPlanner(sessions, new TodoGatewayOptions
        {
            Mode = TodoGatewayMode.SameHostGateway,
            TenantId = tenant.Value.ToString(),
            PrincipalId = principal.Value.ToString()
        }, new FixedContext(session.Id.Value.ToString()), chat, gate);

        var result = await planner.GeneratePlanAsync(Request(session.Id.Value.ToString()));

        Assert.Equal("completed", result.Outcome);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(new[] { EgressDestination.ExternalModel, EgressDestination.Output },
            gate.Contexts.Select(context => context.Destination).Distinct());
        Assert.All(gate.Contexts, context =>
        {
            Assert.Equal(tenant, context.Tenant);
            Assert.Equal(principal, context.Principal);
            Assert.Equal(session.Id, context.Session);
        });
    }

    [Fact]
    public async Task Planner_rejects_other_principals_before_model_egress()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var foreign = sessions.Create(tenant, new PrincipalId(Ulid.NewUlid()));
        var gate = new CapturingGate();
        var chat = new RecordingChatClient();
        var planner = new SameHostGatewayPlanner(sessions, new TodoGatewayOptions
        {
            Mode = TodoGatewayMode.SameHostGateway,
            TenantId = tenant.Value.ToString(),
            PrincipalId = principal.Value.ToString()
        }, new FixedContext(foreign.Id.Value.ToString()), chat, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planner.GeneratePlanAsync(Request(foreign.Id.Value.ToString())));
        Assert.Empty(gate.Contexts);
        Assert.Equal(0, chat.Calls);
    }

    private static LiveUIInferenceRequest Request(string sessionId) => new("Show completed tasks", sessionId,
        "todo", "openconnect.liveui/v1", [new("todo.completed", ["activate"])]);

    [Fact]
    public async Task Planner_rejects_a_different_session_of_the_same_principal()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var current = sessions.Create(tenant, principal);
        var other = sessions.Create(tenant, principal);
        var chat = new RecordingChatClient();
        var gate = new CapturingGate();
        var planner = new SameHostGatewayPlanner(sessions, new TodoGatewayOptions
        {
            Mode = TodoGatewayMode.SameHostGateway,
            TenantId = tenant.Value.ToString(),
            PrincipalId = principal.Value.ToString()
        }, new FixedContext(current.Id.Value.ToString()), chat, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planner.GeneratePlanAsync(Request(other.Id.Value.ToString())));
        Assert.Empty(gate.Contexts);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Planner_rejects_a_different_current_route_before_model_egress()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        var chat = new RecordingChatClient();
        var gate = new CapturingGate();
        var planner = new SameHostGatewayPlanner(sessions, new TodoGatewayOptions
        {
            Mode = TodoGatewayMode.SameHostGateway,
            TenantId = tenant.Value.ToString(),
            PrincipalId = principal.Value.ToString()
        }, new FixedContext(session.Id.Value.ToString()), chat, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            planner.GeneratePlanAsync(Request(session.Id.Value.ToString()) with { RouteId = "other" }));
        Assert.Empty(gate.Contexts);
        Assert.Equal(0, chat.Calls);
    }

    private sealed class FixedContext(string sessionId) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(new LiveUIAgentContext(sessionId, "todo"));
    }

    private sealed class CapturingGate : IEgressGate
    {
        private readonly List<EgressContext> _contexts = [];
        public IReadOnlyList<EgressContext> Contexts => _contexts;

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            _contexts.Add(context);
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"schemaVersion\":\"openconnect.liveui/v1\",\"actions\":[{\"type\":\"activate\",\"targetId\":\"todo.completed\"}]}")));
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
