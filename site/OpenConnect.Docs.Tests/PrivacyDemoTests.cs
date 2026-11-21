using Bunit;
using OpenConnect.Docs.Components.PrivacyDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class PrivacyDemoTests : BunitContext
{
    [Theory]
    [InlineData("email", "alex@example.test", "EMAIL")]
    [InlineData("phone", "+1 202-555-0123", "PHONE")]
    [InlineData("card", "4111 1111 1111 1111", "PAYMENT_CARD")]
    [InlineData("secret", "example-value", "SECRET")]
    public async Task SyntheticSensitiveExamplesAreSanitized(string example, string sensitiveValue, string category)
    {
        var demo = new PrivacyDemoService();
        var result = await demo.EvaluateAsync(example);
        Assert.True(result.Decision.Allowed);
        Assert.Contains(sensitiveValue, result.Input);
        Assert.DoesNotContain(sensitiveValue, result.Decision.SanitizedContent);
        Assert.Contains(result.Decision.DetectedEntities, entity => entity.EntityType == category);
    }

    [Fact]
    public async Task UnknownExamplesAreRejected()
    {
        var demo = new PrivacyDemoService();
        await Assert.ThrowsAsync<ArgumentException>(async () => await demo.EvaluateAsync("untrusted input"));
    }

    [Fact]
    public void VisitorCanRunBothExamples()
    {
        var component = Render<PrivacyDemo>();
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("Firewall output:", component.Markup));
        Assert.DoesNotContain("alex@example.test", component.Find("[role=status] p:nth-child(2)").TextContent);
        component.Find("select").Change("ordinary");
        Assert.DoesNotContain("Firewall output:", component.Markup);
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("Detected categories: none", component.Markup));
    }
}
