using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OpenConnect.Docs.Services;

/// <summary>Parses public documentation with shared section IDs and restricted links.</summary>
internal static class DocumentationMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .DisableHtml()
        .Build();

    public static string Render(string markdown) => Markdig.Markdown.ToHtml(Parse(markdown).Document, Pipeline);

    public static IReadOnlyList<DocumentationHeading> Headings(string markdown) => Parse(markdown).Headings;

    private static (MarkdownDocument Document, IReadOnlyList<DocumentationHeading> Headings) Parse(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var ids = new DocumentationHeadingIds();
        var headings = new List<DocumentationHeading>();
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var text = PlainText(heading);
            var id = ids.Next(text);
            heading.GetAttributes().Id = id;
            if (heading.Level is 2 or 3)
                headings.Add(new DocumentationHeading(heading.Level, text, id));
            heading.Level = Math.Min(heading.Level + 1, 6);
        }
        foreach (var link in document.Descendants<LinkInline>())
        {
            // Documentation must not fetch remote images or emit executable URL schemes.
            if (link.IsImage || !AllowedLink(link.Url))
            {
                link.IsImage = false;
                link.Url = null;
                link.GetDynamicUrl = null;
            }
        }
        foreach (var table in document.Descendants<Table>())
            table.GetAttributes().AddProperty("tabindex", "0");
        return (document, headings);
    }

    private static string PlainText(HeadingBlock heading)
    {
        var text = new StringBuilder();
        if (heading.Inline is not null)
            foreach (var inline in heading.Inline.Descendants<Inline>())
                if (inline is LiteralInline literal)
                    text.Append(literal.Content.ToString());
                else if (inline is CodeInline code)
                    text.Append(code.Content);
                else if (inline is LineBreakInline)
                    text.Append(' ');
        return text.ToString().Trim();
    }

    private static bool AllowedLink(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
            return false;
        if (value.StartsWith('#'))
            return true;
        if (value.StartsWith('/'))
            return !value.StartsWith("//", StringComparison.Ordinal);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(uri.UserInfo);
    }
}
