// <copyright file="ModelCapabilitySelectionTests.cs">
// Tests for ModelRouteSelector (OC2-D-CONFIG-014).
// Explicit model capability selection: tools, streaming, structured output, vision, locality, and fallback.
// Enforces fail-closed exact match, declared-fallback-only, and capability validation.
// </copyright>

using OpenConnect.Abstractions;
using OpenConnect.Agents.Providers;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ModelCapabilitySelectionTests
{
    private static readonly ProviderDescriptor LocalStandard = new(
        ProviderId: "local-standard",
        SupportsChat: true,
        SupportsStreaming: true,
        SupportsTools: true,
        SupportsStructuredOutput: true,
        SupportsVision: false,
        Locality: ModelExecutionLocation.LocalOnly);

    private static readonly ProviderDescriptor LocalVision = new(
        ProviderId: "local-vision",
        SupportsChat: true,
        SupportsStreaming: true,
        SupportsTools: true,
        SupportsStructuredOutput: true,
        SupportsVision: true,
        Locality: ModelExecutionLocation.LocalOnly);

    private static readonly ProviderDescriptor RemoteAdvanced = new(
        ProviderId: "remote-advanced",
        SupportsChat: true,
        SupportsStreaming: true,
        SupportsTools: true,
        SupportsStructuredOutput: true,
        SupportsVision: true,
        Locality: ModelExecutionLocation.RemoteAllowed);

    private static readonly ProviderDescriptor LocalChatOnly = new(
        ProviderId: "local-chat-only",
        SupportsChat: true,
        SupportsStreaming: false,
        SupportsTools: false,
        SupportsStructuredOutput: false,
        SupportsVision: false,
        Locality: ModelExecutionLocation.LocalOnly);

    [Fact]
    public void ExactMatch_WithSupportedCapabilities_Succeeds()
    {
        var requirement = new ModelRequirement(
            ModelId: "local-standard",
            RequireTools: true,
            RequireStreaming: true);

        var selected = ModelRouteSelector.SelectProvider(requirement, [LocalStandard, LocalVision, RemoteAdvanced]);

        Assert.Equal("local-standard", selected.ProviderId);
    }

    [Fact]
    public void MissingCapability_FailsClosed_NamingRequirement()
    {
        var requirement = new ModelRequirement(
            ModelId: "local-chat-only",
            RequireTools: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelRouteSelector.SelectProvider(requirement, [LocalChatOnly]));

        Assert.Contains("Tools", ex.Message);
        Assert.Contains("local-chat-only", ex.Message);
    }

    [Fact]
    public void VisionRequirement_FailsClosed_WhenUnsupported()
    {
        var requirement = new ModelRequirement(
            ModelId: "local-standard",
            RequireVision: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelRouteSelector.SelectProvider(requirement, [LocalStandard]));

        Assert.Contains("Vision", ex.Message);
        Assert.Contains("local-standard", ex.Message);
    }

    [Fact]
    public void LocalityConstraint_LocalOnly_Rejects_RemoteProvider_FailClosed()
    {
        var requirement = new ModelRequirement(
            ModelId: "remote-advanced",
            Location: ModelExecutionLocation.LocalOnly);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelRouteSelector.SelectProvider(requirement, [RemoteAdvanced]));

        Assert.Contains("LocalOnly", ex.Message);
    }

    [Fact]
    public void DeclaredFallback_Succeeds_WhenPrimaryLacksCapability()
    {
        var requirement = new ModelRequirement(
            ModelId: "local-standard",
            RequireVision: true,
            FallbackModelId: "local-vision");

        var selected = ModelRouteSelector.SelectProvider(requirement, [LocalStandard, LocalVision]);

        Assert.Equal("local-vision", selected.ProviderId);
    }

    [Fact]
    public void DeclaredFallback_Succeeds_WhenPrimaryIsUnregistered()
    {
        var requirement = new ModelRequirement(
            ModelId: "missing-primary",
            RequireTools: true,
            FallbackModelId: "local-standard");

        var selected = ModelRouteSelector.SelectProvider(requirement, [LocalStandard]);

        Assert.Equal("local-standard", selected.ProviderId);
    }

    [Fact]
    public void UndeclaredFallback_FailsClosed_NoArbitrarySubstitution()
    {
        var requirement = new ModelRequirement(
            ModelId: "missing-primary",
            FallbackModelId: null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelRouteSelector.SelectProvider(requirement, [LocalStandard, LocalVision]));

        Assert.Contains("no fallback is declared", ex.Message);
    }

    [Fact]
    public void IncompatibleFallback_FailsClosed_NamingFailure()
    {
        var requirement = new ModelRequirement(
            ModelId: "missing-primary",
            RequireVision: true,
            FallbackModelId: "local-standard"); // local-standard lacks vision

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelRouteSelector.SelectProvider(requirement, [LocalStandard]));

        Assert.Contains("Declared fallback provider 'local-standard' does not meet requirements", ex.Message);
        Assert.Contains("Vision", ex.Message);
    }
}
