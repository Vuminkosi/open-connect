namespace OpenConnect.Docs.Services;

public sealed record GeneratedApiDocument(string Name, string RelativePath, string Content);

public sealed class GeneratedApiStore
{
    public IReadOnlyList<GeneratedApiDocument> Documents { get; }

    public GeneratedApiStore()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "generated", "api", "v1");
        Documents = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new GeneratedApiDocument(Path.GetFileNameWithoutExtension(path), Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)))
                .ToArray()
            : Array.Empty<GeneratedApiDocument>();
    }
}
