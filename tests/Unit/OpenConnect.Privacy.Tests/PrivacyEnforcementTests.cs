using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class PrivacyEnforcementTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);

    [Fact]
    public async Task MissingDetectorFailsClosedForRemoteEgress()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine());
        var decision = await new EgressGate(firewall).EvaluateAsync("sensitive", Context);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
    }

    [Fact]
    public async Task DemandAllowedRejectsContradictoryBlockDecision()
    {
        var gate = new EgressGate(new ContradictoryFirewall());
        await Assert.ThrowsAsync<PrivacyBlockedException>(async () => await gate.DemandAllowedAsync("private", Context));
    }

    private sealed class ContradictoryFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Block, "test"));
    }

    [Fact]
    public async Task Accepts_valid_unicode_span_when_sensitive_value_is_removed()
    {
        var detector = new FixtureDetector(new DetectorResult(true, "😀[MASKED]", [new(1, 3, "PERSONAL_DATA")]));
        var decision = await new PolicyEngine(detector).EvaluateAsync("😀bob", Context, FirewallMode.StrictLocal, true);
        Assert.True(decision.Allowed);
        Assert.Equal("😀[MASKED]", decision.SanitizedContent);
        Assert.Equal(SecurityPolicyAction.Mask, decision.Action);
    }

    [Theory]
    [InlineData("clean", SecurityPolicyAction.Allow)]
    [InlineData("[MASKED]", SecurityPolicyAction.Mask)]
    public async Task Action_reflects_whether_content_was_changed(string sanitized, SecurityPolicyAction expected)
    {
        var detector = new FixtureDetector(new DetectorResult(true, sanitized, []));
        var decision = await new PolicyEngine(detector).EvaluateAsync("clean", Context, FirewallMode.StrictLocal, true);
        Assert.True(decision.Allowed);
        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public async Task Blocks_when_span_verification_work_is_excessive()
    {
        var spans = Enumerable.Repeat(new EntitySpan(0, 1, "PERSONAL_DATA"), 10001).ToArray();
        var detector = new FixtureDetector(new DetectorResult(true, new string('x', 10000), spans));
        var decision = await new PolicyEngine(detector).EvaluateAsync("a", Context, FirewallMode.StrictLocal, true);
        Assert.False(decision.Allowed);
        Assert.Equal("detector_failure", decision.Reason);
    }

    [Fact]
    public async Task Detector_cannot_leave_a_reported_value_inside_changed_output()
    {
        var detector = new FixtureDetector(new DetectorResult(true, "prefix private suffix", [new(0, 7, "PERSONAL_DATA")]));
        var decision = await new PolicyEngine(detector).EvaluateAsync("private", Context, FirewallMode.StrictLocal, true);
        Assert.False(decision.Allowed);
        Assert.Equal("detector_failure", decision.Reason);
    }

    [Fact]
    public async Task Detector_cannot_report_sensitive_content_without_changing_it()
    {
        var detector = new FixtureDetector(new DetectorResult(true, "private", [new(0, 7, "PERSONAL_DATA")]));
        var decision = await new PolicyEngine(detector).EvaluateAsync("private", Context, FirewallMode.StrictLocal, true);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        Assert.Equal("detector_failure", decision.Reason);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    public async Task Detector_spans_must_fit_unicode_scalar_positions(int start, int length)
    {
        var detector = new FixtureDetector(new DetectorResult(true, "[MASKED]", [new(start, length, "PERSONAL_DATA")]));
        var decision = await new PolicyEngine(detector).EvaluateAsync("😀x", Context, FirewallMode.StrictLocal, true);
        Assert.False(decision.Allowed);
        Assert.Equal("detector_failure", decision.Reason);
    }

    private sealed class FixtureDetector(DetectorResult result) : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    [Fact]
    public async Task DisabledFirewallAllowsContentExplicitly()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions { Enabled = false, Mode = FirewallMode.Disabled }), new PolicyEngine());
        var decision = await firewall.EvaluateAsync("content", Context);
        Assert.True(decision.Allowed);
        Assert.Equal("content", decision.SanitizedContent);
    }

    [Fact]
    public async Task VaultEncryptsAndEnforcesCompleteScopeAndTtl()
    {
        var clock = new FakeTimeProvider();
        using var vault = new TokenVault(new byte[32], clock);
        var scope = new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "model");
        var token = await vault.StoreAsync("secret", "email", scope, TimeSpan.FromMinutes(1));
        Assert.Equal("secret", await vault.ResolveAsync(token, scope));
        Assert.Null(await vault.ResolveAsync(token, scope with { Tenant = new TenantId(Ulid.NewUlid()) }));
        Assert.Null(await vault.ResolveAsync(token, scope with { Purpose = "nats" }));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await vault.ResolveAsync(token, scope));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
