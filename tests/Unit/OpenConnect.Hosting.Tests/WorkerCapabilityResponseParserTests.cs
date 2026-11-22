using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class WorkerCapabilityResponseParserTests
{
    [Fact]
    public void StructuredResponsesExposeOnlyExplicitlyAllowedReadyIds()
    {
        using var json = JsonDocument.Parse("""{"capabilities":[{"schemaVersion":"openconnect.v1","id":"knowledge.embed","kind":"embedding","state":"ready","version":"1.0.0","localOnly":true},{"schemaVersion":"openconnect.v1","id":"knowledge.index","kind":"tool","state":"ready","version":"1.0.0","localOnly":true}]}""");
        var result = WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "knowledge.index" }, false);
        var item = Assert.Single(result);
        Assert.Equal(new CapabilityId("knowledge.index"), item.Id);
        Assert.Equal(CapabilityKind.Tool, item.Kind);
        Assert.Equal(DetectorState.Ready, item.State);
    }

    [Theory]
    [InlineData("failed", "tool", true)]
    [InlineData("ready", "image", true)]
    [InlineData("ready", "tool", false)]
    public void RejectsUnreadyWrongKindOrRemoteDescriptor(string state, string kind, bool local)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            capabilities = new[] { new {
            schemaVersion = "openconnect.v1", id = "knowledge.index", kind, state, version = "1", localOnly = local } }
        }));
        Assert.Throws<InvalidOperationException>(() => WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "knowledge.index" }, false));
    }

    [Theory]
    [InlineData("real", true)]
    [InlineData("fake-bounded", false)]
    public void InferenceUsesItsOwnShapeAndRejectsFakeEngine(string engine, bool accepted)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            worker = "inference",
            version = "1",
            backend = "local",
            engine,
            modelId = (string?)null,
            modelRevision = (string?)null,
            modelLoaded = false,
            capabilities = new[] { "chat", "documents.extract" },
            privacyMode = "strict-local"
        }));
        if (accepted)
            Assert.Equal("documents.extract", Assert.Single(WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "documents.extract" }, true)).Id.Value);
        else
            Assert.Throws<InvalidOperationException>(() => WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "documents.extract" }, true));
    }

    [Theory]
    [InlineData("structured.json", "knowledge.embed", false)]
    [InlineData("inference.json", "documents.extract", true)]
    public async Task ReadsSharedPythonWorkerFixtures(string filename, string id, bool inference)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenConnect.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "contracts", "fixtures", "worker-capability", filename)));
        var result = WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { id }, inference);
        Assert.Equal(id, Assert.Single(result).Id.Value);
    }

    [Fact]
    public void RejectsDuplicatePropertiesAndUnrecognizedScopes()
    {
        using var json = JsonDocument.Parse("""{"capabilities":[],"capabilities":[]}""");
        Assert.Throws<InvalidOperationException>(() => WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "knowledge.embed" }, false));
        Assert.Throws<ArgumentException>(() => WorkerCapabilityResponseParser.Parse(json.RootElement, new HashSet<string> { "unknown" }, false));
    }
}
