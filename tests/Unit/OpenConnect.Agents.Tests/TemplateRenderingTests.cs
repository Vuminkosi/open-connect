using OpenConnect.Agents.Templates;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class TemplateRenderingTests
{
    [Fact]
    public async Task RenderAsync_HtmlEncodesApprovedValues()
    {
        var result = await Render(new TemplateVariables { ApplicationName = "<script>alert('x')</script>" },
            "{{applicationName}}");

        Assert.Equal("&lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt;", result.Content);
    }

    [Fact]
    public async Task RenderAsync_RedactsSecretLikeSubstringsBeforeReturningOutput()
    {
        const string rawSecret = "api_key=super-secret-value";
        var result = await Render(new TemplateVariables { TaskGoal = $"Use this {rawSecret} now" }, "{{taskGoal}}");

        Assert.Contains("[REDACTED]", result.Content);
        Assert.DoesNotContain(rawSecret, result.Content);
    }

    [Fact]
    public async Task RenderAsync_RejectsUnsupportedPlaceholderWithDistinctError()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Render(new TemplateVariables(), "before {{unsupportedValue}} after"));

        Assert.Contains("unsupported placeholder 'unsupportedValue'", exception.Message);
    }

    [Fact]
    public async Task RenderAsync_ProducesDeterministicRevisionForSameInputs()
    {
        var variables = new TemplateVariables { Locale = "en-GB" };
        var first = await Render(variables, "Locale: {{locale}}");
        var second = await Render(variables, "Locale: {{locale}}");

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Matches("^[0-9a-f]{64}$", first.Revision);
    }

    private static Task<RenderedTemplateInstructions> Render(TemplateVariables variables, string content)
    {
        var registry = new TemplateRegistry();
        registry.Register(new AgentTemplate("render-test", "1", "test", "instructions/test.md", null,
            new HashSet<ToolId>(), null, new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic,
            "standard", AllowedScopes: new HashSet<string>(["read"], StringComparer.Ordinal)));

        return registry.RenderAsync("render-test", variables, (_, _) => Task.FromResult(content));
    }
}
