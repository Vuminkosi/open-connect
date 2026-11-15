using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using OpenConnect.Samples.Workers.Host;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class MetaTodoPrivacyEgressTests
{
    [Fact]
    public async Task Todo_agent_uses_task_scope_and_tokenizes_canaries_before_chat_provider_egress()
    {
        const string email = "todo.canary@example.test";
        const string phone = "+1-555-867-5309";
        const string card = "4532015112830366";
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var detector = new RulesSecurityDetector(vault);
        var gate = new EgressGate(new PrivacyFirewall(
            Options.Create(new PrivacyOptions()), new PolicyEngine(detector)));
        var chatClient = new CapturingChatClient();
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") },
            Options.Create(new MetaCloudOptions()));
        var executor = new MetaTodoTaskExecutor(
            new TestAgentRegistry(), chatClient, gate, todo, sessions, new NoopInteractionBroker(),
            Options.Create(new MetaCloudOptions()), NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(
            new TaskId(Ulid.NewUlid()), tenant, principal, session,
            $"Handle this contact: {email}, {phone}, payment card {card}.", "privacy-canary"),
            CancellationToken.None);

        Assert.NotEmpty(chatClient.OutboundMessages);
        var outbound = string.Join("\n", chatClient.OutboundMessages.Select(message => message.Text));
        Assert.DoesNotContain(email, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain(phone, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain(card, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B1-", outbound, StringComparison.OrdinalIgnoreCase);

        var tokens = System.Text.RegularExpressions.Regex.Matches(outbound, @"\[OC_[A-F0-9]{32}\]")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, tokens.Length);
        var scope = new PrivacyScope(tenant, principal, session, "redaction");
        var restored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var value = await vault.ResolveAsync(token, scope);
            Assert.NotNull(value);
            restored.Add(value);
        }
        Assert.True(new[] { email, phone, card }.ToHashSet(StringComparer.Ordinal).SetEquals(restored));
    }

    private sealed class TestAgentRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("worker-sample-direct"), "OpenConnect assistant");
        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class NoopInteractionBroker : IClientInteractionBroker
    {
        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClientInteractionResponse?>(null);
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage> OutboundMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboundMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Handled safely.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
