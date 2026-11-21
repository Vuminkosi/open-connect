using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

public static class DocumentationLiveUIManifest
{
    public static LiveUIManifest Create(IEnumerable<DocumentationDocument>? documents = null)
    {
        var routes = new List<LiveUIRoute>
        {
            new("documentation-home", "/", "Home", [Heading("documentation-home-heading", "Build AI applications, not AI infrastructure.")]),
            new("documentation-index", "/docs", "Documentation", [Heading("documentation-index-heading", "Documentation"),
                new("documentation-search", LiveUITargetKind.Input, "Search documentation",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Focus, LiveUICapability.SetValue },
                    Selector: "#docs-search"),
                new("documentation-show-guides", LiveUITargetKind.Button, "Show integration guides",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Activate },
                    Selector: "#docs-show-guides")]),
            new("documentation-api", "/api", "API reference", [Heading("documentation-api-heading", "Generated API reference")])
        };
        foreach (var document in documents ?? [])
        {
            if (!SafeSegment(document.Version) || !SafeSegment(document.Slug))
                throw new InvalidOperationException("Documentation routes contain an invalid segment.");
            var id = $"documentation-{document.Version}-{document.Slug}";
            var targets = new List<LiveUITarget> { Heading($"{id}-heading", document.Title) };
            var sections = DocumentationHeadingIds.Extract(document.Markdown);
            for (var index = 0; index < sections.Count; index++)
            {
                var section = sections[index];
                targets.Add(new LiveUITarget($"{id}-section-{index + 1}", LiveUITargetKind.Heading, section.Text,
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll },
                    Selector: $".markdown-body [id=\"{section.Id}\"]"));
            }
            routes.Add(new(id, $"/docs/{document.Version}/{document.Slug}", document.Title, targets));
        }
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "documentation-v1", "pending", routes);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private static bool SafeSegment(string value) => value.Length > 0 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static LiveUITarget Heading(string id, string label) => new(id, LiveUITargetKind.Heading, label,
        new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll },
        Selector: "main h1");
}
