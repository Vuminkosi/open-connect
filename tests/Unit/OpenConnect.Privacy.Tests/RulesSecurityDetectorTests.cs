using System.Text.RegularExpressions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class RulesSecurityDetectorTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);

    [Theory]
    [InlineData("Client connected from 203.0.113.42", "203.0.113.42")]
    [InlineData("Client connected from 2001:db8:85a3::8a2e:370:7334", "2001:db8:85a3::8a2e:370:7334")]
    [InlineData("Client connected from ::ffff:203.0.113.42", "::ffff:203.0.113.42")]
    [InlineData("Device 02:11:22:33:44:55", "02:11:22:33:44:55")]
    [InlineData("Device 02-11-22-33-44-55", "02-11-22-33-44-55")]
    [InlineData("Reference 123-45-6789", "123-45-6789")]
    [InlineData("\ud83d\ude00 203.0.113.42", "203.0.113.42")]
    public async Task Masks_unlabeled_network_addresses_and_social_security_numbers(string input, string sensitive)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain(sensitive, result.SanitizedContent, StringComparison.Ordinal);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "PERSONAL_DATA");
    }

    [Theory]
    [InlineData("Address 999.999.999.999")]
    [InlineData("Time 12:30:45")]
    [InlineData("Device 02:11:22:33:44:gg")]
    [InlineData("Reference 000-45-6789")]
    [InlineData("Reference 123-00-6789")]
    [InlineData("Reference 123-45-0000")]
    public async Task Does_not_classify_invalid_unlabeled_identifiers(string input)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.Equal(input, result.SanitizedContent);
        Assert.Empty(result.DetectedEntities);
    }

    [Theory]
    [InlineData("Contact al\u200bice@example.com", "al\u200bice@example.com", "EMAIL")]
    [InlineData("Call +27\u2060821234567", "+27\u2060821234567", "PHONE")]
    [InlineData("Reference 123-45-\u200d6789", "123-45-\u200d6789", "PERSONAL_DATA")]
    [InlineData("Host 203.0.\u200b113.42", "203.0.\u200b113.42", "PERSONAL_DATA")]
    [InlineData("Device 02:11:22:\u200c33:44:55", "02:11:22:\u200c33:44:55", "PERSONAL_DATA")]
    public async Task Masks_identifiers_with_invisible_separators_using_original_offsets(string input, string sensitive, string category)
    {
        var content = "\ud83d\ude00 " + input;
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(content, Context);
        Assert.DoesNotContain(sensitive, result.SanitizedContent, StringComparison.Ordinal);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == category && entity.Start >= 2 && entity.Length > 0);
    }

    [Fact]
    public async Task Excessive_invisible_separator_input_fails_closed()
    {
        var detector = new RulesSecurityDetector();
        var content = new string('x', 1_000_001) + "\u200b";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await detector.DetectAndSanitizeAsync(content, Context));
    }

    [Theory]
    [InlineData("Full name: Jordan Example", "Jordan Example")]
    [InlineData("{\"date_of_birth\":\"1990-02-03\"}", "1990-02-03")]
    [InlineData("national id: ABC-12345", "ABC-12345")]
    [InlineData("driver's license number: ZX-1234", "ZX-1234")]
    [InlineData("{\"tax_id\":\"T-1234\"}", "T-1234")]
    [InlineData("bank account number: 12345678", "12345678")]
    [InlineData("medical record number: MR-10", "MR-10")]
    [InlineData("given-name: Jordan", "Jordan")]
    [InlineData("family-name: Example", "Example")]
    [InlineData("mobile number: 76123456", "76123456")]
    [InlineData("ip address: 203.0.113.42", "203.0.113.42")]
    [InlineData("device identifier: device-7a42", "device-7a42")]
    [InlineData("IMEI: 123456789012345", "123456789012345")]
    [InlineData("latitude: -26.3054", "-26.3054")]
    [InlineData("longitude: 31.1367", "31.1367")]
    [InlineData("email address: jordan at example dot org", "jordan at example dot org")]
    [InlineData("date-of-birth: 1990-02-03", "1990-02-03")]
    [InlineData("{\"full_name\":\"Example, Jordan\"}", "Example, Jordan")]
    [InlineData("{\"home_address\":\"Unit 3, 123 Main Street\"}", "Unit 3, 123 Main Street")]
    [InlineData("{\"first_name\":\"A\\\"B\"}", "A\\\"B")]
    [InlineData("first name: Q", "Q")]
    [InlineData("home address: Unit 3, Main Street; Second Floor", "Unit 3, Main Street; Second Floor")]
    [InlineData("full name: \"Example, Jordan", "Example, Jordan")]
    [InlineData("full name: \"Example, Jordan\\", "Example, Jordan")]
    public async Task MasksLabeledPersonalData(string input, string value)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain(value, result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "PERSONAL_DATA");
    }

    [Theory]
    [InlineData("Contact: josé@example.org", "josé@example.org")]
    [InlineData("Contact: jose\u0301@example.org", "jose\u0301@example.org")]
    [InlineData("Contact: user@例子.测试", "user@例子.测试")]
    [InlineData("Contact: δοκιμή@παράδειγμα.δοκιμή", "δοκιμή@παράδειγμα.δοκιμή")]
    public async Task MasksInternationalizedEmailAddresses(string input, string address)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain(address, result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "EMAIL");
    }

    [Theory]
    [InlineData("phone: [OC_PHONE]")]
    [InlineData("{\"phone\":\"[OC_0123456789ABCDEF0123456789ABCDEF]\"}")]
    public async Task ExactPrivacyTokensAreNotReclassifiedAsPersonalData(string input)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.Equal(input, result.SanitizedContent);
        Assert.Empty(result.DetectedEntities);
    }

    [Fact]
    public async Task TextAdjacentToPrivacyTokenRemainsProtected()
    {
        const string input = "phone: [OC_PHONE]76123456";
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain("76123456", result.SanitizedContent);
    }

    [Theory]
    [InlineData("password: unusual-value!", "unusual-value!")]
    [InlineData("{\"api_key\":\"part1,part2\"}", "part1,part2")]
    [InlineData("{\"password\":\"first\\\"second\"}", "first\\\"second")]
    [InlineData("refresh-token=opaque-value", "opaque-value")]
    [InlineData("client secret: customCredential", "customCredential")]
    [InlineData("Authorization: Basic c2Vuc2l0aXZl", "Basic c2Vuc2l0aXZl")]
    [InlineData("Authorization: Digest username=\"alice\", response=\"private\"", "Digest username=\"alice\", response=\"private\"")]
    [InlineData("AWS_SECRET_ACCESS_KEY=private-key-material", "private-key-material")]
    [InlineData("{\"aws_access_key_id\":\"AKIAEXAMPLE\"}", "AKIAEXAMPLE")]
    [InlineData("password: part1,part2;part3", "part1,part2;part3")]
    [InlineData("password: \"private-credential", "private-credential")]
    [InlineData("password: \"private-credential\\", "private-credential")]
    public async Task MasksLabeledSecrets(string input, string value)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain(value, result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "SECRET");
    }

    [Fact]
    public async Task UnterminatedQuotedSecretStopsAtTheLineBoundary()
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync("password: \"private\r\nnext line", Context);
        Assert.DoesNotContain("private", result.SanitizedContent);
        Assert.EndsWith("\r\nnext line", result.SanitizedContent);
    }

    [Theory]
    [InlineData("before -----BEGIN PRIVATE KEY-----\nPRIVATE-MATERIAL\n-----END PRIVATE KEY----- after", "after")]
    [InlineData("before -----BEGIN RSA PRIVATE KEY-----\nPRIVATE-MATERIAL", null)]
    public async Task MasksEntirePrivateKeyBlockIncludingIncompleteBlocks(string input, string? suffix)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(input, Context);
        Assert.DoesNotContain("PRIVATE-MATERIAL", result.SanitizedContent);
        Assert.DoesNotContain("BEGIN", result.SanitizedContent);
        if (suffix is not null)
            Assert.EndsWith(suffix, result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "SECRET");
    }

    [Fact]
    public async Task Custom_rule_timeout_fails_closed_in_the_policy_engine()
    {
        var detector = new RulesSecurityDetector(customRules:
            [new CustomDetectorRule("CUSTOM", new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1)))]);
        var policy = new PolicyEngine(detector);
        var result = await policy.EvaluateAsync(new string('a', 5000) + "!", Context, FirewallMode.StrictLocal, true);
        Assert.False(result.Allowed);
        Assert.Null(result.SanitizedContent);
        Assert.Equal("detector_failure", result.Reason);
    }

    [Fact]
    public async Task Custom_rule_without_timeout_is_bounded_and_fails_closed()
    {
        var detector = new RulesSecurityDetector(customRules: [new CustomDetectorRule("CUSTOM", new Regex("(a+)+$"))]);
        var policy = new PolicyEngine(detector);
        var result = await policy.EvaluateAsync(new string('a', 5000) + "!", Context, FirewallMode.StrictLocal, true);
        Assert.False(result.Allowed);
        Assert.Equal("detector_failure", result.Reason);
    }

    [Fact]
    public async Task MasksEntireUnionOfOverlappingMatches()
    {
        var detector = new RulesSecurityDetector(customRules: new[]
        {
            new CustomDetectorRule("A", new Regex("ABCDE")),
            new CustomDetectorRule("B", new Regex("DEFGH"))
        });
        var result = await detector.DetectAndSanitizeAsync("ABCDEFGH", Context);
        Assert.Equal("[OC_A]", result.SanitizedContent);
        Assert.Equal(new[] { "A", "B" }, result.DetectedEntities.Select(entity => entity.EntityType));
        Assert.All(result.DetectedEntities, entity => { Assert.Equal(0, entity.Start); Assert.Equal(8, entity.Length); });
    }

    [Fact]
    public async Task Overlapping_labeled_data_retains_all_evidence_categories()
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync("Full name: person@example.com", Context);
        Assert.DoesNotContain("person@example.com", result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "EMAIL");
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "PERSONAL_DATA");
    }

    [Fact]
    public async Task Many_unicode_matches_keep_scalar_offsets()
    {
        var detector = new RulesSecurityDetector(customRules: [new CustomDetectorRule("MARK", new Regex("X"))]);
        var input = string.Concat(Enumerable.Repeat("😀X", 1000));
        var result = await detector.DetectAndSanitizeAsync(input, Context);

        Assert.Equal(1000, result.DetectedEntities.Count);
        for (var i = 0; i < result.DetectedEntities.Count; i++)
        {
            Assert.Equal(i * 2 + 1, result.DetectedEntities[i].Start);
            Assert.Equal(1, result.DetectedEntities[i].Length);
        }
    }

    [Fact]
    public async Task Excessive_matches_fail_closed_before_allocation_grows_unbounded()
    {
        var detector = new RulesSecurityDetector(customRules: [new CustomDetectorRule("MARK", new Regex("X"))]);
        var input = new string('X', 20001);
        var decision = await new PolicyEngine(detector).EvaluateAsync(input, Context, FirewallMode.StrictLocal, true);

        Assert.False(decision.Allowed);
        Assert.Null(decision.SanitizedContent);
        Assert.Equal("detector_failure", decision.Reason);
    }

    [Fact]
    public async Task MasksEntireLongLabeledValue()
    {
        var value = new string('A', 110) + "PRIVATE_SUFFIX";
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync($"Full name: {value}", Context);
        Assert.DoesNotContain("PRIVATE_SUFFIX", result.SanitizedContent);
    }

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

    [Theory]
    [InlineData("+268 7612 3456")]
    [InlineData("+27 82 123 4567")]
    [InlineData("+447700900123")]
    public async Task MasksInternationalPhoneNumbers(string phone)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync($"Call {phone}", Context);
        Assert.DoesNotContain(phone, result.SanitizedContent);
        Assert.Contains(result.DetectedEntities, entity => entity.EntityType == "PHONE");
    }

    [Theory]
    [InlineData("+1234567")]
    public async Task DoesNotClassifyOutOfRangeInternationalNumbers(string value)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(value, Context);
        Assert.DoesNotContain(result.DetectedEntities, entity => entity.EntityType == "PHONE");
    }

    [Theory]
    [InlineData("+1234567890123456")]
    [InlineData("+1 234 567 890 123 456")]
    public async Task MasksTheWholeLongInternationalNumberRatherThanLeavingASuffix(string value)
    {
        var result = await new RulesSecurityDetector().DetectAndSanitizeAsync(value, Context);
        Assert.Equal("[OC_PHONE]", result.SanitizedContent);
    }

    [Fact]
    public async Task InternationalPhoneTokenIncludesCountryPrefix()
    {
        const string phone = "+1-555-867-5309";
        using var vault = new TokenVault(new byte[32]);
        var detector = new RulesSecurityDetector(vault);

        var result = await detector.DetectAndSanitizeAsync($"Call {phone}", Context);
        var token = Regex.Match(result.SanitizedContent, @"\[OC_[A-F0-9]{32}\]").Value;

        Assert.NotEmpty(token);
        Assert.DoesNotContain("+1-", result.SanitizedContent, StringComparison.Ordinal);
        Assert.Equal(phone, await vault.ResolveAsync(token,
            new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "redaction")));
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
