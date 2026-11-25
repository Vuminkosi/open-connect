using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Messaging;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MessagingAgentToolkitTests
{
    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("private_key")]
    [InlineData("download_url")]
    [InlineData("push_token")]
    public async Task BrokeredMessagingAIFunction_BlocksForbiddenSecretOrUrlExposureToModel(string forbiddenKey)
    {
        var binding = new MessagingAgentToolBinding(
            new ToolId("messaging_send"),
            "Messaging Send",
            "Sends a message",
            """{"type":"object","properties":{"text":{"type":"string"}}}""");

        var functions = MessagingAgentToolkit.CreateFunctions(
            [binding],
            (_, _, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["status"] = "committed",
                [forbiddenKey] = "leaked-value"
            })));

        var fn = Assert.Single(functions);
        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
            await fn.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }));
    }

    [Fact]
    public async Task BrokeredMessagingAIFunction_AppliesEgressGateToSafeOutput()
    {
        var binding = new MessagingAgentToolBinding(
            new ToolId("messaging_get_status"),
            "Messaging Get Status",
            "Gets status",
            """{"type":"object","properties":{}}""");

        var gate = new FakeEgressGate(allow: true);
        var ctxProvider = new FakeEgressContextProvider();

        var functions = MessagingAgentToolkit.CreateFunctions(
            [binding],
            (_, _, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { status = "active", active_human_count = 1 })),
            gate,
            ctxProvider);

        var result = (JsonElement)(await functions[0].InvokeAsync(new AIFunctionArguments()))!;
        Assert.Equal("active", result.GetProperty("status").GetString());
        Assert.Equal(1, gate.Calls);

        // When egress gate blocks, function fails closed with PrivacyBlockedException
        var blockingFunctions = MessagingAgentToolkit.CreateFunctions(
            [binding],
            (_, _, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { status = "active" })),
            new FakeEgressGate(allow: false),
            ctxProvider);

        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
            await blockingFunctions[0].InvokeAsync(new AIFunctionArguments()));
    }

    private sealed class FakeEgressContextProvider : IEgressContextProvider
    {
        public EgressContext GetContext() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()), EgressDestination.Tool);
    }

    private sealed class FakeEgressGate(bool allow) : IEgressGate
    {
        public int Calls { get; private set; }

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(allow
                ? new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "allowed")
                : new EgressDecision(false, null, [], SecurityPolicyAction.Block, "blocked"));
        }
    }
}
