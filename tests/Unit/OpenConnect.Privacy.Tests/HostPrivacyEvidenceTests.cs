using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class HostPrivacyEvidenceTests
{
    [Fact]
    public void Cloud_tenant_mapping_uses_guid_network_byte_order()
    {
        Assert.Equal("00000000000000000000000001", CloudTenantIdentity.ToHostTenantId(Guid.Parse("00000000-0000-0000-0000-000000000001")).Value.ToString());
        Assert.Equal("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", CloudTenantIdentity.ToHostTenantId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")).Value.ToString());
        Assert.Throws<ArgumentException>(() => CloudTenantIdentity.ToHostTenantId(Guid.Empty));
    }

    [Fact]
    public void Evidence_contains_only_bounded_allowlisted_metadata()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var eventId = Guid.NewGuid();
        var decision = new EgressDecision(true, "sensitive-content", [new(4, 8, "EMAIL"), new(3, 2, "EMAIL"),
            new(1, 2, "private detector label")], SecurityPolicyAction.Mask, "sensitive-reason");
        var evidence = HostPrivacyEvidence.Create(eventId, context, decision);
        Assert.Equal(eventId, evidence.EventId);
        Assert.Equal(EgressDestination.Output, evidence.Destination);
        Assert.Equal(2, evidence.Categories["EMAIL"]);
        Assert.Equal(1, evidence.Categories["OTHER"]);
        var json = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("sensitive-content", json);
        Assert.DoesNotContain("sensitive-reason", json);
        Assert.DoesNotContain("private detector label", json);
        Assert.DoesNotContain(context.Principal.Value.ToString(), json);
        Assert.DoesNotContain(context.Session.Value.ToString(), json);
        Assert.DoesNotContain("Start", json);
        Assert.DoesNotContain("Length", json);
    }

    [Theory]
    [InlineData(SecurityPolicyAction.Block)]
    [InlineData(SecurityPolicyAction.Review)]
    public void Allowed_evidence_cannot_claim_a_blocking_or_review_action(SecurityPolicyAction action)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var decision = new EgressDecision(true, "safe", [], action, "test");
        Assert.Throws<ArgumentException>(() => HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision));
    }

    [Theory]
    [InlineData("detector_failure", "inspection_failed")]
    [InlineData("required_detector_unavailable", "enforcement_unavailable")]
    [InlineData("private diagnostic", "not_recorded")]
    public void BlockedEvidenceUsesAllowlistedReasonWithoutCopyingDiagnostics(string reason, string expected)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context,
            new EgressDecision(false, null, [], SecurityPolicyAction.Block, reason));
        Assert.Equal(expected, evidence.ReasonCode);
        if (reason == "private diagnostic") Assert.DoesNotContain(reason, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedOutgoingSensitiveValueProducesAContentFreeCaseReason()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var decision = new OpenConnect.Privacy.Gates.OutgoingContentScanner().ScanOutgoing("Contact person@example.com", context);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision);

        Assert.False(evidence.Allowed);
        Assert.Equal("sensitive_detected", evidence.ReasonCode);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.DoesNotContain("person@example.com", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("From 203.0.113.42", "203.0.113.42", EgressDestination.ExternalModel)]
    [InlineData("From 2001:db8::1234", "2001:db8::1234", EgressDestination.ExternalModel)]
    [InlineData("Device 02:11:22:33:44:55", "02:11:22:33:44:55", EgressDestination.Output)]
    [InlineData("Reference 123-45-6789", "123-45-6789", EgressDestination.Output)]
    public async Task Unlabeled_sensitive_values_create_content_free_evidence(string content, string sensitive, EgressDestination destination)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), destination);
        var inner = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var sink = new Sink();
        var decision = await new EvidenceRecordingPrivacyFirewall(inner, sink).EvaluateAsync(content, context);
        Assert.DoesNotContain(sensitive, decision.SanitizedContent!, StringComparison.Ordinal);
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(1, evidence.Categories["PERSONAL_DATA"]);
        Assert.Equal(destination, evidence.Destination);
        Assert.DoesNotContain(sensitive, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EgressDestination.ExternalModel)]
    [InlineData(EgressDestination.Output)]
    public async Task Invisible_separator_detection_reports_category_without_content(EgressDestination destination)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), destination);
        var inner = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var sink = new Sink();
        var decision = await new EvidenceRecordingPrivacyFirewall(inner, sink).EvaluateAsync("al\u200bice@example.com", context);
        Assert.DoesNotContain("al\u200bice@example.com", decision.SanitizedContent!, StringComparison.Ordinal);
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.DoesNotContain("example.com", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"parts":["alice@","example.com"]}""", "EMAIL", "alice@example.com")]
    [InlineData("""{"a":123,"b":"-45-","c":"6789"}""", "PERSONAL_DATA", "123-45-6789")]
    [InlineData("""{"value":"alice%40example.com"}""", "EMAIL", "alice@example.com")]
    [InlineData("""{"value":"alice%2540example.com"}""", "EMAIL", "alice@example.com")]
    [InlineData("""{"parts":["alice%40","example.com"]}""", "EMAIL", "alice@example.com")]
    public async Task Structured_inspection_blocks_sensitive_values_split_across_fields(string body, string category, string sensitive)
    {
        using var document = JsonDocument.Parse(body);
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var recording = new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(recording, document.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(1, evidence.Categories[category]);
        Assert.Equal("sensitive_detected", evidence.ReasonCode);
        Assert.DoesNotContain(sensitive, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new RulesFirewall(), document.RootElement, context));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new RulesGate(), document.RootElement, context));
    }

    [Fact]
    public async Task Structured_inspection_fails_closed_when_encoding_depth_is_exceeded()
    {
        using var document = JsonDocument.Parse("""{"value":"alice%252540example.com"}""");
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink), document.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal("inspection_failed", evidence.ReasonCode);
        Assert.DoesNotContain("alice", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new RulesGate(), document.RootElement, context));
    }

    [Fact]
    public async Task Structured_inspection_accepts_safe_percent_value_at_supported_depth()
    {
        using var document = JsonDocument.Parse("""{"value":"rate%2525"}""");
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        await PrivacyJsonInspection.DemandUnchangedAsync(new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink),
            document.RootElement, context);
        Assert.True(Assert.Single(sink.Events).Allowed);
    }

    [Fact]
    public async Task EncodingVariantsDoNotInflateSensitiveCategoryCounts()
    {
        using var document = JsonDocument.Parse("""{"value":"alice%40example.com%2520"}""");
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink), document.RootElement, context));
        Assert.Equal(1, Assert.Single(sink.Events).Categories["EMAIL"]);
    }

    [Fact]
    public async Task DistinctEncodedFieldsContributeSeparatelyToEvidence()
    {
        using var document = JsonDocument.Parse("""{"first":"alice%40example.com%2520","second":"bob%40example.net%2520"}""");
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink), document.RootElement, context));
        Assert.Equal(2, Assert.Single(sink.Events).Categories["EMAIL"]);
    }

    private sealed class RulesGate : IEgressGate
    {
        private readonly RulesFirewall _firewall = new();
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            _firewall.EvaluateAsync(content, context, cancellationToken);
    }

    [Fact]
    public async Task Standard_egress_gate_batches_structured_evidence()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var gate = new OpenConnect.Privacy.Gates.EgressGate(new EvidenceRecordingPrivacyFirewall(new JsonFirewall(), sink));
        using var json = JsonDocument.Parse("""{"values":["one","two","three"]}""");
        await PrivacyJsonInspection.DemandGateUnchangedAsync(gate, json.RootElement, context);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task DisabledFirewallRecordsStructuredEnforcementUnavailable()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new DisabledFirewall(), sink);
        var gate = new OpenConnect.Privacy.Gates.EgressGate(firewall);
        using var json = JsonDocument.Parse("""{"value":"safe"}""");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(gate, json.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal("enforcement_unavailable", evidence.ReasonCode);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
    }

    [Fact]
    public async Task Disabled_decisions_fail_closed_even_when_provider_claims_enabled()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        using var json = JsonDocument.Parse("""{"value":"safe"}""");
        var inner = new DisabledDecisionFirewall();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(inner, json.RootElement, context));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new DisabledDecisionGate(), json.RootElement, context));

        var sink = new Sink();
        var recording = new EvidenceRecordingPrivacyFirewall(inner, sink);
        Assert.False(await recording.CheckReadinessAsync(context));
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
        sink.Events.Clear();
        Assert.False((await recording.EvaluateAsync("safe", context)).Allowed);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
        sink.Events.Clear();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(recording, json.RootElement, context));
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    private sealed class DisabledDecisionFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "privacy_enforcement_disabled"));
    }

    private sealed class DisabledDecisionGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "privacy_enforcement_disabled"));
    }

    [Theory]
    [InlineData(SecurityPolicyAction.Review)]
    [InlineData((SecurityPolicyAction)999)]
    public async Task Structured_inspection_rejects_invalid_or_review_actions(SecurityPolicyAction action)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        using var json = JsonDocument.Parse("""{"value":"safe"}""");
        var decision = new EgressDecision(true, json.RootElement.GetRawText(), [], action, "test");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new FakeFirewall(decision), json.RootElement, context));
        var sink = new Sink();
        var recording = new EvidenceRecordingPrivacyFirewall(new FakeFirewall(decision), sink);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(recording, json.RootElement, context));
        Assert.Equal("inspection_failed", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task SuccessfulReadinessProbeDoesNotInflateDecisionCounts()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new FakeFirewall(
            new EgressDecision(true, string.Empty, [], SecurityPolicyAction.Allow, "sanitized")), sink);
        Assert.True(await firewall.CheckReadinessAsync(context));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task MalformedReadinessProbeRecordsInspectionFailure()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new FakeFirewall(
            new EgressDecision(true, "changed", [], SecurityPolicyAction.Allow, "private diagnostic")), sink);
        Assert.False(await firewall.CheckReadinessAsync(context));
        Assert.Equal("inspection_failed", Assert.Single(sink.Events).ReasonCode);
        Assert.DoesNotContain("private diagnostic", JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirewallDisabledDuringReadinessProbeIsReported()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new SelfDisablingFirewall(), sink);
        Assert.False(await firewall.CheckReadinessAsync(context));
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task FirewallDisabledDuringTextInspectionBlocksAndReportsUnavailable()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new SelfDisablingFirewall(), sink);

        var decision = await firewall.EvaluateAsync("private content", context);

        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal("enforcement_unavailable", evidence.ReasonCode);
        Assert.DoesNotContain("private content", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_inspection_records_once_and_checks_decoded_strings()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new JsonFirewall(), sink);
        using var clean = JsonDocument.Parse("""{"name":"ok","nested":["safe","text"]}""");
        await PrivacyJsonInspection.DemandUnchangedAsync(firewall, clean.RootElement, context);
        Assert.Single(sink.Events);
        Assert.True(sink.Events[0].Allowed);
        using var sensitive = JsonDocument.Parse("""{"value":"person\u0040example.com"}""");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, sensitive.RootElement, context));
        Assert.Equal(2, sink.Events.Count);
        Assert.False(sink.Events[1].Allowed);
        Assert.Equal(1, sink.Events[1].Categories["EMAIL"]);
    }

    [Theory]
    [InlineData(EgressDestination.Worker)]
    [InlineData(EgressDestination.Output)]
    public async Task Structured_location_is_blocked_and_reported_without_the_value(EgressDestination destination)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), destination);
        var sink = new Sink();
        var inner = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);
        using var document = JsonDocument.Parse("""{"location":{"latitude":-26.3054}}""");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, document.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(destination, evidence.Destination);
        Assert.False(evidence.Allowed);
        Assert.True(evidence.Categories["PERSONAL_DATA"] > 0);
        Assert.DoesNotContain("-26.3054", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_inspection_records_large_supported_numeric_arrays_once()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new JsonFirewall(), sink);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { input = new { values = Enumerable.Repeat(1, 10000) } }));
        await PrivacyJsonInspection.DemandUnchangedAsync(firewall, document.RootElement, context);
        Assert.True(Assert.Single(sink.Events).Allowed);
    }

    private sealed class JsonFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(content.Contains('@')
                ? new EgressDecision(true, "[MASKED]", [new(0, 1, "EMAIL")], SecurityPolicyAction.Mask, "masked")
                : new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "clean"));
    }

    [Theory]
    [InlineData("{\"full\\u004eame\":\"Jordan Example\"}")]
    [InlineData("{\"national\\u0049d\":12345678}")]
    [InlineData("{\"full\\u004eame\":{\"value\":\"Jordan Example\"}}")]
    [InlineData("{\"national\\u0049d\":{\"text\":12345678}}")]
    [InlineData("{\"mobile\\u004eumber\":\"76123456\"}")]
    [InlineData("{\"given\\u004eame\":\"Jordan\"}")]
    [InlineData("{\"full\\u004eame\":{\"nested\":{\"value\":\"Jordan Example\"}}}")]
    [InlineData("{\"national\\u0049d\":[{\"record\":{\"text\":12345678}}]}")]
    public async Task Structured_inspection_preserves_decoded_field_context(string input)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        using var json = JsonDocument.Parse(input);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, json.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(1, evidence.Categories["PERSONAL_DATA"]);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new RulesFirewall(), json.RootElement, context));
    }

    [Theory]
    [InlineData(EgressDestination.Worker, "{\"api_\\u006bey\":\"sensitive-credential\"}")]
    [InlineData(EgressDestination.Output, "{\"pass\\u0077ord\":{\"value\":\"sensitive-credential\"}}")]
    [InlineData(EgressDestination.Output, "{\"aws_secret_access_key\":\"sensitive-credential\"}")]
    [InlineData(EgressDestination.Worker, "{\"pass\\u0077ord\":{\"nested\":[{\"data\":\"sensitive-credential\"}]}}")]
    public async Task Structured_credentials_are_blocked_and_reported_without_content(EgressDestination destination, string input)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), destination);
        using var json = JsonDocument.Parse(input);
        var sink = new Sink();
        var gate = new OpenConnect.Privacy.Gates.EgressGate(new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink));

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(gate, json.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal(destination, evidence.Destination);
        Assert.Equal(1, evidence.Categories["SECRET"]);
        Assert.DoesNotContain("sensitive-credential", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_key_is_redacted_before_output_and_recorded_without_key_material()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink);
        var decision = await firewall.EvaluateAsync("-----BEGIN PRIVATE KEY-----\nPRIVATE-MATERIAL\n-----END PRIVATE KEY-----", context);

        Assert.True(decision.Allowed);
        Assert.DoesNotContain("PRIVATE-MATERIAL", decision.SanitizedContent);
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(SecurityPolicyAction.Mask, evidence.Action);
        Assert.Equal(1, evidence.Categories["SECRET"]);
        Assert.DoesNotContain("PRIVATE-MATERIAL", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_inspection_preserves_labels_for_array_values()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        using var json = JsonDocument.Parse("""{"full\u004eame":["Jordan",["Example"]]}""");
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new RulesFirewall(), sink);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, json.RootElement, context));
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(2, evidence.Categories["PERSONAL_DATA"]);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new RulesFirewall(), json.RootElement, context));
    }

    private sealed class RulesFirewall : IPrivacyFirewall
    {
        private readonly OpenConnect.Privacy.Rules.RulesSecurityDetector _detector = new();
        public bool Enabled => true;
        public async ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            var result = await _detector.DetectAndSanitizeAsync(content, context, cancellationToken);
            return new EgressDecision(true, result.SanitizedContent, result.DetectedEntities, SecurityPolicyAction.Mask, "test");
        }
    }

    [Fact]
    public async Task Structured_inspection_bounds_fragments_without_evidence_recording()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Repeat("safe", 50000)));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new JsonFirewall(), json.RootElement, context));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new CustomGate(false), json.RootElement, context));
    }

    [Fact]
    public async Task Structured_combined_value_limit_blocks_and_records_inspection_failure()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new string('x', 600000), new string('y', 600000) }));
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(new JsonFirewall(), sink), json.RootElement, context));
        Assert.Equal("inspection_failed", Assert.Single(sink.Events).ReasonCode);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new JsonFirewall(), json.RootElement, context));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new CustomGate(false), json.RootElement, context));
    }

    [Fact]
    public async Task Structured_inspection_hides_detector_failures_but_preserves_cancellation()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        using var json = JsonDocument.Parse("""{"value":"safe"}""");
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new FakeFirewall(null), json.RootElement, context));
        Assert.DoesNotContain("secret detector error", error.ToString());
        var gateError = await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandGateUnchangedAsync(new CustomGate(true), json.RootElement, context));
        Assert.DoesNotContain("private gate error", gateError.ToString());
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(new FakeFirewall(null), sink), json.RootElement, context));
        Assert.Equal("inspection_failed", Assert.Single(sink.Events).ReasonCode);
        Assert.DoesNotContain("secret detector error", JsonSerializer.Serialize(sink.Events), StringComparison.Ordinal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrivacyJsonInspection.DemandUnchangedAsync(new JsonFirewall(), json.RootElement, context, cancellation.Token));
    }

    [Fact]
    public async Task MalformedStructuredDetectorResultReportsInspectionFailure()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        using var json = JsonDocument.Parse("""{"value":"safe"}""");
        foreach (var decision in new[]
        {
            new EgressDecision(true, null, [], SecurityPolicyAction.Allow, "invalid"),
            new EgressDecision(true, "safe", [], SecurityPolicyAction.Block, "invalid"),
            new EgressDecision(true, "safe", [new EntitySpan(999, 1, "SECRET")], SecurityPolicyAction.Mask, "invalid"),
            new EgressDecision(true, "safe", [], (SecurityPolicyAction)999, "invalid"),
        })
        {
            var sink = new Sink();
            var firewall = new EvidenceRecordingPrivacyFirewall(new FakeFirewall(decision), sink);
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, json.RootElement, context));
            var evidence = Assert.Single(sink.Events);
            Assert.Equal("inspection_failed", evidence.ReasonCode);
            Assert.Empty(evidence.Categories);
        }
    }

    private sealed class CustomGate(bool fail) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            fail ? throw new InvalidOperationException("private gate error") :
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "clean"));
    }

    [Fact]
    public async Task Structured_evidence_inspects_later_decoded_values_without_counting_raw_matches_twice()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new MultiCategoryFirewall(), sink);
        using var json = JsonDocument.Parse("""{"first":"person@example.com","second":"phone\u002dvalue"}""");

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(firewall, json.RootElement, context));

        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.Equal(1, evidence.Categories["PHONE"]);
    }

    private sealed class MultiCategoryFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            var entities = new List<EntitySpan>();
            if (content.Contains('@')) entities.Add(new(0, 1, "EMAIL"));
            if (content.Contains("phone-value", StringComparison.Ordinal)) entities.Add(new(0, 1, "PHONE"));
            return ValueTask.FromResult(new EgressDecision(entities.Count == 0, entities.Count == 0 ? content : null,
                entities, entities.Count == 0 ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test"));
        }
    }

    [Fact]
    public async Task Recording_wrapper_persists_evidence_before_returning_a_decision()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var inner = new FakeFirewall(new EgressDecision(true, "sanitized", [new(0, 3, "EMAIL")], SecurityPolicyAction.Mask, "private reason"));
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);
        var result = await firewall.EvaluateAsync("private content", context);
        Assert.True(result.Allowed);
        Assert.Single(sink.Events);
        Assert.Equal(EgressDestination.Output, sink.Events[0].Destination);
        Assert.Equal(1, sink.Events[0].Categories["EMAIL"]);
        Assert.DoesNotContain("private content", JsonSerializer.Serialize(sink.Events[0]));
    }

    [Fact]
    public async Task Recorded_policy_action_distinguishes_clean_and_masked_content()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);

        await firewall.EvaluateAsync("hello", context);
        await firewall.EvaluateAsync("person@example.com", context);

        Assert.Equal(SecurityPolicyAction.Allow, sink.Events[0].Action);
        Assert.Empty(sink.Events[0].Categories);
        Assert.Equal(SecurityPolicyAction.Mask, sink.Events[1].Action);
        Assert.Equal(1, sink.Events[1].Categories["EMAIL"]);
    }

    [Fact]
    public async Task Recorded_evidence_retains_categories_of_overlapping_detections()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var sink = new Sink();

        var decision = await new EvidenceRecordingPrivacyFirewall(inner, sink).EvaluateAsync("Full name: person@example.com", context);

        Assert.True(decision.Allowed);
        Assert.DoesNotContain("person@example.com", decision.SanitizedContent);
        var evidence = Assert.Single(sink.Events);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.Equal(1, evidence.Categories["PERSONAL_DATA"]);
    }

    [Fact]
    public async Task Recording_failure_cannot_return_an_allowed_decision()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new FakeFirewall(new EgressDecision(true, "data", [], SecurityPolicyAction.Allow, "ok"));
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, new Sink(fail: true));
        var error = await Assert.ThrowsAsync<PrivacyBlockedException>(async () => await firewall.EvaluateAsync("data", context));
        Assert.DoesNotContain("secret sink error", error.ToString());
    }

    [Fact]
    public async Task Unsafe_unmasked_decision_is_recorded_as_blocked()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new FakeFirewall(new EgressDecision(true, "secret", [new(0, 6, "SECRET")], SecurityPolicyAction.Allow, "test"));
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);

        var decision = await firewall.EvaluateAsync("secret", context);

        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal(1, evidence.Categories["SECRET"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contradictory_allowed_block_action_fails_closed_and_is_recorded()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new FakeFirewall(new EgressDecision(true, "safe", [], SecurityPolicyAction.Block, "test"));
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);

        var decision = await firewall.EvaluateAsync("safe", context);

        Assert.False(decision.Allowed);
        Assert.False(Assert.Single(sink.Events).Allowed);
        Assert.Equal(SecurityPolicyAction.Block, sink.Events[0].Action);
    }

    [Fact]
    public async Task Masked_entities_with_allow_action_are_recorded_as_blocked()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new FakeFirewall(new EgressDecision(true, "[MASKED]", [new(0, 6, "SECRET")], SecurityPolicyAction.Allow, "test"));
        var sink = new Sink();
        var decision = await new EvidenceRecordingPrivacyFirewall(inner, sink).EvaluateAsync("secret", context);
        Assert.False(decision.Allowed);
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal(1, evidence.Categories["SECRET"]);
    }

    [Fact]
    public async Task Structured_inspection_rejects_allowed_block_action()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new EchoBlockFirewall();
        using var json = JsonDocument.Parse("\"safe\"");
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(inner, json.RootElement, context));
        var sink = new Sink();
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => PrivacyJsonInspection.DemandUnchangedAsync(
            new EvidenceRecordingPrivacyFirewall(inner, sink), json.RootElement, context));
        Assert.False(Assert.Single(sink.Events).Allowed);
    }

    private sealed class EchoBlockFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Block, "test"));
    }

    [Fact]
    public void Explicit_registration_requires_a_sink_and_wraps_the_supplied_firewall()
    {
        var services = new ServiceCollection();
        services.AddRecordedPrivacyFirewall(_ => new FakeFirewall(new EgressDecision(true, "ok", [], SecurityPolicyAction.Allow, "ok")));
        var factory = services.Single(item => item.ServiceType == typeof(IPrivacyFirewall)).ImplementationFactory!;
        Assert.Throws<InvalidOperationException>(() => factory(new SinkProvider(null)));
        Assert.IsType<EvidenceRecordingPrivacyFirewall>(factory(new SinkProvider(new Sink())));
    }

    private sealed class SinkProvider(IHostPrivacyEvidenceSink? sink) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IHostPrivacyEvidenceSink) ? sink : null;
    }

    [Fact]
    public async Task Decision_waits_for_durable_sink_and_honors_cancellation()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var inner = new FakeFirewall(new EgressDecision(true, "data", [], SecurityPolicyAction.Allow, "ok"));
        var sink = new DeferredSink();
        var firewall = new EvidenceRecordingPrivacyFirewall(inner, sink);
        using var cancellation = new CancellationTokenSource();
        var pending = firewall.EvaluateAsync("data", context, cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(sink.Events);
    }

    private sealed class DeferredSink : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Events { get; } = [];
        public async ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    [Fact]
    public async Task Detector_failure_is_recorded_as_a_block()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var sink = new Sink();
        var firewall = new EvidenceRecordingPrivacyFirewall(new FakeFirewall(null), sink);
        var result = await firewall.EvaluateAsync("data", context);
        Assert.False(result.Allowed);
        Assert.False(Assert.Single(sink.Events).Allowed);
    }

    private sealed class FakeFirewall(EgressDecision? decision) : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            decision is null ? throw new InvalidOperationException("secret detector error") : ValueTask.FromResult(decision);
    }

    private sealed class DisabledFirewall : IPrivacyFirewall
    {
        public bool Enabled => false;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disabled firewall must not inspect content.");
    }

    private sealed class SelfDisablingFirewall : IPrivacyFirewall
    {
        public bool Enabled { get; private set; } = true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Enabled = false;
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
        }
    }

    private sealed class Sink(bool fail = false) : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Events { get; } = [];
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException("secret sink error");
            Events.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Rejects_missing_identity_or_oversized_evidence()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var decision = new EgressDecision(false, null, Enumerable.Repeat(new EntitySpan(0, 1, "EMAIL"), 20001).ToArray(), SecurityPolicyAction.Block, "reason");
        Assert.Throws<ArgumentException>(() => HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision));
        Assert.Throws<ArgumentException>(() => HostPrivacyEvidence.Create(Guid.Empty, context, decision with { DetectedEntities = [] }));
    }
}
