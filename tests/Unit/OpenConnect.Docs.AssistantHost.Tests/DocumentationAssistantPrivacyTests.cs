using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Docs.AssistantHost.Execution;
using OpenConnect.LiveUI.Agents;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.AssistantHost.Tests;

public sealed class DocumentationAssistantPrivacyTests
{
    [Fact]
    public async Task Greeting_with_documentation_and_liveui_tools_passes_request_screening()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var instructions = (string)typeof(DocumentationExecutionInputsFactory).GetField("Instructions", flags)!.GetRawConstantValue()!;
        var documentationTools = (IReadOnlyList<AITool>)typeof(DocumentationExecutionInputsFactory).GetField("DocumentationTools", flags)!.GetValue(null)!;
        var tools = LiveUIClientAgentToolFactory.CreateToolsForConnection(new Broker(), session, "connection").Concat(documentationTools).ToList();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var inner = new Client();
        var client = new FirewallGuardedChatClient(inner, gate,
            new FixedEgressContextProvider(new EgressContext(tenant, principal, session, EgressDestination.ExternalModel)));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], new ChatOptions
        {
            Instructions = instructions + "\n\n" + LiveUIAgentGuidance.Instructions,
            Tools = tools
        });
        Assert.True(inner.Called);
    }

    private sealed class Broker : IClientInteractionBroker
    {
        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<ClientInteractionResponse?>(null);
    }

    private sealed class Client : IChatClient
    {
        public bool Called { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello!")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello!");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
