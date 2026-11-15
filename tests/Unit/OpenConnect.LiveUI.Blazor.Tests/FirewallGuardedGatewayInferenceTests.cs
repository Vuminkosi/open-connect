using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class FirewallGuardedGatewayInferenceTests
{
    [Fact]
    public void Constructor_rejects_an_unguarded_chat_client()
    {
        Assert.Throws<InvalidOperationException>(() => new FirewallGuardedLiveUIGatewayInferenceProvider(new FakeChatClient("{}")));
    }

    [Fact]
    public async Task Gateway_plan_uses_firewall_guard_and_parses_structured_plan()
    {
        var inner = new FakeChatClient("{\"schemaVersion\":\"openconnect.liveui/v1\",\"actions\":[{\"type\":\"activate\",\"targetId\":\"todo.tab.completed\"}]}");
        var guarded = CreateGuarded(inner, allow: true);
        var provider = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        var result = await provider.GeneratePlanAsync(request);

        Assert.Equal(1, inner.Calls);
        Assert.Equal("todo.tab.completed", Assert.Single(Assert.IsType<LiveUIActionPlan>(result.Plan).Actions).TargetId);
    }

    [Fact]
    public async Task Privacy_denial_prevents_chat_client_call_and_fails_closed()
    {
        var inner = new FakeChatClient("{}");
        var provider = new FirewallGuardedLiveUIGatewayInferenceProvider(CreateGuarded(inner, allow: false));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => provider.GeneratePlanAsync(request));

        Assert.Equal(0, inner.Calls);
    }

    private static FirewallGuardedChatClient CreateGuarded(FakeChatClient inner, bool allow) =>
        new(inner, new TestEgressGate(allow), new FixedEgressContextProvider(new EgressContext(
            new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel)));

    private sealed class TestEgressGate(bool allow) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(allow, allow ? content : null, [],
                allow ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test_policy"));
    }

    private sealed class FakeChatClient(string responseText) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.Contains(messages, message => message.Role == ChatRole.User && message.Text.Contains("Show completed tasks", StringComparison.Ordinal));
            Assert.Equal(ChatResponseFormat.Json, options?.ResponseFormat);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
