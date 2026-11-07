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
