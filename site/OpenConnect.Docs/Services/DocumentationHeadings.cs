using System.Text;

namespace OpenConnect.Docs.Services;

public sealed record DocumentationHeading(int Level, string Text, string Id);

/// <summary>Creates matching, unique IDs for rendered Markdown headings and page navigation.</summary>
public sealed class DocumentationHeadingIds
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string Next(string text)
    {
        var builder = new StringBuilder();
        var separator = false;
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && builder.Length > 0) builder.Append('-');
                builder.Append(character);
                separator = false;
            }
            else if (char.IsWhiteSpace(character) || character == '-') separator = true;
        }

        var key = builder.Length == 0 ? "section" : builder.ToString();
        var count = _counts.GetValueOrDefault(key);
        var candidate = count == 0 ? key : $"{key}-{count}";
        while (!_used.Add(candidate)) candidate = $"{key}-{++count}";
        _counts[key] = count + 1;
        return candidate;
    }

    public static IReadOnlyList<DocumentationHeading> Extract(string markdown)
        => DocumentationMarkdown.Headings(markdown);
}
