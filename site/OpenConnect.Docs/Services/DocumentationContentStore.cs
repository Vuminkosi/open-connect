using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace OpenConnect.Docs.Services;

public sealed record DocumentationDocument(
    string Version,
    string Slug,
    string Title,
    string Summary,
    int Order,
    string Markdown,
    IReadOnlyList<string> Headings);

public sealed class DocumentationContentStore
{
    private static readonly Regex FrontMatter = new("^---\\s*(?<yaml>.*?)\\s*---\\s*(?<body>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private readonly List<DocumentationDocument> _documents = [];
    public IReadOnlyList<DocumentationDocument> Documents => new ReadOnlyCollection<DocumentationDocument>(_documents);
    public bool IsLoaded { get; private set; }

    public void Load()
    {
        if (IsLoaded) return;
        var root = Path.Combine(AppContext.BaseDirectory, "Content");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Documentation content directory is missing: {root}");
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new InvalidDataException($"Documentation files must use Content/<version>/<slug>.md: {relative}");
            var version = parts[0];
            var slug = Path.GetFileNameWithoutExtension(parts[1]);
            var source = File.ReadAllText(file);
            var match = FrontMatter.Match(source);
            if (!match.Success) throw new InvalidDataException($"Missing YAML front matter: {relative}");
            var metadata = ParseYaml(match.Groups["yaml"].Value);
            if (!metadata.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) throw new InvalidDataException($"Missing title: {relative}");
            metadata.TryGetValue("summary", out var summary);
            var order = metadata.TryGetValue("order", out var orderText) && int.TryParse(orderText, out var parsed) ? parsed : 0;
            var headings = Regex.Matches(match.Groups["body"].Value, "^#{1,3}\\s+(.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToArray();
            _documents.Add(new DocumentationDocument(version, slug, title, summary ?? string.Empty, order, match.Groups["body"].Value.Trim(), headings));
        }
        if (_documents.GroupBy(d => $"{d.Version}/{d.Slug}").Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate documentation route.");
        _documents.Sort((left, right) => left.Order != right.Order ? left.Order.CompareTo(right.Order) : string.Compare(left.Title, right.Title, StringComparison.Ordinal));
        IsLoaded = true;
    }

    public DocumentationDocument? Find(string version, string slug) => _documents.FirstOrDefault(d => string.Equals(d.Version, version, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> Versions() => _documents.Select(d => d.Version).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToArray();

    private static Dictionary<string, string> ParseYaml(string yaml) => yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim().Trim('"', '\''), StringComparer.OrdinalIgnoreCase);
}
