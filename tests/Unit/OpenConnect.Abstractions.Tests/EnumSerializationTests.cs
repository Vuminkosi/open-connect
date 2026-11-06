using System.Text.Json;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class EnumSerializationTests
{
    [Fact]
    public void Enums_use_kebab_case() {
        Assert.Equal("\"in-memory-development\"", JsonSerializer.Serialize(TransportMode.InMemoryDevelopment));
        Assert.Equal("\"interactive\"", JsonSerializer.Serialize(AgentExecutionMode.Interactive));
        Assert.Equal("\"gliner-pii\"", JsonSerializer.Serialize(SecurityDetectorKind.GlinerPii));
        Assert.Equal("\"strict-local\"", JsonSerializer.Serialize(FirewallMode.StrictLocal));
        Assert.Equal("\"event-triggered\"", JsonSerializer.Serialize(ActivationMode.EventTriggered));
    }

    [Fact]
    public void Converter_rejects_unknown_string() => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentExecutionMode>("\"future-mode\""));

    [Fact]
    public void Converter_rejects_numeric_value() => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentExecutionMode>("1"));

    [Fact]
    public void Capability_descriptor_serializes_kind_as_kebab_case()
    {
        var descriptor = new CapabilityDescriptor(new CapabilityId("capability-1"), CapabilityKind.Security, DetectorState.Ready);

        var json = JsonSerializer.Serialize(descriptor);

        Assert.Contains("\"Kind\":\"security\"", json);
        Assert.Contains("\"State\":\"ready\"", json);
    }

    [Fact]
    public void Capability_descriptor_rejects_numeric_kind()
    {
        const string json = "{\"Id\":{\"Value\":\"capability-1\"},\"Kind\":1,\"State\":\"ready\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CapabilityDescriptor>(json));
    }

    [Fact]
    public void Capability_descriptor_rejects_unknown_kind()
    {
        const string json = "{\"Id\":{\"Value\":\"capability-1\"},\"Kind\":\"future-kind\",\"State\":\"ready\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CapabilityDescriptor>(json));
    }
}
