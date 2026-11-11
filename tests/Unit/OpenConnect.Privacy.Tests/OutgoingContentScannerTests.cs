using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class OutgoingContentScannerTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);

    [Fact]
    public void CleanOutputPassesInspection()
    {
        var scanner = new OutgoingContentScanner();
        var decision = scanner.ScanOutgoing("Here is the requested forecast for tomorrow: Sunny and 22C.", Context);

        Assert.True(decision.Allowed);
        Assert.Equal("clean", decision.Reason);
        Assert.Empty(decision.DetectedEntities);
    }

    [Fact]
    public void DetectsLiteralSecretAndBlocksWhenFailClosed()
    {
        var scanner = new OutgoingContentScanner();
        var content = "Your generated token is ghp_123456789012345678901234567890123456";
        var decision = scanner.ScanOutgoing(content, Context, failClosed: true);

        Assert.False(decision.Allowed);
        Assert.Equal("literal_sensitive_value_detected", decision.Reason);
        Assert.Contains(decision.DetectedEntities, e => e.EntityType == "SECRET");
    }

    [Fact]
    public void DetectsLiteralPaymentCardAndMasksWhenNotFailClosed()
    {
        var scanner = new OutgoingContentScanner();
        // Valid Luhn 16-digit card: 4532015112830366
        var content = "Charging card 4532015112830366 for the order.";
        var decision = scanner.ScanOutgoing(content, Context, failClosed: false);

        Assert.True(decision.Allowed);
        Assert.Equal("outgoing_content_sanitized", decision.Reason);
        Assert.DoesNotContain("4532015112830366", decision.SanitizedContent);
        Assert.Contains("[OC_PAYMENT_CARD]", decision.SanitizedContent);
    }
}
