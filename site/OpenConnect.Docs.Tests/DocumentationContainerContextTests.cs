using System.Xml.Linq;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationContainerContextTests
{
    [Fact]
    public void SiteContainerIncludesTransitiveProjectReferences()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json")))
            root = root.Parent;
        Assert.NotNull(root);

        var repository = root.FullName;
        var patterns = File.ReadAllLines(Path.Combine(repository, "site", "OpenConnect.Docs", "Dockerfile.dockerignore"))
            .Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(Path.Combine(repository, "site", "OpenConnect.Docs", "OpenConnect.Docs.csproj"));
        while (pending.TryPop(out var project))
        {
            project = Path.GetFullPath(project);
            if (!visited.Add(project)) continue;
            Assert.True(File.Exists(project), $"Referenced project is missing: {project}");
            var relative = Path.GetRelativePath(repository, project).Replace('\\', '/');
            if (relative.StartsWith("src/", StringComparison.Ordinal))
            {
                var directory = relative[..relative.LastIndexOf('/')];
                Assert.Contains($"!{directory}/", patterns);
                Assert.Contains($"!{directory}/**", patterns);
            }
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include");
                Assert.False(string.IsNullOrWhiteSpace(include));
                pending.Push(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
        Assert.Contains("**/.env", patterns);
        Assert.Contains("**/*.key", patterns);
    }
}
