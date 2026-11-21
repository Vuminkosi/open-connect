using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class OutgoingContentScannerTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);

    [Theory]
    [InlineData("Contact alice@example.com", "alice@example.com")]
    [InlineData("Full name: Jordan Example", "Jordan Example")]
    [InlineData("Call +1-555-867-5309", "+1-555-867-5309")]
    [InlineData("Connected from 203.0.113.42", "203.0.113.42")]
    [InlineData("Connected from 2001:db8::1234", "2001:db8::1234")]
    [InlineData("Device 02:11:22:33:44:55", "02:11:22:33:44:55")]
    [InlineData("Reference 123-45-6789", "123-45-6789")]
    public void MasksSupportedPersonalDataInOutput(string input, string value)
    {
        var decision = new OutgoingContentScanner().ScanOutgoing(input, Context, failClosed: false);
        Assert.True(decision.Allowed);
        Assert.DoesNotContain(value, decision.SanitizedContent);
        Assert.NotEmpty(decision.DetectedEntities);
    }

    [Theory]
    [InlineData("Connected from 203.0.113.42")]
    [InlineData("Connected from 2001:db8::1234")]
    [InlineData("Device 02:11:22:33:44:55")]
    [InlineData("Reference 123-45-6789")]
    public void BlocksSupportedPersonalDataInOutputByDefault(string input)
    {
        var decision = new OutgoingContentScanner().ScanOutgoing(input, Context);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        Assert.Contains(decision.DetectedEntities, entity => entity.EntityType == "PERSONAL_DATA");
    }

    [Fact]
    public void Detector_failure_blocks_even_when_masking_was_requested()
    {
        var decision = CreateScanner(new FailingDetector()).ScanOutgoing("private response", Context, failClosed: false);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        Assert.Equal("detector_failure", decision.Reason);
    }

    private sealed class FailingDetector : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private detector details");
    }

    [Fact]
    public void Inconsistent_detector_result_does_not_release_original_content()
    {
        var decision = CreateScanner(new InconsistentDetector()).ScanOutgoing("private response", Context, failClosed: false);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
    }

    private sealed class InconsistentDetector : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DetectorResult(true, "[MASKED]", []));
    }

    [Theory]
    [InlineData("secret", 0, 6)]
    [InlineData("prefix secret", 0, 6)]
    [InlineData("[MASKED]", -1, 6)]
    [InlineData("[MASKED]", 0, 20)]
    public void Invalid_detector_masking_never_releases_content(string sanitized, int start, int length)
    {
        var scanner = CreateScanner(new ReportedDetector(sanitized, start, length));
        var decision = scanner.ScanOutgoing("secret", Context, failClosed: false);
        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        Assert.Equal("detector_failure", decision.Reason);
    }

    private sealed class ReportedDetector(string sanitized, int start, int length) : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DetectorResult(true, sanitized, [new(start, length, "SECRET")]));
    }

    private static OutgoingContentScanner CreateScanner(ISecurityDetector detector) =>
        (OutgoingContentScanner)Activator.CreateInstance(
            typeof(OutgoingContentScanner),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [detector],
            culture: null)!;

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
