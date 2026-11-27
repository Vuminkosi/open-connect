using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public sealed class MessagingCapabilityParserTests
{
    [Theory]
    [InlineData(MessagingCapabilityIds.InAppSend, "imessaging")]
    [InlineData(MessagingCapabilityIds.InAppReceive, "imessaging")]
    [InlineData(MessagingCapabilityIds.InAppCall, "imessaging")]
    [InlineData(MessagingCapabilityIds.E2EE, "imessaging")]
    [InlineData(MessagingCapabilityIds.PushFcm, "imessaging")]
    [InlineData("messaging.push.webpush", "imessaging")]
    [InlineData(MessagingCapabilityIds.PushWeb, "imessaging")]
    [InlineData(MessagingCapabilityIds.DeviceEnrollment, "imessaging")]
    [InlineData(MessagingCapabilityIds.InAppSend, "tool")]
    [InlineData("messaging.push.webpush", "tool")]
    public void Parse_AcceptsVerifiedMessagingCapabilities(string capabilityId, string workerKind)
    {
        using var doc = JsonDocument.Parse($$"""
        {
          "capabilities": [
            {
              "schemaVersion": "openconnect.v1",
              "id": "{{capabilityId}}",
              "kind": "{{workerKind}}",
              "state": "ready",
              "version": "1.0.0",
              "localOnly": true
            }
          ]
        }
        """);

        var parsed = WorkerCapabilityResponseParser.Parse(doc.RootElement, new HashSet<string> { capabilityId }, inference: false);
        var descriptor = Assert.Single(parsed);
        Assert.Equal(capabilityId, descriptor.Id.Value);
        Assert.Equal(CapabilityKind.Tool, descriptor.Kind);
        Assert.Equal(DetectorState.Ready, descriptor.State);
    }

    [Theory]
    [InlineData("messaging.whatsapp.send")]
    [InlineData("messaging.telegram.send")]
    [InlineData("messaging.discord.send")]
    public void Parse_RejectsUnplannedExternalChannelCapabilities(string forbiddenId)
    {
        using var doc = JsonDocument.Parse("""{"capabilities":[]}""");
        Assert.Throws<ArgumentException>(() =>
            WorkerCapabilityResponseParser.Parse(doc.RootElement, new HashSet<string> { forbiddenId }, inference: false));
    }

    [Fact]
    public void Parse_RejectsMismatchedWorkerKindForMessagingCapability()
    {
        using var doc = JsonDocument.Parse("""
        {
          "capabilities": [
            {
              "schemaVersion": "openconnect.v1",
              "id": "messaging.inapp.send",
              "kind": "security",
              "state": "ready",
              "version": "1.0.0",
              "localOnly": true
            }
          ]
        }
        """);

        Assert.Throws<InvalidOperationException>(() =>
            WorkerCapabilityResponseParser.Parse(doc.RootElement, new HashSet<string> { "messaging.inapp.send" }, inference: false));
    }
}
