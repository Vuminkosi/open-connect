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

    [Fact]
    public async Task RenderAsync_ForwardsCancellationTokenAndPropagatesCancellation()
    {
        var registry = CreateRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loaderCalled = false;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.RenderAsync(
            "render-test",
            new TemplateVariables(),
            (_, token) =>
            {
                loaderCalled = true;
                Assert.Equal(cancellation.Token, token);
                token.ThrowIfCancellationRequested();
                return Task.FromResult("must not render");
            },
            cancellation.Token));

        Assert.True(loaderCalled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RenderAsync_WithUncancelledTokenStillRenders()
    {
        var registry = CreateRegistry();
        using var cancellation = new CancellationTokenSource();

        var result = await registry.RenderAsync(
            "render-test",
            new TemplateVariables { Locale = "en-GB" },
            (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult("Locale: {{locale}}");
            },
            cancellation.Token);

        Assert.Equal("Locale: en-GB", result.Content);
    }

    private static TemplateRegistry CreateRegistry()
    {
        var registry = new TemplateRegistry();
        registry.Register(new AgentTemplate("render-test", "1", "test", "instructions/test.md", null,
            new HashSet<ToolId>(), null, new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic,
            "standard", AllowedScopes: new HashSet<string>(["read"], StringComparer.Ordinal)));
        return registry;
    }

    private static Task<RenderedTemplateInstructions> Render(TemplateVariables variables, string content)
    {
        return CreateRegistry().RenderAsync("render-test", variables, (_, _) => Task.FromResult(content));
    }
}
