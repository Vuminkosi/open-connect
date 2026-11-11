// <copyright file="MafNativeCapabilityLabTests.cs">
// Standalone MAF native capability lab tests (OC2-D-MAF-001 and OC2-D-MAF-002).
// MAF-001: standalone compile/run sample with real MAF AIAgent + deterministic IChatClient
// capturing streaming text and structured updates, plus misuse/cancellation negative cases.
// MAF-002: native function-tool invocation, tool result handling, and middleware invocation order.
// Deterministic stubs only, no external network, no real LLM dependencies.
// </copyright>

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>Executable MAF laboratory tests for native agent streaming, tool execution, and middleware order.</summary>
public sealed class MafNativeCapabilityLabTests
{
    private readonly ITestOutputHelper _output;

    public MafNativeCapabilityLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Maf001_Real_Maf_Agent_Streams_Text_And_Structured_Updates()
    {
        var client = new DeterministicStreamingChatClient(
            chunks: ["Hello", " ", "from", " ", "streaming", " ", "MAF!"],
            structuredKey: "test-meta",
            structuredVal: "step-1");

        var agent = client.AsAIAgent(name: "StreamingAgent", instructions: "Stream responses");

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("give me a stream"))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var combinedText = string.Join(string.Empty, updates.Select(u => u.Text));
        Assert.Equal("Hello from streaming MAF!", combinedText);

        // Verify structured update / role update is captured
        var hasAssistantRole = updates.Any(u => u.Role == ChatRole.Assistant);
        Assert.True(hasAssistantRole);
        Assert.Equal(1, client.StreamingCalls);
    }

    [Fact]
    public async Task Maf001_Negative_Streaming_Cancellation_Throws_And_Halts_Early()
    {
        var client = new DeterministicStreamingChatClient(
            chunks: ["chunk1", "chunk2", "chunk3", "chunk4"]);

        var agent = client.AsAIAgent(name: "CancelingAgent", instructions: "Stream responses");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("stream", cancellationToken: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Maf002_Native_Function_Tool_Invocation_And_Result_Handling()
    {
        var toolInvocations = 0;
        var tool = AIFunctionFactory.Create((int a, int b) =>
        {
            toolInvocations++;
            return a + b;
        }, "AddNumbers", "Adds two integers");

        var client = new ToolRoundTripChatClient(functionName: "AddNumbers", arguments: new Dictionary<string, object?>
        {
            ["a"] = 17,
            ["b"] = 25
        });

        var agent = client.AsAIAgent(name: "MathAgent", instructions: "Perform math", tools: [tool]);

        var response = await agent.RunAsync("Calculate 17 + 25");

        Assert.NotNull(response);
        Assert.Equal(1, toolInvocations);
        Assert.Equal(2, client.Calls); // 1: tool call request, 2: final answer after tool result
        Assert.Contains("42", response.Text, StringComparison.Ordinal);
        Assert.Contains("result is 42", client.ObservedToolResultText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Maf002_Middleware_Invocation_Order_Preserved()
    {
        var orderLog = new List<string>();

        var baseClient = new TestEchoChatClient();

        var client = new ChatClientBuilder(baseClient)
            .Use((inner) => new LoggingMiddlewareChatClient(inner, "Outer", orderLog))
            .Use((inner) => new LoggingMiddlewareChatClient(inner, "Inner", orderLog))
            .Build();

        var agent = client.AsAIAgent(name: "MiddlewareAgent", instructions: "Echo");

        var response = await agent.RunAsync("test pipeline");

        Assert.NotNull(response);
        Assert.Equal("Echo: test pipeline", response.Text);

        // Verification of strict middleware pipeline ordering:
        // Outer:Before -> Inner:Before -> Core -> Inner:After -> Outer:After
        Assert.Equal([
            "Outer:Before",
            "Inner:Before",
            "CoreClient",
            "Inner:After",
            "Outer:After"
        ], orderLog);
    }

    private sealed class DeterministicStreamingChatClient(
        IReadOnlyList<string> chunks,
        string? structuredKey = null,
        string? structuredVal = null) : IChatClient
    {
        public int StreamingCalls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("deterministic-stream");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join(string.Empty, chunks))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamingCalls++;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
                {
                    RawRepresentation = structuredKey is not null ? new KeyValuePair<string, object?>(structuredKey, structuredVal) : null
                };
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolRoundTripChatClient(string functionName, IDictionary<string, object?> arguments) : IChatClient
    {
        public int Calls { get; private set; }
        public string? ObservedToolResultText { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("tool-roundtrip");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            if (Calls == 1)
            {
                var functionCall = new FunctionCallContent("call_1", functionName, arguments);
                var callMessage = new ChatMessage(ChatRole.Assistant, [functionCall]);
                return Task.FromResult(new ChatResponse(callMessage));
            }

            var toolResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            ObservedToolResultText = $"result is {toolResult?.Result}";
            var finalMessage = new ChatMessage(ChatRole.Assistant, $"The computed {ObservedToolResultText}");
            return Task.FromResult(new ChatResponse(finalMessage));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, res.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("test-echo");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastUser = messages.Last(m => m.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo: {lastUser}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, res.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LoggingMiddlewareChatClient(IChatClient inner, string stage, List<string> orderLog) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            orderLog.Add($"{stage}:Before");
            if (stage == "Inner") orderLog.Add("CoreClient");
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            orderLog.Add($"{stage}:After");
            return response;
        }
    }
}
