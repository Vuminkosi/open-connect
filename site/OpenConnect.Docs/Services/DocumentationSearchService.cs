namespace OpenConnect.Docs.Services;

public sealed class DocumentationSearchService(DocumentationContentStore store)
{
    public IReadOnlyList<DocumentationDocument> Search(string? query, string version)
    {
        if (string.IsNullOrWhiteSpace(query)) return store.Documents.Where(d => d.Version == version).Take(20).ToArray();
        return store.Documents.Where(d => d.Version == version && ($"{d.Title} {d.Summary} {d.Markdown}").Contains(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
    }
}
