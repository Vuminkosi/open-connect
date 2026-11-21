using Bunit;
using OpenConnect.Docs.Components.PrivacyDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class PrivacyDemoTests : BunitContext
{
    [Fact]
    public async Task SyntheticEmailIsSanitizedAndUnknownExamplesAreRejected()
    {
        var demo = new PrivacyDemoService();
        var result = await demo.EvaluateAsync("email");
        Assert.True(result.Decision.Allowed);
        Assert.Contains("alex@example.test", result.Input);
        Assert.DoesNotContain("alex@example.test", result.Decision.SanitizedContent);
        Assert.NotEmpty(result.Decision.DetectedEntities);
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
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("Detected categories: none", component.Markup));
    }
}
