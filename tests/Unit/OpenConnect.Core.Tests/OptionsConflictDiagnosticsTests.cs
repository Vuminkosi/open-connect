using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

/// <summary>
/// Conflicting repeat-registration diagnostics (CONFIG-005 remainder, AG-MAP-006 SLICE-C1).
/// Same-policy repeats stay idempotent; differing policy fails fast naming field + old + new.
/// </summary>
public sealed class OptionsConflictDiagnosticsTests
{
    [Fact]
    public void SamePolicyRepeat_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect(o => o.Agents.MaxDelegationDepth = 1);
        services.AddOpenConnect(o => o.Agents.MaxDelegationDepth = 1);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value.Agents.MaxDelegationDepth);
        Assert.Single(provider.GetRequiredService<IAgentDefinitionRegistry>().GetAll());
    }

    [Fact]
    public void NullConfigureRepeat_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect(o => o.Agents.MaxDelegationDepth = 1);
        services.AddOpenConnect();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value.Agents.MaxDelegationDepth);
    }

    [Fact]
    public void DifferingPrivacyMode_FailsNamingFieldAndBothValues()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenConnect(o => o.Privacy.Mode = FirewallMode.LocalOnly));

        Assert.Contains("Privacy:Mode", ex.Message);
        Assert.Contains(FirewallMode.StrictLocal.ToString(), ex.Message);
        Assert.Contains(FirewallMode.LocalOnly.ToString(), ex.Message);
    }

    [Fact]
    public void DifferingMaxDelegationDepth_FailsNamingFieldAndBothValues()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect(o => o.Agents.MaxDelegationDepth = 2);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenConnect(o => o.Agents.MaxDelegationDepth = 1));

        Assert.Contains("Agents:MaxDelegationDepth", ex.Message);
        Assert.Contains("'2'", ex.Message);
        Assert.Contains("'1'", ex.Message);
    }

    [Fact]
    public void DifferingDetectorSet_FailsNamingFieldAndBothValues()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenConnect(o =>
            {
                o.Privacy.Rules.Enabled = false;
                o.Privacy.GlinerPii.Enabled = true;
            }));

        // First compared detector dimension that differs is reported.
        Assert.Contains("Privacy:Rules:Enabled", ex.Message);
        Assert.Contains("'True'", ex.Message);
        Assert.Contains("'False'", ex.Message);
    }
}
