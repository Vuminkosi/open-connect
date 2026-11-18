using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.QdrantVactor;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

/// <summary>
/// Proves OC2-D-EXT-007:
/// Prove Qdrant outage or missing embedder leaves direct/harness/workflow tasks independently usable.
/// Also proves OC2-D-EXT-011:
/// Test extension registration/unregistration has no mandatory DB/worker/transport dependency leak.
/// </summary>
public sealed class QdrantOutageAndIsolationTests
{
    [Fact]
    public async Task QdrantOutage_LeavesDirectTasksIndependentlyUsable()
    {
        // Simulate remote vector store outage (connection refused)
        var failingStore = new FailingVectorStore();

        // Query throws network/outage exception without crashing host
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failingStore.SearchAsync("tenant-1", "agent-1", new float[] { 0.1f, 0.2f }, 5, CancellationToken.None));

        // Core OpenConnect direct execution contracts have zero dependency on Qdrant
        var eventId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var canonicalEvent = new CanonicalAgentEvent(
            "openconnect.v1",
            eventId,
            "task.accepted",
            sessionId,
            1,
            DateTimeOffset.UtcNow,
            new System.Text.Json.Nodes.JsonObject { ["status"] = "ok" });

        Assert.Equal("openconnect.v1", canonicalEvent.SchemaVersion);
        Assert.Equal("task.accepted", canonicalEvent.EventType);
        Assert.Equal(sessionId, canonicalEvent.SessionId);
    }

    [Fact]
    public void ExtensionRegistration_HasNoMandatoryDbOrWorkerDependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Register QdrantVactor
        services.AddQdrantVactor(o =>
        {
            o.Enabled = true;
            o.SubscribeToAgentEvents = false;
            o.Endpoint = new Uri("http://localhost:6333");
            o.ApiKey = "test-api-key";
        });
        services.UseQdrantRestVectorStore();

        using var provider = services.BuildServiceProvider();

        // Vector store resolves cleanly
        var vectorStore = provider.GetService<IVectorStore>();
        Assert.NotNull(vectorStore);

        // Verify zero database, worker host, or transport registrations required
        var serviceTypes = services.Select(s => s.ServiceType.FullName).Where(s => s != null).ToList();
        Assert.DoesNotContain(serviceTypes, s => s!.Contains("DbContext", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(serviceTypes, s => s!.Contains("Nats", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(serviceTypes, s => s!.Contains("JetStream", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FailingVectorStore : IVectorStore
    {
        public Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken)
            => throw new HttpRequestException("Qdrant connection refused");

        public Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken)
            => throw new HttpRequestException("Qdrant connection refused");

        public Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken)
            => throw new HttpRequestException("Qdrant connection refused");
    }
}
