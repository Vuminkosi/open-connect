using System.Text.Json;
using Xunit;

namespace OpenConnect.Contracts.Tests;

public sealed class ContractParityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void EveryJsonSchemaParsesAsJson()
    {
        AssertJsonFilesParse(Path.Combine(RepositoryRoot, "contracts", "jsonschema"));
    }

    [Fact]
    public void EveryValidFixtureParsesAsJson()
    {
        AssertJsonFilesParse(Path.Combine(RepositoryRoot, "contracts", "fixtures", "valid"));
    }

    [Fact]
    public void InvalidFixturesArePresent()
    {
        var directory = Path.Combine(RepositoryRoot, "contracts", "fixtures", "invalid");

        Assert.True(Directory.Exists(directory), $"Invalid fixture directory not found: {directory}");
        Assert.NotEmpty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void OpenApiContractExistsAndDeclaresVersion()
    {
        var path = Path.Combine(RepositoryRoot, "contracts", "openapi", "openconnect.v1.yaml");

        Assert.True(File.Exists(path), $"OpenAPI contract not found: {path}");
        Assert.Contains("openconnect.v1", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static void AssertJsonFilesParse(string directory)
    {
        Assert.True(Directory.Exists(directory), $"Contract directory not found: {directory}");
        var files = Directory.GetFiles(directory, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts", "jsonschema")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository contracts directory.");
    }
}
