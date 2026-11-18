using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>Host DI consumer wiring proof for <see cref="FirewallGuardedChatClientExtensions"/>.</summary>
/// <remarks>Uses a real <see cref="ServiceCollection"/> with deterministic fakes only; no network.
/// Proves decorated resolution, lifetime match, no double-wrap, and end-to-end denial.</remarks>
public sealed class FirewallGuardedDiWiringTests
{
    private static EgressContext Context => new(
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()),
        EgressDestination.ExternalModel);

    [Fact]
    public async Task Resolved_client_is_decorated_and_evaluates_gate_once_per_turn()
    {
        var inner = new CountingChatClient();
        var gate = new CountingGate(static content => Allow(content));
        using var provider = NewProvider(inner, gate);

        var resolved = provider.GetRequiredService<IChatClient>();

        Assert.IsType<FirewallGuardedChatClient>(resolved);

        var response = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, gate.Evaluations);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Singleton_lifetime_matches_and_second_registration_does_not_double_wrap()
    {
        var inner = new CountingChatClient();
        var gate = new CountingGate(static content => Allow(content));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(inner);
        services.AddSingleton<IEgressGate>(gate);
        services.AddFirewallGuardedChatClient(Context);
        services.AddFirewallGuardedChatClient(Context);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IChatClient>();
        var second = provider.GetRequiredService<IChatClient>();

        Assert.Same(first, second);
        Assert.IsType<FirewallGuardedChatClient>(first);

        await first.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(1, gate.Evaluations);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Resolved_client_denies_end_to_end_without_calling_inner()
    {
        var inner = new CountingChatClient();
        var gate = new CountingGate(static _ => Block());
        using var provider = NewProvider(inner, gate);

        var resolved = provider.GetRequiredService<IChatClient>();
        Assert.IsType<FirewallGuardedChatClient>(resolved);

        var exception = await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "secret SYNTH-TOKEN-0001")]));

        Assert.StartsWith(ErrorCodes.PrivacyBlocked, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH-TOKEN-0001", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Without_extension_resolved_client_is_raw_inner()
    {
        var inner = new CountingChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(inner);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IChatClient>();

        Assert.Same(inner, resolved);
        var response = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "verbatim")]);
        Assert.Equal("verbatim", response.Text);
        Assert.Equal(1, inner.Calls);
    }

    private static ServiceProvider NewProvider(CountingChatClient inner, CountingGate gate)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(inner);
        services.AddSingleton<IEgressGate>(gate);
        services.AddFirewallGuardedChatClient(Context);
        return services.BuildServiceProvider();
    }

    private static EgressDecision Allow(string sanitized) =>
        new(true, sanitized, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test-allow");

    private static EgressDecision Block() =>
        new(false, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Block, "test-block");

    private sealed class CountingGate(Func<string, EgressDecision> decide) : IEgressGate
    {
        public int Evaluations { get; private set; }

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Evaluations++;
            return ValueTask.FromResult(decide(content));
        }
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var text = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
