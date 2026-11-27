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
        Assert.Equal("services.AddOpenConnect();", article.QuerySelector("pre code")?.TextContent.TrimEnd('\r', '\n'));
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
        foreach (var heading in headings)
            Assert.NotNull(cut.Find($"#{heading.Id}"));
        Assert.DoesNotContain(headings, h => h.Text == "Not a section");
    }

    [Fact]
    public void MarkdownRenderer_EncodesUnsafeCodeBlocks()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, "```csharp\nvar value = \"<tag>\";\n```"));

        Assert.Contains("&lt;tag&gt;", cut.Markup);
        Assert.DoesNotContain("<tag>", cut.Markup);
    }

    [Fact]
    public void MarkdownRenderer_RendersTablesListsAndInlineFormatting()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown,
            "## Use `client`\n\n| Feature | API |\n| --- | --- |\n| **Voice** | `Speak` |\n\n- One\n- Two"));

        Assert.Equal("Use client", cut.Find("h3").TextContent);
        Assert.NotNull(cut.Find("h3 code"));
        Assert.Equal("Voice", cut.Find("table tbody strong").TextContent);
        Assert.Equal("Speak", cut.Find("table tbody code").TextContent);
        Assert.Equal(2, cut.FindAll("ul li").Count);
        Assert.Equal("use-client", Assert.Single(OpenConnect.Docs.Services.DocumentationHeadingIds.Extract(
            "## Use `client`")).Id);
    }

    [Fact]
    public void MarkdownRenderer_DoesNotEmitUnsafeLinksOrRemoteImages()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown,
            "[bad](javascript:alert(1)) [encoded](jav&#x61;script:alert(1)) [data](data:text/html,evil) [relative](//evil.example) [safe](/docs) ![image](https://evil.example/pixel.png)\n\n<script>alert(1)</script>"));

        Assert.Empty(cut.FindAll("img"));
        Assert.DoesNotContain("javascript:", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"//evil.example", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/docs\"", cut.Markup);
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
