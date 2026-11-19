using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
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
    public async Task Sanitizing_assistant_text_preserves_tool_call_and_matching_result()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(content =>
            Allow(content is "request" or "private text" ? "[REDACTED]" : content)), Provider);
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new TextContent("private text"), new FunctionCallContent("call-1", "liveui_inspect", new Dictionary<string, object?>())])
        { MessageId = "message-1" };
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "visible")]);

        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), assistant, tool]);

        var forwarded = inner.LastMessages[1];
        Assert.Equal("[REDACTED]", forwarded.Text);
        Assert.Equal("message-1", forwarded.MessageId);
        Assert.Equal("call-1", Assert.Single(forwarded.Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("call-1", Assert.Single(inner.LastMessages[2].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal("private text", assistant.Text);
    }

    [Fact]
    public async Task Structured_tool_arguments_and_results_are_guarded_before_egress()
    {
        const string secret = "SYNTH-PRIVATE-0001";
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(content => Allow(content.Replace(secret, "[REDACTED]", StringComparison.Ordinal))), Provider);
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = secret })]);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new { description = secret })]);

        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), assistant, tool]);

        var call = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionCallContent>());
        var result = Assert.Single(inner.LastMessages[2].Contents.OfType<FunctionResultContent>());
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(call.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result.Result), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", System.Text.Json.JsonSerializer.Serialize(call.Arguments), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", System.Text.Json.JsonSerializer.Serialize(result.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_tool_content_preserves_argument_and_result_types()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        const string payload = "visible";
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["limit"] = 3 })]);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", payload)]);

        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), assistant, tool]);

        var call = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionCallContent>());
        var result = Assert.Single(inner.LastMessages[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal(3, Assert.IsType<int>(call.Arguments!["limit"]));
        Assert.Equal(payload, Assert.IsType<string>(result.Result));
    }

    [Fact]
    public async Task Redacted_string_tool_result_remains_a_string()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(content =>
            Allow(content.Replace("SYNTH-PRIVATE-0001", "[REDACTED]", StringComparison.Ordinal))), Provider);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "SYNTH-PRIVATE-0001")]);

        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), tool]);

        var result = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionResultContent>());
        Assert.Equal("[REDACTED]", Assert.IsType<string>(result.Result));
    }


    [Fact]
    public async Task Rules_firewall_redacts_sensitive_tool_data_before_model_egress()
    {
        const string email = "tool.canary@example.test";
        const string phone = "+1-555-867-5309";
        var inner = new TestChatClient();
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = email })]);
        using var toolJson = System.Text.Json.JsonDocument.Parse("{\"phone\":\"\\u002b1-555-867-5309\"}");
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", toolJson.RootElement)]);

        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), assistant, tool]);

        var call = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionCallContent>());
        var result = Assert.Single(inner.LastMessages[2].Contents.OfType<FunctionResultContent>());
        Assert.DoesNotContain(email, System.Text.Json.JsonSerializer.Serialize(call.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(phone, System.Text.Json.JsonSerializer.Serialize(result.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("555-867-5309", System.Text.Json.JsonSerializer.Serialize(result.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_sanitized_tool_json_fails_closed_before_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(content =>
            Allow(content.StartsWith('{') ? "not-json" : content)), Provider);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new { name = "value" })]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), tool]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Denied_structured_tool_result_never_reaches_provider()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(_ => Block()), Provider);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new { value = "private" })]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([tool]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Media_only_message_is_denied_before_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        var ex = await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([message]));

        Assert.Contains(ErrorCodes.PrivacyBlocked, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Text_plus_data_content_is_denied_before_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, [new TextContent("describe SYNTH-USER-0001"), new DataContent(new byte[] { 9 }, "audio/wav")]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([message]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Uri_content_is_denied_before_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, [new UriContent(new Uri("https://example.invalid/synthetic.pdf"), "application/pdf")]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([message]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Unknown_content_subtype_is_denied_before_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, [new UnknownContent()]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([message]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Streaming_media_is_denied_before_first_provider_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/jpeg")]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
        {
            await foreach (var _ in guarded.GetStreamingResponseAsync([message]))
            {
            }
        });
        Assert.Equal(0, inner.Calls);
    }

    private sealed class UnknownContent : AIContent;

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
        public List<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastMessages = messages.ToList();
            LastText = LastMessages.Last(message => message.Role == ChatRole.User).Text;
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
