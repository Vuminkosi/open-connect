using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using System.Xml.Linq;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class PackageBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "NATS",
        "Qdrant",
        "MudBlazor",
        "Microsoft.AspNetCore",
        "Aspire",
        "Python",
        "Torch",
        "ONNX"
    ];

    [Fact]
    public void Abstractions_and_agents_assemblies_have_no_forbidden_direct_references()
    {
        var assemblies = new[]
        {
            typeof(AgentDefinition).Assembly,
            typeof(MafRealAgentFactory).Assembly
        };

        foreach (var assembly in assemblies)
        {
            var forbidden = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => ForbiddenPrefixes.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            Assert.True(forbidden.Length == 0,
                $"{assembly.GetName().Name} has forbidden direct assembly references: {string.Join(", ", forbidden)}");
        }
    }

    [Fact]
    public void Core_project_declares_no_forbidden_heavyweight_packages()
    {
        var attemptedPaths = new List<string>();
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? projectPath = null;

        for (var depth = 0; directory is not null && depth <= 6; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OpenConnect.Core", "OpenConnect.Core.csproj");
            attemptedPaths.Add(candidate);
            var hasRepositoryMarker = File.Exists(Path.Combine(directory.FullName, "OpenConnect.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "OpenConnect.sln"))
                || Directory.Exists(Path.Combine(directory.FullName, "src"));

            if (hasRepositoryMarker && File.Exists(candidate))
            {
                projectPath = candidate;
                break;
            }
        }

        Assert.True(projectPath is not null && File.Exists(projectPath),
            $"Could not locate src/OpenConnect.Core/OpenConnect.Core.csproj. Attempted paths: {string.Join("; ", attemptedPaths)}");

        // The declared-graph guard complements the runtime assembly guard; transitive closure is the package manager's job + release smoke (out of scope).
        var project = XDocument.Load(projectPath!);
        var packageNames = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => include!);
        var forbiddenPrefixes = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "NATS",
            "Qdrant",
            "MudBlazor",
            "Microsoft.AspNetCore",
            "Aspire",
            "Python",
            "Torch",
            "ONNX"
        };
        var forbiddenPackages = packageNames
            .Where(name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(forbiddenPackages);

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;
        var projectReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
            .ToArray();
        var abstractionsProject = Path.GetFullPath(Path.Combine(
            projectDirectory, "..", "OpenConnect.Abstractions", "OpenConnect.Abstractions.csproj"));

        Assert.All(projectReferences, reference =>
            Assert.Equal(abstractionsProject, reference, ignoreCase: true));
    }
}
