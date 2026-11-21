using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

public static class DocumentationLiveUIManifest
{
    public static LiveUIManifest Create(IEnumerable<DocumentationDocument>? documents = null)
    {
        var routes = new List<LiveUIRoute>
        {
            new("documentation-home", "/", "Home", [Heading("documentation-home-heading", "Build AI applications, not AI infrastructure.")]),
            new("documentation-index", "/docs", "Documentation", [Heading("documentation-index-heading", "Documentation")]),
            new("documentation-api", "/api", "API reference", [Heading("documentation-api-heading", "Generated API reference")])
        };
        foreach (var document in documents ?? [])
        {
            if (!SafeSegment(document.Version) || !SafeSegment(document.Slug))
                throw new InvalidOperationException("Documentation routes contain an invalid segment.");
            var id = $"documentation-{document.Version}-{document.Slug}";
            routes.Add(new(id, $"/docs/{document.Version}/{document.Slug}", document.Title,
                [Heading($"{id}-heading", document.Title)]));
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
