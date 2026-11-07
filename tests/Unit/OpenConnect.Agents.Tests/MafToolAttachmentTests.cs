using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafToolAttachmentTests
{
    [Fact]
    public async Task ToolFunction_reaches_maf_loop_and_returns_result()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new ToolCallClient();
        var id = new AgentId("echo");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(id, "Echo")), client);
        var executions = 0;
        var tool = AIFunctionFactory.Create((string value) =>
        {
            executions++;
            return value + "-suffix";
        }, "append_suffix", "Append a suffix.");

        var response = await factory.RunAsync(id, "hello", [tool], guard.Token);

        Assert.Equal(1, executions);
        Assert.Contains("hello-suffix", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task UnknownAgentWithTools_still_throws_before_maf_call()
    {
        var client = new ToolCallClient();
        var id = new AgentId("unknown");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(new AgentId("known"), "Known")), client);
        var tool = AIFunctionFactory.Create((string value) => value, "echo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.RunAsync(id, "hello", [tool]));

        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task NoTools_path_unchanged()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new ToolCallClient();
        var id = new AgentId("echo");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(id, "Echo")), client);

        var response = await factory.RunAsync(id, "hello", guard.Token);

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, client.Calls);
    }

    private sealed class ToolCallClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1 && options?.Tools?.Count > 0)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(options.Tools[0]);
                var call = new FunctionCallContent("call-1", function.Name, new Dictionary<string, object?> { ["value"] = "hello" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            var text = result?.Result?.ToString() ?? messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
