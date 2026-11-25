using System.Net;
using System.Net.Http.Headers;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Components.Pages;
using OpenConnect.Docs.Components.VoiceBenchmark;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.Voice;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class LiveVoiceBenchmarkDashboardTests : BunitContext
{
    [Fact]
    public void Dashboard_SeparatesTargetsFromUnmeasuredMetricsAndNeverFabricatesNumbers()
    {
        var cut = Render<LiveVoiceBenchmarkDashboard>();

        var metricsTable = cut.Find("[data-testid='voice-metrics-table']");
        Assert.Contains("Target P50", metricsTable.TextContent);
        Assert.Contains("Target P95", metricsTable.TextContent);
        Assert.Contains("Measured P50", metricsTable.TextContent);
        Assert.Contains("Measured P95", metricsTable.TextContent);
        Assert.Contains("≤ 800 ms", metricsTable.TextContent);
        Assert.Contains("≤ 1,500 ms", metricsTable.TextContent);
        Assert.Contains("≤ 400 ms", metricsTable.TextContent);
        Assert.Contains("≤ 200 ms", metricsTable.TextContent);
        Assert.Contains("≤ 150 ms", metricsTable.TextContent);
        Assert.Contains(LiveVoiceBenchmarkService.NotYetMeasured, metricsTable.TextContent);

        var measuredCells = cut.FindAll(".voice-measured-cell");
        Assert.NotEmpty(measuredCells);
        Assert.All(measuredCells, cell => Assert.Equal(LiveVoiceBenchmarkService.NotYetMeasured, cell.TextContent.Trim()));

        Assert.Equal("0", cut.Find("[data-testid='voice-benchmark-samples']").TextContent.Trim());
        Assert.Equal("No benchmark run recorded", cut.Find("[data-testid='voice-benchmark-classification']").TextContent.Trim());
    }

    [Fact]
    public void Dashboard_LabelsSyntheticHarnessRunsExplicitly()
    {
        var snapshot = new LiveVoiceBenchmarkSnapshot(
            HasMeasuredRun: true,
            IsSyntheticHarness: true,
            SampleCount: 12,
            MeasuredAtUtc: new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero),
            DeploymentMode: "Local / Self-hosted",
            HardwareProfile: "Local synthetic harness",
            RunClassificationLabel: "Synthetic / Local Harness",
            UnderrunCount: 0,
            QueuePressureCount: 0,
            Metrics: LiveVoiceBenchmarkService.CreateDefaultMetrics(),
            Stages: LiveVoiceBenchmarkService.CreateDefaultStages(),
            Providers: []);

        var cut = Render<LiveVoiceBenchmarkDashboard>(parameters => parameters
            .Add(p => p.SnapshotOverride, snapshot));

        Assert.Contains("Synthetic / Local Harness", cut.Find("[data-testid='voice-benchmark-classification']").TextContent);
        Assert.Equal("12", cut.Find("[data-testid='voice-benchmark-samples']").TextContent.Trim());
        Assert.Contains("2026-11-24 08:00 UTC", cut.Find("[data-testid='voice-benchmark-date']").TextContent);
    }

    [Fact]
    public void Dashboard_ShowsProviderReadinessAndPrivacyBoundaryWithoutRawAudioOrTranscript()
    {
        Services.AddSingleton(new DocumentationAssistantAvailability(Configured: true));
        Services.AddScoped(_ => CreateVoiceAvailability(HttpStatusCode.OK, "{\"available\":true}"));

        var cut = Render<LiveVoiceBenchmarkDashboard>();

        cut.WaitForAssertion(() =>
        {
            var readinessTable = cut.Find("[data-testid='voice-readiness-table']");
            Assert.Contains("Cloud Muse", readinessTable.TextContent);
            Assert.Contains("ENDPOINTING", readinessTable.TextContent);
            Assert.Contains("Local Whisper", readinessTable.TextContent);
            Assert.Contains("Kokoro 82M", readinessTable.TextContent);
            Assert.Contains("Silero ONNX", readinessTable.TextContent);
            Assert.Contains("Ready", readinessTable.TextContent);
        });

        var privacyNote = cut.Find("[data-testid='voice-benchmark-privacy-note']").TextContent;
        Assert.Contains("Raw transcripts", privacyNote);
        Assert.Contains("raw PCM audio", privacyNote);
        Assert.DoesNotContain("data:audio/", cut.Markup, StringComparison.OrdinalIgnoreCase);

        var waterfallTable = cut.Find("[data-testid='voice-waterfall-table']").TextContent;
        Assert.Contains("Endpointing", waterfallTable);
        Assert.Contains("STT finalization", waterfallTable);
        Assert.Contains("Policy screening", waterfallTable);
        Assert.Contains("TTS onset", waterfallTable);
    }

    [Fact]
    public void Dashboard_ShowsUnavailableWhenHostReadinessFailsClosed()
    {
        Services.AddSingleton(new DocumentationAssistantAvailability(Configured: true));
        Services.AddScoped(_ => CreateVoiceAvailability(HttpStatusCode.OK, "{\"available\":false}"));

        var cut = Render<LiveVoiceBenchmarkDashboard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Unavailable", cut.Find("[data-testid='voice-readiness-table']").TextContent);
            Assert.Contains("Text input in MiniComposer remains active.", cut.Find("[data-testid='voice-benchmark-status']").TextContent);
        });
    }

    [Fact]
    public void DocPage_MountsBenchmarkDashboardOnlyOnVoiceAndTranslationPage()
    {
        var store = new DocumentationContentStore();
        store.Load();
        Services.AddSingleton(store);

        var voicePage = Render<DocPage>(parameters => parameters
            .Add(p => p.Version, "v1")
            .Add(p => p.Slug, "voice-and-translation"));

        Assert.NotNull(voicePage.Find("[data-testid='live-voice-benchmark-dashboard']"));
        Assert.Contains("Continuous Live Voice", voicePage.Markup);
        Assert.Contains("openconnect.voice.v2", voicePage.Markup);

        var privacyPage = Render<DocPage>(parameters => parameters
            .Add(p => p.Version, "v1")
            .Add(p => p.Slug, "privacy"));

        Assert.Empty(privacyPage.FindAll("[data-testid='live-voice-benchmark-dashboard']"));
    }

    private static DocumentationVoiceAvailability CreateVoiceAvailability(HttpStatusCode status, string body)
    {
        var client = new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("https://localhost:8098/openconnect/v1/")
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "visitor-token");
        return new DocumentationVoiceAvailability(client);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
