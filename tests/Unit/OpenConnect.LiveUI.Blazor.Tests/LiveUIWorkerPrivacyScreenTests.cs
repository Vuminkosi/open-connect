using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIWorkerPrivacyScreenTests
{
    [Fact]
    public async Task Screens_worker_intent_and_rejects_sensitive_target_metadata()
    {
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector(vault)));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
        var request = new LiveUIInferenceRequest("Search for person@example.org", context.Session.Value.ToString(), "home",
            "openconnect.liveui/v1", [new("oc.runtime.target", ["setValue"]) { Label = "Search tasks" }]);

        var screened = await LiveUIWorkerPrivacyScreen.PrepareAsync(request, firewall, context);
        Assert.DoesNotContain("person@example.org", screened.Intent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Search tasks", Assert.Single(screened.Capabilities).Label);

        var sensitiveLabel = request with
        {
            Capabilities = [new("oc.runtime.target", ["setValue"]) { Label = "person@example.org" }]
        };
        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
            await LiveUIWorkerPrivacyScreen.PrepareAsync(sensitiveLabel, firewall, context));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await LiveUIWorkerPrivacyScreen.PrepareAsync(request with { SessionId = Ulid.NewUlid().ToString() }, firewall, context));
    }

    [Fact]
    public async Task Screens_a_snapshot_when_the_caller_changes_the_original_capabilities()
    {
        var capabilities = new List<LiveUIInferenceCapability> { new("oc.runtime.target", ["activate"]) { Label = "Active" } };
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
        var request = new LiveUIInferenceRequest("Show active tasks", context.Session.Value.ToString(), "home", "openconnect.liveui/v1", capabilities);
        var firewall = new MutatingFirewall(() => capabilities[0] = capabilities[0] with { Label = "person@example.org" });

        var screened = await LiveUIWorkerPrivacyScreen.PrepareAsync(request, firewall, context);

        Assert.Equal("person@example.org", capabilities[0].Label);
        Assert.Equal("Active", Assert.Single(screened.Capabilities).Label);
    }

    private sealed class MutatingFirewall(Action mutate) : IPrivacyFirewall
    {
        private bool _mutated;
        public bool Enabled => true;

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            if (!_mutated) { _mutated = true; mutate(); }
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
        }
    }
}
