using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI.Agents;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIConversationFallbackTests
{
    [Fact]
    public async Task Unavailable_browser_and_worker_fall_back_to_server_planning_from_authorized_page_context()
    {
        var broker = new FallbackPlanningBroker();
        var client = new ConversationFallbackChatClient();
        var factory = new MafRealAgentFactory(
            new TestRegistry(new AgentDefinition(new AgentId("liveui-fallback"), "OpenConnect assistant")),
            client);
        var sessionId = new SessionId(Ulid.NewUlid());
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker, sessionId);

        var response = await factory.RunAsync(
            new AgentId("liveui-fallback"),
            "Pull up my active tasks",
            tools,
            instructions: "For UI requests, first try local LiveUI planning. If unavailable, inspect authorized current-page targets, choose only the exact target and capability returned, execute through LiveUI, and verify its receipt.");

        Assert.Equal(sessionId, broker.SessionId);
        Assert.Equal(new[] { "infer_plan", "get_current_page", "execute", "await_state" }, broker.Requests.Select(item => item.Operation));
        Assert.Equal("Pull up my active tasks", broker.Requests[0].Value);
        Assert.Equal("todo.tab.active", broker.Requests[2].TargetId);
        Assert.Equal("SelectTab", broker.Requests[2].Action);
        Assert.Equal("selected", broker.Requests[3].StateName);
        Assert.Equal(5, client.Calls);
        Assert.Contains("Active tab is selected", response.Text, StringComparison.Ordinal);
    }

    private sealed class FallbackPlanningBroker : IClientInteractionBroker
    {
        public List<ClientInteractionRequest> Requests { get; } = [];
        public SessionId SessionId { get; private set; }

        public Task<ClientInteractionResponse?> RequestAsync(
            SessionId sessionId,
            ClientInteractionRequest request,
            string expectedResultKind,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            SessionId = sessionId;
            var payload = request.Operation switch
            {
                "infer_plan" => "{\"provider\":\"Browser\",\"plan\":null,\"outcome\":\"inference_unavailable\"}",
                "get_current_page" => "{\"routeId\":\"todo.home\",\"targets\":[{\"targetId\":\"todo.tab.active\",\"capabilities\":[\"Activate\"]}],\"navigableRoutes\":[]}",
                "execute" or "await_state" => "{\"outcome\":\"Completed\",\"postconditionVerified\":true,\"verifiedState\":\"selected\"}",
                _ => throw new InvalidOperationException($"Unexpected LiveUI operation '{request.Operation}'.")
            };
            var resultKind = request.Operation switch
            {
                "infer_plan" => "plan",
                "get_current_page" => "page",
                _ => "action"
            };
            Assert.Equal(expectedResultKind, resultKind);
            return Task.FromResult<ClientInteractionResponse?>(new(
                request.CorrelationId,
                resultKind,
                payload));
        }
    }

    private sealed class ConversationFallbackChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var function = Calls switch
            {
                1 => (Name: "liveui_infer_plan", Arguments: new Dictionary<string, object?> { ["intent"] = "Pull up my active tasks" }),
                2 => (Name: "liveui_get_current_page", Arguments: new Dictionary<string, object?>()),
                3 => (Name: "liveui_execute", Arguments: new Dictionary<string, object?> { ["targetId"] = "todo.tab.active", ["action"] = "SelectTab" }),
                4 => (Name: "liveui_await_state", Arguments: new Dictionary<string, object?> { ["targetId"] = "todo.tab.active", ["stateName"] = "selected", ["stateValue"] = "true" }),
                _ => (Name: string.Empty, Arguments: new Dictionary<string, object?>())
            };
            if (function.Name.Length > 0)
            {
                var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(
                    options!.Tools!, candidate => candidate is AIFunction actual && actual.Name == function.Name));
                if (Calls == 3)
                {
                    Assert.Contains(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                        result => result.Result?.ToString()?.Contains("todo.tab.active", StringComparison.Ordinal) == true);
                }

                var call = new FunctionCallContent($"liveui-fallback-{Calls}", tool.Name, function.Arguments);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            Assert.Contains(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString() is { } value &&
                    value.Contains("postconditionVerified", StringComparison.Ordinal) &&
                    value.Contains("true", StringComparison.Ordinal));
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "The Active tab is selected and the task list shows active tasks.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
