using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

public static class DocumentationLiveUIManifest
{
    public static LiveUIManifest Create(IEnumerable<DocumentationDocument>? documents = null)
    {
        var routes = new List<LiveUIRoute>
        {
            new("documentation-home", "/", "Home", []),
            new("documentation-index", "/docs", "Documentation", []),
            new("documentation-api", "/api", "API reference", [])
        };
        foreach (var document in documents ?? [])
        {
            if (!SafeSegment(document.Version) || !SafeSegment(document.Slug))
                throw new InvalidOperationException("Documentation routes contain an invalid segment.");
            routes.Add(new($"documentation-{document.Version}-{document.Slug}",
                $"/docs/{document.Version}/{document.Slug}", document.Title, []));
        }
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "documentation-v1", "pending", routes);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private static bool SafeSegment(string value) => value.Length > 0 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
