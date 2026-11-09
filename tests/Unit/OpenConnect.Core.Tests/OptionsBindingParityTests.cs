using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

// YAML fixture coverage is not verified: the configured feed offers 2.0.0-preview2, while the contract pins 8.0.0.
public sealed class OptionsBindingParityTests
{
    [Fact]
    public void Key_values_bind_to_fluent_equivalent_and_validate()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:DefaultExecutionMode", "interactive"),
            ("OpenConnect:Privacy:Mode", "strict-local"),
            ("OpenConnect:Privacy:Rules:Enabled", "true")));
        var fluent = new OpenConnectOptions();
        fluent.Agents.DefaultExecutionMode = AgentExecutionMode.Interactive;
        fluent.Privacy.Mode = FirewallMode.StrictLocal;
        fluent.Privacy.Rules.Enabled = true;

        Assert.Equal(fluent.Agents.DefaultExecutionMode, bound.Agents.DefaultExecutionMode);
        Assert.Equal(fluent.Privacy.Mode, bound.Privacy.Mode);
        Assert.Equal(fluent.Privacy.Rules.Enabled, bound.Privacy.Rules.Enabled);
        Assert.True(new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound).Succeeded);
    }

    [Fact]
    public void Enum_values_accept_exact_member_name_casing()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:DefaultExecutionMode", "Interactive")));

        Assert.Equal(AgentExecutionMode.Interactive, bound.Agents.DefaultExecutionMode);
    }

    [Fact]
    public void Scalar_values_bind_as_booleans_and_numbers()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:RegisterLeadAgent", " TRUE "),
            ("OpenConnect:Agents:MaxDelegationDepth", "4")));

        Assert.True(bound.Agents.RegisterLeadAgent);
        Assert.Equal(4, bound.Agents.MaxDelegationDepth);
    }

    [Fact]
    public void Duration_values_bind_to_fluent_equivalent_and_validate()
    {
        // System.Text.Json has no built-in TimeSpan converter for JSON strings.
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:MaxTurnDuration", "00:02:00")));
        var fluent = new OpenConnectOptions();
        fluent.Agents.MaxTurnDuration = TimeSpan.FromMinutes(2);

        Assert.Equal(fluent.Agents.MaxTurnDuration, bound.Agents.MaxTurnDuration);
        Assert.True(new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound).Succeeded);
    }

    [Fact]
    public void Invalid_duration_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenConnectConfigurationBinder.Bind(
            Kv(("OpenConnect:Agents:MaxTurnDuration", "not-a-duration"))));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("frobnicator")]
    [InlineData("0")]
    public void Invalid_or_numeric_enum_values_are_rejected(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenConnectConfigurationBinder.Bind(
            Kv(("OpenConnect:Agents:DefaultExecutionMode", value))));
        Assert.Contains("OpenConnect", exception.Message);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public void Nested_unknown_keys_are_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Privacy:UnknownSetting", "x"),
            ("OpenConnect:Agents:Nope", "1"))));

        Assert.Contains("OpenConnect", exception.Message);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public void Empty_input_returns_secure_defaults_that_validate()
    {
        var options = OpenConnectConfigurationBinder.Bind(Array.Empty<KeyValuePair<string, string?>>());

        Assert.True(options.Privacy.Enabled);
        Assert.Equal(FirewallMode.StrictLocal, options.Privacy.Mode);
        Assert.True(options.Privacy.Rules.Enabled);
        Assert.True(new OpenConnectOptionsValidator().Validate(Options.DefaultName, options).Succeeded);
    }

    [Fact]
    public void Bound_invalid_depth_fails_validation_actionably()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:MaxDelegationDepth", "99")));

        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("MaxDelegationDepth", StringComparison.Ordinal) &&
            failure.Contains("0 and 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Bound_negative_budget_fails_validation_with_field_name()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:MaxTotalToolCalls", "-1")));

        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("MaxTotalToolCalls", StringComparison.Ordinal));
    }

    [Fact]
    public void Bound_valid_config_matches_fluent_options_and_validates()
    {
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:DefaultExecutionMode", "interactive"),
            ("OpenConnect:Agents:MaxDelegationDepth", "2"),
            ("OpenConnect:Agents:MaxTotalToolCalls", "20"),
            ("OpenConnect:Agents:MaxModelCalls", "12"),
            ("OpenConnect:Agents:MaxTurnDuration", "00:02:00"),
            ("OpenConnect:Agents:MaxToolDuration", "00:00:30"),
            ("OpenConnect:Privacy:Mode", "strict-local"),
            ("OpenConnect:Privacy:Rules:Enabled", "true")));
        var fluent = new OpenConnectOptions();
        fluent.Agents.DefaultExecutionMode = AgentExecutionMode.Interactive;
        fluent.Agents.MaxDelegationDepth = 2;
        fluent.Agents.MaxTotalToolCalls = 20;
        fluent.Agents.MaxModelCalls = 12;
        fluent.Agents.MaxTurnDuration = TimeSpan.FromMinutes(2);
        fluent.Agents.MaxToolDuration = TimeSpan.FromSeconds(30);
        fluent.Privacy.Mode = FirewallMode.StrictLocal;
        fluent.Privacy.Rules.Enabled = true;

        Assert.True(new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound).Succeeded);
        Assert.Equal(fluent.Agents.DefaultExecutionMode, bound.Agents.DefaultExecutionMode);
        Assert.Equal(fluent.Agents.MaxDelegationDepth, bound.Agents.MaxDelegationDepth);
        Assert.Equal(fluent.Agents.MaxTotalToolCalls, bound.Agents.MaxTotalToolCalls);
        Assert.Equal(fluent.Agents.MaxModelCalls, bound.Agents.MaxModelCalls);
        Assert.Equal(fluent.Agents.MaxTurnDuration, bound.Agents.MaxTurnDuration);
        Assert.Equal(fluent.Agents.MaxToolDuration, bound.Agents.MaxToolDuration);
        Assert.Equal(fluent.Privacy.Mode, bound.Privacy.Mode);
        Assert.Equal(fluent.Privacy.Rules.Enabled, bound.Privacy.Rules.Enabled);
    }

    [Fact]
    public void Bound_operational_mode_with_active_user_identity_fails_validation()
    {
        // Core owns this invariant; workload-identity/catalog checks belong to Agents and are out of scope.
        var bound = OpenConnectConfigurationBinder.Bind(Kv(
            ("OpenConnect:Agents:DefaultExecutionMode", "operational"),
            ("OpenConnect:Agents:IdentityMode", "active-user")));

        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, bound);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("Operational agents", StringComparison.Ordinal) &&
            failure.Contains("ActiveUser", StringComparison.Ordinal));
    }

    private static IEnumerable<KeyValuePair<string, string?>> Kv(params (string Key, string? Value)[] entries) =>
        entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value));
}
