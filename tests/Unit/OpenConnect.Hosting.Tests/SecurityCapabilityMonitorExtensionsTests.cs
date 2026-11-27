using System.Net;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class SecurityCapabilityMonitorExtensionsTests
{
    private static readonly string[] AllSecurityIds =
    [
        "security.rules",
        "security.gliner-pii",
        "security.deberta-injection",
        "security.qwen3-guard",
        "security.paddle-ocr",
        "security.yunet",
        "security.malware",
        "security.clamav"
    ];

    [Fact]
    public void ParserAcceptsExpandedSecurityCapabilityIdsAndMapsToSecurityKind()
    {
        var payload = JsonSerializer.Serialize(new
        {
            capabilities = AllSecurityIds.Select(id => new
            {
                schemaVersion = "openconnect.v1",
                id,
                kind = "security",
                state = "ready",
                version = "1.2.0",
                localOnly = true
            }).ToArray()
        });

        using var json = JsonDocument.Parse(payload);
        var parsed = WorkerCapabilityResponseParser.Parse(
            json.RootElement,
            AllSecurityIds.ToHashSet(StringComparer.Ordinal),
            inference: false);

        Assert.Equal(AllSecurityIds.Length, parsed.Count);
        Assert.All(parsed, item =>
        {
            Assert.Equal(CapabilityKind.Security, item.Kind);
            Assert.Equal(DetectorState.Ready, item.State);
        });
    }

    [Fact]
    public void ParserRejectsSecurityIdWithWrongWorkerKindOrUnreadyState()
    {
        using var wrongKind = JsonDocument.Parse(
            """{"capabilities":[{"schemaVersion":"openconnect.v1","id":"security.malware","kind":"tool","state":"ready","version":"1.0.0","localOnly":true}]}""");
        Assert.Throws<InvalidOperationException>(() =>
            WorkerCapabilityResponseParser.Parse(wrongKind.RootElement, new HashSet<string> { "security.malware" }, false));

        using var provisioning = JsonDocument.Parse(
            """{"capabilities":[{"schemaVersion":"openconnect.v1","id":"security.gliner-pii","kind":"security","state":"provisioning","version":"1.0.0","localOnly":true}]}""");
        Assert.Throws<InvalidOperationException>(() =>
            WorkerCapabilityResponseParser.Parse(provisioning.RootElement, new HashSet<string> { "security.gliner-pii" }, false));
    }

    [Fact]
    public void ConfiguredEndpointIsNotReadyUntilVerifiedPollAndStaleLeaseRevokes()
    {
        var registry = new CapabilityRegistry();
        var source = new WorkerCapabilitySource("https://security.internal", "secret-token", AllSecurityIds);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null,
            _ => new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            {
                BaseAddress = new Uri("https://security.internal")
            });

        // Configured endpoint alone must never be treated as ready.
        foreach (var id in AllSecurityIds)
            Assert.Null(registry.Get(new CapabilityId(id)));
    }

    [Fact]
    public async Task PartialDetectorReadinessPublishesOnlyVerifiedDetectorsAndRevokesOmittedOnes()
    {
        var registry = new CapabilityRegistry();
        var body = """
            {"capabilities":[
              {"schemaVersion":"openconnect.v1","id":"security.rules","kind":"security","state":"ready","version":"1.1.0","localOnly":true},
              {"schemaVersion":"openconnect.v1","id":"security.malware","kind":"security","state":"ready","version":"1.1.0","localOnly":true}
            ]}
            """;

        var source = new WorkerCapabilitySource("https://security.internal", "secret-token",
            ["security.rules", "security.gliner-pii", "security.malware"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null,
            _ => new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }))
            {
                BaseAddress = new Uri("https://security.internal")
            });

        await monitor.RefreshAsync();
        Assert.Equal(DetectorState.Ready, registry.Get(new CapabilityId("security.rules"))!.State);
        Assert.Equal(DetectorState.Ready, registry.Get(new CapabilityId("security.malware"))!.State);
        Assert.Null(registry.Get(new CapabilityId("security.gliner-pii")));

        // Now malware goes offline; rules stays ready and malware lease is revoked.
        body = """
            {"capabilities":[
              {"schemaVersion":"openconnect.v1","id":"security.rules","kind":"security","state":"ready","version":"1.1.0","localOnly":true}
            ]}
            """;
        await monitor.RefreshAsync();
        Assert.Equal(DetectorState.Ready, registry.Get(new CapabilityId("security.rules"))!.State);
        Assert.Null(registry.Get(new CapabilityId("security.malware")));
    }

    [Fact]
    public void SourceRejectsExceedingBoundedCapabilityIdCount()
    {
        var tooMany = Enumerable.Range(0, WorkerCapabilitySource.MaxCapabilityIdsPerSource + 1)
            .Select(i => $"security.id.{i}")
            .ToArray();
        Assert.Throws<ArgumentException>(() => new WorkerCapabilitySource("https://security.internal", "token", tooMany));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
