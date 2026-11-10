using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void DefaultsRegisterLeadAgentAndEnableRulesPrivacy()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value;

        Assert.True(options.Privacy.Enabled);
        Assert.True(options.Privacy.Rules.Enabled);
        Assert.False(options.Privacy.GlinerPii.Enabled);
        Assert.Equal(AgentExecutionMode.Interactive, options.Agents.DefaultExecutionMode);
        Assert.Equal("lead", provider.GetRequiredService<IAgentDefinitionRegistry>().GetAll().Single().Id.Value);
    }

    [Fact]
    public void RegistrationIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect().AddOpenConnect();
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetRequiredService<IAgentDefinitionRegistry>().GetAll());
        var options = provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value;
        Assert.True(options.Privacy.Enabled);
    }

    [Fact]
    public void StrictLocalRejectsDisablingAllPiiDetectors()
    {
        var options = new OpenConnectOptions();
        options.Privacy.Rules.Enabled = false;
        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, options);
        Assert.False(result.Succeeded);
        Assert.Contains("Strict-local privacy requires", string.Join(" ", result.Failures ?? []));
    }

    [Fact]
    public void DetectorValuesAreValidatedActionably()
    {
        var options = new OpenConnectOptions();
        options.Privacy.Rules.MaxConcurrentRequests = 0;
        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, options);
        Assert.Contains("Privacy:Rules:MaxConcurrentRequests", string.Join(" ", result.Failures ?? []));
    }

    [Theory]
    [InlineData(nameof(AgentRegistryOptions.MaxTokens), "Agents:MaxTokens cannot be negative.")]
    [InlineData(nameof(AgentRegistryOptions.MaxQueuedAgents), "Agents:MaxQueuedAgents cannot be negative.")]
    [InlineData(nameof(AgentRegistryOptions.MaxCostMicrocents), "Agents:MaxCostMicrocents cannot be negative.")]
    public void NegativeCapValuesAreRejected(string field, string expectedMessage)
    {
        var options = new OpenConnectOptions();
        switch (field)
        {
            case nameof(AgentRegistryOptions.MaxTokens):
                options.Agents.MaxTokens = -1;
                break;
            case nameof(AgentRegistryOptions.MaxQueuedAgents):
                options.Agents.MaxQueuedAgents = -1;
                break;
            default:
                options.Agents.MaxCostMicrocents = -1;
                break;
        }

        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedMessage, string.Join(" ", result.Failures ?? []));
    }
}
