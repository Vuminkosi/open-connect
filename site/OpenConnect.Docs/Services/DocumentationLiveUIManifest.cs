using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

public static class DocumentationLiveUIManifest
{
    public static LiveUIManifest Create()
    {
        // Only explicitly reviewed public routes are exposed to the UI planner.
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "documentation-v1", "pending",
        [
            new("documentation-home", "/", "Home", []),
            new("documentation-index", "/docs", "Documentation", []),
            new("documentation-api", "/api", "API reference", [])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }
}
