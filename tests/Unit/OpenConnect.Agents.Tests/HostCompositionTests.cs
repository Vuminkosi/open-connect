using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Core;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class HostCompositionTests
{
    [Fact]
    public async Task AddOpenConnect_registers_default_lead_that_runs_through_maf_adapter()
    {
        var host = Host.CreateApplicationBuilder(args: []);
        host.Services.AddOpenConnect();
        using var app = host.Build();
        var registry = app.Services.GetRequiredService<IAgentDefinitionRegistry>();
        var lead = Assert.Single(registry.GetAll());
        var leadId = new AgentId("lead");
        var client = new EchoChatClient();

        Assert.Equal(leadId, lead.Id);
        Assert.Equal(AgentExecutionMode.Interactive, lead.ExecutionMode);
        Assert.Same(lead, registry.Get(leadId));

        var factory = new MafRealAgentFactory(registry, client);
        var response = await factory.RunAsync(leadId, "hello");

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, client.Calls);
        Assert.Contains(factory.GetTrace(), record => record.AgentId == leadId.Value);
    }

    private sealed class EchoChatClient : IChatClient
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
            var text = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
