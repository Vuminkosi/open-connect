using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
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
}
