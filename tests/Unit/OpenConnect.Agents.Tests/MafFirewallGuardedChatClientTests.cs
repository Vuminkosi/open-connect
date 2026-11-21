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
    [Theory]
    [InlineData(false, "alice%40example.com")]
    [InlineData(true, "alice%2540example.com")]
    public async Task Encoded_sensitive_text_is_blocked_in_model_requests_and_responses(bool output, string encoded)
    {
        var inner = new TestChatClient { Output = output ? encoded : "safe" };
        var sink = new RecordingSink();
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, sink)), Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync(
            [new ChatMessage(ChatRole.User, output ? "safe" : encoded)]));
        if (!output) Assert.Null(inner.LastText);
        Assert.Contains(sink.Events, evidence => !evidence.Allowed && evidence.Categories.ContainsKey("EMAIL") &&
            evidence.Destination == (output ? EgressDestination.Output : EgressDestination.ExternalModel));
        Assert.DoesNotContain(encoded, System.Text.Json.JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encoded_sensitive_stream_is_blocked_before_any_update_is_released()
    {
        var inner = new TestChatClient { StreamChunks = ["alice%", "40example.com"] };
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(firewall), Provider);
        var received = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
        {
            await foreach (var update in guarded.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "safe")]))
                received.Add(update);
        });
        Assert.Empty(received);
    }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_blocks_sensitive_value_split_across_messages(bool streaming)
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var messages = new[] { new ChatMessage(ChatRole.User, "person@"), new ChatMessage(ChatRole.User, "example.test") };

        if (streaming)
            await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
            {
                await foreach (var _ in guarded.GetStreamingResponseAsync(messages)) { }
            });
        else
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync(messages));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task SplitMessageBlockRecordsTheActualActionWithoutContent()
    {
        var inner = new TestChatClient();
        var sink = new RecordingSink();
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, sink)), Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "person@"), new ChatMessage(ChatRole.User, "example.test")]));
        var blocked = Assert.Single(sink.Events, item => !item.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, blocked.Action);
        Assert.Equal("sensitive_detected", blocked.ReasonCode);
        Assert.Equal(1, blocked.Categories["EMAIL"]);
        Assert.Equal(EgressDestination.ExternalModel, blocked.Destination);
        Assert.DoesNotContain("person@example.test", System.Text.Json.JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("blocked_action")]
    [InlineData("review_action")]
    [InlineData("unknown_action")]
    [InlineData("disabled")]
    [InlineData("retained_entity")]
    public async Task Inconsistent_gate_decisions_never_forward_request_text(string failure)
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(text => failure switch
        {
            "blocked_action" => new(true, text, [], SecurityPolicyAction.Block, "test"),
            "review_action" => new(true, text, [], SecurityPolicyAction.Review, "test"),
            "unknown_action" => new(true, text, [], (SecurityPolicyAction)999, "test"),
            "disabled" => new(true, text, [], SecurityPolicyAction.Allow, "privacy_enforcement_disabled"),
            _ => new(true, text, [new EntitySpan(0, text.Length, "SECRET")], SecurityPolicyAction.Mask, "test"),
        }), Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "secret")]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Inconsistent_gate_decision_never_releases_response_text()
    {
        var inner = new TestChatClient { Output = "secret" };
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(text => text == "secret"
            ? new EgressDecision(true, text, [new EntitySpan(0, 6, "SECRET")], SecurityPolicyAction.Mask, "test")
            : Allow(text)), Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "safe")]));
    }

    [Theory]
    [InlineData("203.0.113.42")]
    [InlineData("2001:db8::1234")]
    [InlineData("02:11:22:33:44:55")]
    [InlineData("123-45-6789")]
    public async Task Guarded_chat_masks_unlabeled_identifiers_in_requests_and_responses(string sensitive)
    {
        var inner = new TestChatClient { Output = "Result " + sensitive };
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(firewall), Provider);
        var result = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "Input " + sensitive)]);
        Assert.DoesNotContain(sensitive, inner.LastText!, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, result.Text, StringComparison.Ordinal);
        Assert.Contains("[OC_PERSONAL_DATA]", inner.LastText!, StringComparison.Ordinal);
        Assert.Contains("[OC_PERSONAL_DATA]", result.Text, StringComparison.Ordinal);
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
    public async Task Split_sensitive_value_in_tool_result_is_blocked_before_model_call()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new { parts = new[] { "alice@", "example.com" } })]);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), tool]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Split_sensitive_value_in_model_tool_arguments_is_blocked_before_return()
    {
        var inner = new TestChatClient { OutputMessage = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["parts"] = new[] { "alice@", "example.com" } })]) };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request")]));
    }

    [Fact]
    public async Task Sensitive_value_split_across_tool_messages_is_blocked_before_model_call()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var first = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "alice@")]);
        var second = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", "example.com")]);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), first, second]));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Sensitive_value_split_across_model_tool_calls_is_blocked_before_return()
    {
        var inner = new TestChatClient { OutputMessage = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["value"] = "alice@" }),
             new FunctionCallContent("call-2", "lookup", new Dictionary<string, object?> { ["value"] = "example.com" })]) };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Split_tool_content_records_content_free_block_evidence(bool output)
    {
        var inner = new TestChatClient();
        var sink = new RecordingSink();
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, sink)), Provider);
        if (output)
        {
            inner.OutputMessage = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["value"] = "alice@" }),
                 new FunctionCallContent("call-2", "lookup", new Dictionary<string, object?> { ["value"] = "example.com" })]);
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request")]));
        }
        else
        {
            var first = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "alice@")]);
            var second = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", "example.com")]);
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), first, second]));
            Assert.Equal(0, inner.Calls);
        }
        var evidence = Assert.Single(sink.Events, item => !item.Allowed);
        Assert.Equal(output ? EgressDestination.Output : EgressDestination.ExternalModel, evidence.Destination);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal("sensitive_detected", evidence.ReasonCode);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.DoesNotContain("alice@", System.Text.Json.JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", System.Text.Json.JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
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
    public async Task Escaped_sensitive_tool_result_never_reaches_model_uninspected()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        using var json = System.Text.Json.JsonDocument.Parse("""{"email":"person\u0040example.test"}""");
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", json.RootElement)]);
        try
        {
            await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), tool]);
            var result = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionResultContent>());
            var value = Assert.IsType<System.Text.Json.JsonElement>(result.Result).GetProperty("email").GetString();
            Assert.DoesNotContain("person@example.test", value!, StringComparison.OrdinalIgnoreCase);
        }
        catch (PrivacyBlockedException)
        {
            Assert.Equal(0, inner.Calls);
        }
    }

    [Fact]
    public async Task Escaped_sensitive_tool_arguments_never_leave_the_response_uninspected()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"email":"person\u0040example.test"}""");
        var inner = new TestChatClient { OutputMessage = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["payload"] = json.RootElement })]) };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        try
        {
            var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request")]);
            var call = Assert.Single(Assert.Single(response.Messages).Contents.OfType<FunctionCallContent>());
            var payload = Assert.IsType<Dictionary<string, object?>>(call.Arguments!["payload"]);
            Assert.DoesNotContain("person@example.test", Assert.IsType<string>(payload["email"]), StringComparison.OrdinalIgnoreCase);
        }
        catch (PrivacyBlockedException) { }
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
    public async Task Escaped_labeled_tool_result_does_not_expose_name_to_provider()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"full\u004eame":"Jordan Example"}""");
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", json.RootElement)]);

        try
        {
            await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), tool]);
            var result = Assert.Single(inner.LastMessages[1].Contents.OfType<FunctionResultContent>());
            Assert.DoesNotContain("Jordan Example", System.Text.Json.JsonSerializer.Serialize(result.Result));
        }
        catch (PrivacyBlockedException)
        {
            Assert.Equal(0, inner.Calls);
        }
    }

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
    public async Task Response_is_sanitized_with_output_context()
    {
        var inner = new TestChatClient { Output = "Contact alice@example.test" };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.DoesNotContain("alice@example.test", response.Text, StringComparison.Ordinal);
        Assert.Contains("[OC_EMAIL]", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_content_free_evidence_for_sensitive_requests_and_responses()
    {
        var inner = new TestChatClient { Output = "Reply to output@example.test" };
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var sink = new RecordingSink();
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, sink)), Provider);

        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "Contact input@example.test")]);

        Assert.DoesNotContain("input@example.test", inner.LastText!, StringComparison.Ordinal);
        Assert.DoesNotContain("output@example.test", response.Text, StringComparison.Ordinal);
        var request = Assert.Single(sink.Events, item => item.Destination == EgressDestination.ExternalModel);
        var output = Assert.Single(sink.Events, item => item.Destination == EgressDestination.Output);
        Assert.True(request.Allowed);
        Assert.True(output.Allowed);
        Assert.Equal(SecurityPolicyAction.Mask, request.Action);
        Assert.Equal(SecurityPolicyAction.Mask, output.Action);
        Assert.Equal(1, request.Categories["EMAIL"]);
        Assert.Equal(1, output.Categories["EMAIL"]);
        var evidence = System.Text.Json.JsonSerializer.Serialize(sink.Events);
        Assert.DoesNotContain("input@example.test", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("output@example.test", evidence, StringComparison.Ordinal);
    }

    private sealed class RecordingSink : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Events { get; } = [];
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Evidence_failure_blocks_request_before_model_call()
    {
        var inner = new TestChatClient();
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var guarded = new FirewallGuardedChatClient(inner, new EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, new FailingSink())), Provider);
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "person@example.test")]));
        Assert.Equal(0, inner.Calls);
        Assert.DoesNotContain("private storage failure", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class FailingSink : IHostPrivacyEvidenceSink
    {
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private storage failure");
    }

    [Fact]
    public async Task Response_denial_does_not_expose_model_content()
    {
        var inner = new TestChatClient { Output = "private-response" };
        var guarded = new FirewallGuardedChatClient(inner,
            new FakeGate(text => text == "private-response" ? Block() : Allow(text)), Provider);
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.DoesNotContain("private-response", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_rejects_uninspected_binary_content()
    {
        var inner = new TestChatClient
        {
            OutputMessage = new ChatMessage(ChatRole.Assistant,
                [new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        };
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_rejects_uninspected_binary_before_model_call(bool withText)
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var contents = new List<AIContent>();
        if (withText) contents.Add(new TextContent("describe this"));
        contents.Add(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, contents)]));
        Assert.DoesNotContain("image/png", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Request_removes_uninspected_message_metadata_before_model_call()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var message = new ChatMessage(ChatRole.User, "hello") { RawRepresentation = "private" };
        message.AdditionalProperties = new() { ["private"] = "sensitive value" };
        await guarded.GetResponseAsync([message]);
        var forwarded = Assert.Single(inner.LastMessages!);
        Assert.Null(forwarded.RawRepresentation);
        Assert.Null(forwarded.AdditionalProperties);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("author")]
    [InlineData("call")]
    [InlineData("name")]
    [InlineData("result")]
    public async Task Request_rejects_sensitive_protocol_identifiers_before_model_call(string field)
    {
        const string email = "person@example.test";
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(
            field == "call" ? email : "call-1", field == "name" ? email : "lookup", new Dictionary<string, object?>())]);
        if (field == "message") message.MessageId = email;
        if (field == "author") message.AuthorName = email;
        if (field == "result") message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(email, "ok")]);
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([message]));
        Assert.DoesNotContain(email, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Response_rejects_sensitive_tool_identifiers()
    {
        var inner = new TestChatClient { OutputMessage = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("person@example.test", "lookup", new Dictionary<string, object?>())]) };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
    }

    [Fact]
    public async Task Request_options_are_screened_without_mutating_caller_options()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var options = new ChatOptions { Instructions = "Contact person@example.test", ModelId = "model" };
        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], options);
        Assert.Contains("person@example.test", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", inner.LastOptions!.Instructions!, StringComparison.Ordinal);
        Assert.Equal("model", inner.LastOptions.ModelId);
    }

    [Fact]
    public async Task Sensitive_option_identifiers_and_opaque_options_are_blocked()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ConversationId = "person@example.test" }));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { AdditionalProperties = new() { ["private"] = "value" } }));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Sensitive_tool_descriptions_and_response_schemas_are_blocked()
    {
        var inner = new TestChatClient();
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var tool = AIFunctionFactory.Create((string value) => value,
            new AIFunctionFactoryOptions { Name = "lookup", Description = "Contact person@example.test" });
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [tool] }));
        using var schema = System.Text.Json.JsonDocument.Parse("""{"type":"string","description":"person\u0040example.test"}""");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ResponseFormat = new ChatResponseFormatJson(schema.RootElement.Clone(), "result", "result") }));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Tool_definitions_are_snapshotted_and_callable_functions_remain_callable()
    {
        var inner = new TestChatClient();
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var declaration = new MutableDeclaration();
        var callable = AIFunctionFactory.Create((string value) => value.ToUpperInvariant(),
            new AIFunctionFactoryOptions { Name = "uppercase", Description = "Convert a string" });
        await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [declaration, callable] });

        declaration.Text = "person@example.test";
        var tools = inner.LastOptions!.Tools!;
        Assert.Equal("Safe description", tools[0].Description);
        Assert.NotSame(declaration, tools[0]);
        var guardedFunction = Assert.IsAssignableFrom<AIFunction>(tools[1]);
        Assert.NotSame(callable, guardedFunction);
        var result = await guardedFunction.InvokeAsync(new AIFunctionArguments { ["value"] = "hello" });
        Assert.Equal("HELLO", result?.ToString());
    }

    private sealed class MutableDeclaration : AIFunctionDeclaration
    {
        public string Text { get; set; } = "Safe description";
        public override string Name => "safe_tool";
        public override string Description => Text;
    }

    [Theory]
    [InlineData("response")]
    [InlineData("conversation")]
    [InlineData("model")]
    [InlineData("finish")]
    public async Task Response_rejects_sensitive_provider_identifiers(string field)
    {
        const string email = "person@example.test";
        var output = new ChatResponse([new ChatMessage(ChatRole.Assistant, "visible")]);
        if (field == "response") output.ResponseId = email;
        if (field == "conversation") output.ConversationId = email;
        if (field == "model") output.ModelId = email;
        if (field == "finish") output.FinishReason = new ChatFinishReason(email);
        var inner = new TestChatClient { OutputResponse = output };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() => guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.DoesNotContain(email, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_removes_uninspected_provider_metadata()
    {
        var message = new ChatMessage(ChatRole.Assistant, "visible") { RawRepresentation = "private" };
        var inner = new TestChatClient { OutputMessage = message };
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.Null(response.RawRepresentation);
        Assert.Null(Assert.Single(response.Messages).RawRepresentation);
    }

    [Fact]
    public async Task Response_preserves_standard_usage_without_arbitrary_metadata()
    {
        var usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5, TotalTokenCount = 17,
            AdditionalCounts = new() { ["person@example.test"] = 1 } };
        var inner = new TestChatClient { OutputMessage = new ChatMessage(ChatRole.Assistant,
            [new TextContent("visible"), new UsageContent(usage)]) };
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var response = await guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        var safe = Assert.Single(Assert.Single(response.Messages).Contents.OfType<UsageContent>()).Details;
        Assert.Equal(12, safe.InputTokenCount);
        Assert.Equal(5, safe.OutputTokenCount);
        Assert.Equal(17, safe.TotalTokenCount);
        Assert.Null(safe.AdditionalCounts);
    }

    [Fact]
    public async Task Streaming_preserves_standard_usage_without_arbitrary_metadata()
    {
        var inner = new TestChatClient { StreamUsage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5,
            AdditionalCounts = new() { ["person@example.test"] = 1 } } };
        var guarded = new FirewallGuardedChatClient(inner, new FakeGate(Allow), Provider);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in guarded.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            updates.Add(update);
        var response = updates.ToChatResponse();
        Assert.Equal(12, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        Assert.Null(response.Usage.AdditionalCounts);
    }

    [Fact]
    public async Task Streaming_buffers_and_sanitizes_values_split_between_updates()
    {
        var inner = new TestChatClient { StreamChunks = ["Contact alice@", "example.test now"] };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in guarded.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            updates.Add(update);
        var result = updates.ToChatResponse().Text;
        Assert.DoesNotContain("alice@example.test", result, StringComparison.Ordinal);
        Assert.Contains("[OC_EMAIL]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_blocks_sensitive_value_split_across_messages()
    {
        var inner = new TestChatClient { OutputMessages =
            [new ChatMessage(ChatRole.Assistant, "Contact alice@"),
             new ChatMessage(ChatRole.Assistant, "example.test now")] };
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        var guarded = new FirewallGuardedChatClient(inner, gate, Provider);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() =>
            guarded.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
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
        Assert.Equal(3, gate.Evaluations);

        var singleGate = new FakeGate(content => Allow(content));
        var singleGuarded = new FirewallGuardedChatClient(new TestChatClient(), singleGate, Provider);
        var rewrapped = new MafRealAgentFactory(registry, singleGuarded, singleGate, Provider);
        await rewrapped.RunAsync(id, "hello");

        Assert.Equal(3, singleGate.Evaluations);
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
        public string? Output { get; set; }
        public ChatMessage? OutputMessage { get; set; }
        public ChatResponse? OutputResponse { get; set; }
        public List<ChatMessage>? OutputMessages { get; set; }
        public string[]? StreamChunks { get; set; }
        public UsageDetails? StreamUsage { get; set; }
        public int Calls { get; private set; }
        public string? LastText { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastOptions = options;
            LastMessages = messages.ToList();
            LastText = LastMessages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(OutputResponse ?? new ChatResponse(OutputMessages ?? [OutputMessage ?? new ChatMessage(ChatRole.Assistant, Output ?? LastText)])
                { RawRepresentation = "private-provider-response" });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var chunk in StreamChunks ?? [response.Text])
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            if (StreamUsage is not null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(StreamUsage)]);
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
