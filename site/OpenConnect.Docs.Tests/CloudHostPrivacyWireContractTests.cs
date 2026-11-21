using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudHostPrivacyWireContractTests
{
    [Fact]
    public async Task Registered_host_gate_records_model_and_output_decisions_without_content()
    {
        var events = new EvidenceSink();
        var services = new ServiceCollection();
        services.EnsureHostPrivacyServices();
        services.AddSingleton<IHostPrivacyEvidenceSink>(events);
        services.AddRecordedPrivacyFirewall(_ => new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector())));
        using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<IEgressGate>();
        var scope = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);
        var request = await gate.EvaluateAsync("contact person@example.com", scope);
        var response = await gate.EvaluateAsync("call +268 7612 3456", scope with { Destination = EgressDestination.Output });
        Assert.DoesNotContain("person@example.com", request.SanitizedContent);
        Assert.DoesNotContain("+268 7612 3456", response.SanitizedContent);
        Assert.Equal(2, events.Items.Count);
        Assert.Equal(EgressDestination.ExternalModel, events.Items[0].Destination);
        Assert.Equal(EgressDestination.Output, events.Items[1].Destination);
        Assert.Equal(1, events.Items[0].Categories["EMAIL"]);
        Assert.Equal(1, events.Items[1].Categories["PHONE"]);
        var json = JsonSerializer.Serialize(events.Items);
        Assert.DoesNotContain("person@example.com", json);
        Assert.DoesNotContain("+268 7612 3456", json);
    }

    private sealed class EvidenceSink : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Items { get; } = [];
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        { Items.Add(evidence); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Actual_sender_payload_is_accepted_by_tenant_bound_receiver_parser()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenantId, Guid.NewGuid(), "Development", new HashSet<string>());
        var scope = new EgressContext(new(Ulid.Parse("00000000000000000000000001")), new(Ulid.NewUlid()),
            new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), scope, new EgressDecision(true, "protected-content",
            [new EntitySpan(7, 12, "EMAIL")], SecurityPolicyAction.Mask, "protected-reason"));
        var handler = new Handler(async request =>
        {
            var content = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("protected-content", content);
            Assert.DoesNotContain("protected-reason", content);
            using var json = JsonDocument.Parse(content);
            var report = CloudHostPrivacyReportParser.Parse(json.RootElement, identity);
            Assert.Equal(evidence.EventId, report.EventId);
            Assert.Equal("{\"EMAIL\":1}", report.CategoryCounts);
            Assert.Equal("allowed", report.ReasonCode);
            Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(json.RootElement, identity with { TenantId = Guid.NewGuid() }));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"eventId\":\"{report.EventId:D}\"}}", Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);
    }

    [Fact]
    public async Task InspectionFailureCrossesWireWithoutDetectorDiagnostics()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenantId, Guid.NewGuid(), "Development", new HashSet<string>());
        var scope = new EgressContext(new(Ulid.Parse("00000000000000000000000001")), new(Ulid.NewUlid()),
            new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), scope,
            new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure"));
        var handler = new Handler(async request =>
        {
            var content = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("detector_failure", content, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(content);
            var report = CloudHostPrivacyReportParser.Parse(json.RootElement, identity);
            Assert.Equal("inspection_failed", report.ReasonCode);
            Assert.False(report.Allowed);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"eventId\":\"{report.EventId:D}\"}}", Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
