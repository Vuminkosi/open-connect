using System.Text.RegularExpressions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class RulesSecurityDetectorTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);

    [Fact]
    public async Task DetectsEmailPhoneAndValidCard()
    {
        using var vault = new TokenVault(new byte[32]);
        var detector = new RulesSecurityDetector(vault);

        // Valid 16-digit card using Luhn algorithm: 4532015112830366
        var text = "Contact alice@example.com at +1-555-867-5309 with card 4532015112830366 and secret bearer abcdefghijklmnopqrstuvwxyz123456";
        var result = await detector.DetectAndSanitizeAsync(text, Context);

        Assert.True(result.Available);
        Assert.DoesNotContain("alice@example.com", result.SanitizedContent);
        Assert.DoesNotContain("4532015112830366", result.SanitizedContent);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", result.SanitizedContent);
        Assert.Contains("[OC_", result.SanitizedContent);

        Assert.Equal(4, result.DetectedEntities.Count);
        Assert.Contains(result.DetectedEntities, e => e.EntityType == "EMAIL");
        Assert.Contains(result.DetectedEntities, e => e.EntityType == "PHONE");
        Assert.Contains(result.DetectedEntities, e => e.EntityType == "PAYMENT_CARD");
        Assert.Contains(result.DetectedEntities, e => e.EntityType == "SECRET");
    }

    [Fact]
    public async Task RejectsInvalidLuhnCardFromCardClassification()
    {
        var detector = new RulesSecurityDetector();
        // 4532015112830367 fails Luhn check
        var text = "Invalid card 4532015112830367 should not be classified as card";
        var result = await detector.DetectAndSanitizeAsync(text, Context);

        Assert.DoesNotContain(result.DetectedEntities, e => e.EntityType == "PAYMENT_CARD");
        Assert.Equal(text, result.SanitizedContent);
    }

    [Fact]
    public async Task SupportsCustomRulesAndUnicodeOffsets()
    {
        var custom = new List<CustomDetectorRule>
        {
            new("PROJECT_ID", new Regex(@"PRJ-\d{4}", RegexOptions.Compiled))
        };
        var detector = new RulesSecurityDetector(customRules: custom);

        // Unicode with emoji: \U0001F680 is 2 UTF-16 code units but 1 Unicode scalar
        var text = "\U0001F680 Deploy PRJ-1234 now!";
        var result = await detector.DetectAndSanitizeAsync(text, Context);

        Assert.Contains(result.DetectedEntities, e => e.EntityType == "PROJECT_ID");
        var match = result.DetectedEntities.First(e => e.EntityType == "PROJECT_ID");
        // Emoji is 1 scalar, space is 1, 'Deploy ' is 7 -> 'PRJ-1234' starts at 9
        Assert.Equal(9, match.Start);
        Assert.Equal(8, match.Length);
    }
}
