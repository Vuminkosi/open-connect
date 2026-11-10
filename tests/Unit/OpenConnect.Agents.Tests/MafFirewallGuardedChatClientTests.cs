using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafFirewallGuardedChatClientTests
{
    private static readonly EgressContext Context = new(
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()),
        EgressDestination.ExternalModel);

    private static IEgressContextProvider Provider => new FixedEgressContextProvider(Context);

    [Fact]
    public async Task Missing_detector_blocks_before_inner_client_is_called()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine()));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello SYNTH-USER-0001")]));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Synthetic_secret_is_never_forwarded_verbatim()
    {
        const string raw = "contact SYNTH-USER-0001 at SYNTH-PHONE-0001";
        var inner = new TestChatClient();
        var gate = new FakeGate(_ => Allow("[REDACTED]"));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);

        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, raw)]);

        Assert.Equal("[REDACTED]", response.Text);
        Assert.Equal("[REDACTED]", inner.LastText);
        Assert.DoesNotContain("SYNTH-USER-0001", inner.LastText, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH-PHONE-0001", inner.LastText, StringComparison.Ordinal);
        Assert.Equal(EgressDestination.ExternalModel, gate.Contexts[0].Destination);
    }

    [Fact]
    public async Task Denial_carries_privacy_blocked_code_without_echo()
    {
        const string raw = "secret SYNTH-TOKEN-0001 payload";
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(_ => Block()), Provider);

        var exception = await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, raw)]));

        Assert.StartsWith(ErrorCodes.PrivacyBlocked, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH-TOKEN-0001", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Allow_forwards_verbatim_and_delegates_services()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(content => Allow(content)), Provider);

        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, inner.Calls);
        Assert.Equal("hello", inner.LastText);
        Assert.Equal("svc", guarded.GetService(typeof(string)));
    }

    [Fact]
    public async Task Streaming_is_blocked_before_first_token()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(_ => Block()), Provider);

        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
        {
            await foreach (var _ in guarded.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "secret SYNTH-TOKEN-0002")]))
            {
            }
        });

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Factory_wraps_once_at_construction_and_never_double_wraps()
    {
        var id = new AgentId("echo");
        var registry = new TestRegistry(new AgentDefinition(id, "Echo"));

        var inner = new TestChatClient();
        var gate = new FakeGate(content => Allow(content));
        var factory = new MafRealAgentFactory(registry, inner, gate, Provider);

        var response = await factory.RunAsync(id, "hello");

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, gate.Evaluations);

        var singleGate = new FakeGate(content => Allow(content));
        var singleGuarded = new FirewallGuardedChatClient(new TestChatClient(), singleGate, Provider);
        var rewrapped = new MafRealAgentFactory(registry, singleGuarded, singleGate, Provider);
        await rewrapped.RunAsync(id, "hello");

        Assert.Equal(1, singleGate.Evaluations);
    }

    private static EgressDecision Allow(string sanitized) =>
        new(true, sanitized, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test-allow");

    private static EgressDecision Block() =>
        new(false, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Block, "test-block");

    private sealed class FakeGate(Func<string, EgressDecision> decide) : IEgressGate
    {
        public int Evaluations { get; private set; }
        public List<EgressContext> Contexts { get; } = [];

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Evaluations++;
            Contexts.Add(context);
            return ValueTask.FromResult(decide(content));
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastText = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, LastText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(string) ? "svc" : null;

        public void Dispose() { }
    }

    private sealed class TestRegistry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => id == definition.Id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
