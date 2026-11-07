using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class HostStartupValidationTests
{
    [Fact]
    public async Task ValidOptions_StartsSuccessfully()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOpenConnect(options => options.Agents.MaxDelegationDepth = 2);

        using var host = builder.Build();
        await host.StartAsync();

        var options = host.Services.GetRequiredService<IOptions<OpenConnectOptions>>().Value;
        Assert.Equal(2, options.Agents.MaxDelegationDepth);
        Assert.True(options.Privacy.Enabled);
    }

    [Fact]
    public async Task InvalidAgentsOptions_FailFast()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOpenConnect(options => options.Agents.MaxDelegationDepth = 99);

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Agents:MaxDelegationDepth must be between 0 and 2.", exception.Message);
    }

    [Fact]
    public async Task InvalidPrivacyCombo_FailsFast()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOpenConnect(options =>
        {
            options.Privacy.Enabled = true;
            options.Privacy.Mode = FirewallMode.Disabled;
        });

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Privacy:Mode cannot be disabled while Privacy:Enabled is true.", exception.Message);
    }
}
