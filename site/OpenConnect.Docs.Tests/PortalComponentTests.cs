using Bunit;
using OpenConnect.Docs.Components;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class PortalComponentTests : BunitContext
{
    [Fact]
    public void MarkdownRenderer_PreservesParagraphImmediatelyBeforeCode()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown,
            "Configure the gateway:\n```csharp\nservices.AddOpenConnect();\n```"));

        var article = cut.Find("article");
        Assert.Equal("Configure the gateway:", article.QuerySelector("p")?.TextContent);
        Assert.Equal("services.AddOpenConnect();", article.QuerySelector("pre code")?.TextContent);
        Assert.Equal("P", article.Children[0].TagName);
        Assert.Equal("PRE", article.Children[1].TagName);
    }

    [Fact]
    public void MarkdownRenderer_EncodesRawHtmlAndRendersHeadings()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, "# Hello\n\n<script>alert(1)</script>\n\n[Safe](/docs/v1/getting-started)"));

        Assert.Contains("<h2 id=\"hello\">Hello</h2>", cut.Markup);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", cut.Markup);
        Assert.Contains("href=\"/docs/v1/getting-started\"", cut.Markup);
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkdownHeadings_ProduceMatchingUniqueLinksAndIgnoreCode()
    {
        const string markdown = "# Guide\n## Start here\n### Details\n## Start here\n## Start here-1\n```text\n## Not a section\n```";
        var headings = OpenConnect.Docs.Services.DocumentationHeadingIds.Extract(markdown);
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, markdown));

        Assert.Equal(new[] { "start-here", "details", "start-here-1", "start-here-1-1" }, headings.Select(h => h.Id));
        Assert.Equal(new[] { 2, 3, 2, 2 }, headings.Select(h => h.Level));
        foreach (var heading in headings) Assert.NotNull(cut.Find($"#{heading.Id}"));
        Assert.DoesNotContain(headings, h => h.Text == "Not a section");
    }

    [Fact]
    public void MarkdownRenderer_EncodesUnsafeCodeBlocks()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, "```csharp\nvar value = \"<tag>\";\n```"));

        Assert.Contains("&lt;tag&gt;", cut.Markup);
        Assert.DoesNotContain("<tag>", cut.Markup);
    }
}

public sealed class TodoStoreTests
{
    [Fact]
    public async Task InMemoryTodoStore_supports_create_update_complete_and_delete()
    {
        var store = new OpenConnect.Samples.Blazor.Models.InMemoryTodoStore();
        var created = await store.AddAsync("Portal regression task");

        Assert.NotNull(await store.GetByIdAsync(created.Id));
        Assert.True(await store.UpdateStatusAsync(created.Id, true));
        Assert.True((await store.GetByIdAsync(created.Id))!.IsCompleted);
        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task InMemoryTodoStore_seeds_reference_tasks_without_external_dependencies()
    {
        var store = new OpenConnect.Samples.Blazor.Models.InMemoryTodoStore();
        var items = await store.GetAllAsync();

        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.Title.Contains("privacy firewall", StringComparison.OrdinalIgnoreCase));
    }
}
