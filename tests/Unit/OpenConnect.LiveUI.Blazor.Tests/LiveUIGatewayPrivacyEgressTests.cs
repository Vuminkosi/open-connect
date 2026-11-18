using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIGatewayPrivacyEgressTests
{
    [Fact]
    public async Task Liveui_gateway_sends_tokenized_canaries_and_vault_retains_values_only_as_encrypted_entries()
    {
        const string email = "canary.user@example.test";
        const string phone = "+1-555-867-5309";
        const string card = "4532015112830366";
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());
        var scope = new PrivacyScope(tenant, principal, session, "redaction");

        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var detector = new RulesSecurityDetector(vault);
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(detector));
        var egress = new EgressGate(firewall);
        var egressContext = new EgressContext(tenant, principal, session, EgressDestination.ExternalModel);
        var inner = new CapturingChatClient("{\"schemaVersion\":\"openconnect.liveui/v1\",\"actions\":[]}");
        var guarded = new FirewallGuardedChatClient(inner, egress, new FixedEgressContextProvider(egressContext));
        var provider = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        var request = new LiveUIInferenceRequest(
            $"Find the record for {email}, call {phone}, card {card}", session.Value.ToString(), "todo.home",
            LiveUISchema.CurrentVersion, []);

        var result = await provider.GeneratePlanAsync(request);

        Assert.NotNull(result.Plan);
        Assert.Equal(1, inner.Calls);
        Assert.DoesNotContain(email, inner.LastUserText, StringComparison.Ordinal);
        Assert.DoesNotContain(phone, inner.LastUserText, StringComparison.Ordinal);
        Assert.DoesNotContain("+1-", inner.LastUserText, StringComparison.Ordinal);
        Assert.DoesNotContain(card, inner.LastUserText, StringComparison.Ordinal);

        var tokens = Regex.Matches(inner.LastUserText!, @"\[OC_[A-F0-9]{32}\]")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, tokens.Length);

        var restored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var value = await vault.ResolveAsync(token, scope);
            Assert.NotNull(value);
            restored.Add(value);
        }

        var expected = new[] { email, phone, card }.ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(restored),
            $"Outbound content: {inner.LastUserText}; restored values: {string.Join(" | ", restored)}");
        Assert.Null(await vault.ResolveAsync(tokens[0], scope with { Purpose = "external-model" }));
    }

    private sealed class CapturingChatClient(string responseText) : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastUserText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUserText = string.Join("\n", messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            Assert.Equal(ChatResponseFormat.Json, options?.ResponseFormat);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
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
