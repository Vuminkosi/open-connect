using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Capabilities;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

public class RegistrationInvariantsTests
{
    [Fact]
    public void REG_AT_017_All11WorkersRegisteredInCanonicalRegistry()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();

        services.AddOpenConnectSecurityWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectClassificationWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectTranslationWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectInferenceWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectTranscriptionWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectRealtimeVoiceWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectKokoroWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectMmsTtsWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectPlaywrightWorker(o => o.Enabled = true);
        services.AddOpenConnectOperationalAgents(o => o.HostingMode = WorkerHostingMode.Embedded);
        services.AddOpenConnectOutbox(o => o.HostingMode = WorkerHostingMode.Embedded);

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IWorkerCapabilityRegistry>();
        var all = registry.GetAll();

        Assert.Equal(11, all.Count);
        Assert.All(Enum.GetValues<OpenConnectWorkerKind>(), kind =>
        {
            var found = registry.Get(kind);
            Assert.NotNull(found);
            Assert.Equal(kind, found.Kind);
        });
    }

    [Fact]
    public void REG_AT_019_020_HostingModeExclusivityThrowsOnConflict()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();
        services.AddOpenConnectOperationalAgents(o => o.HostingMode = WorkerHostingMode.Embedded);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddOpenConnectOperationalAgents(o => o.HostingMode = WorkerHostingMode.Standalone);
        });

        Assert.Contains(ErrorCodes.WorkerHostingConflict, ex.Message);
    }

    [Fact]
    public void REG_AT_019_020_OutboxHostingModeExclusivityThrowsOnConflict()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();
        services.AddOpenConnectOutbox(o => o.HostingMode = WorkerHostingMode.Embedded);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddOpenConnectOutbox(o => o.HostingMode = WorkerHostingMode.Standalone);
        });

        Assert.Contains(ErrorCodes.WorkerHostingConflict, ex.Message);
    }

    [Fact]
    public void REG_AT_015_016_SingleHostPrivacyOptionsConfiguredWithoutDuplicates()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();
        services.AddOpenConnectPrivacy(p =>
        {
            p.Mode = FirewallMode.StrictLocal;
            p.RequireEgressValidation = true;
        });
        services.AddOpenConnectPrivacy(p =>
        {
            p.EnableTokenization = true;
        });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<OpenConnectOptions>>().Value;
        Assert.True(options.Privacy.RequireEgressValidation);
        Assert.True(options.Privacy.EnableTokenization);
        Assert.Equal(FirewallMode.StrictLocal, options.Privacy.Mode);
    }

    [Fact]
    public void REG_AT_004_GatewaySileroVadPerCaptureIsolationAndResampling()
    {
        var vadOptions = Microsoft.Extensions.Options.Options.Create(new ServerVadSileroOptions
        {
            SampleRate = 16_000,
            FrameSamples = 512,
            SpeechThreshold = 0.5f
        });
        var engine = new ServerSileroVadEngine(vadOptions);

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        // Stream A: 16kHz
        var pcm16k = new float[512];
        var res1 = engine.ProcessPcmFrame("capture-A", tenant, principal, sequence: 0, sourceSampleRate: 16_000, sourceSampleOffset: 0, pcm16k);
        Assert.False(res1.IsSpeech);

        // Stream B: 24kHz with automatic downsampling to 16kHz
        var pcm24k = new float[768];
        var res2 = engine.ProcessPcmFrame("capture-B", tenant, principal, sequence: 0, sourceSampleRate: 24_000, sourceSampleOffset: 0, pcm24k);
        Assert.False(res2.IsSpeech);

        // Dispose capture-A state independently
        var disposed = engine.DisposeCapture("capture-A", tenant, principal);
        Assert.True(disposed);

        // Processing capture-A after disposal starts fresh without affecting capture-B
        var res3 = engine.ProcessPcmFrame("capture-A", tenant, principal, sequence: 0, sourceSampleRate: 16_000, sourceSampleOffset: 0, pcm16k);
        Assert.False(res3.IsSpeech);
    }

    [Fact]
    public void REG_AT_004_GatewayVadSequenceGapResetsState()
    {
        var vadOptions = Microsoft.Extensions.Options.Options.Create(new ServerVadSileroOptions
        {
            SampleRate = 16_000,
            FrameSamples = 512,
            SpeechThreshold = 0.5f
        });
        var engine = new ServerSileroVadEngine(vadOptions);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var pcm = new float[512];

        engine.ProcessPcmFrame("gap-test", tenant, principal, sequence: 0, 16_000, 0, pcm);

        // Jump sequence by 5 -> triggers sequence gap reset without throwing
        var res = engine.ProcessPcmFrame("gap-test", tenant, principal, sequence: 5, 16_000, 2560, pcm);
        Assert.True(res.StateWasReset);
    }

    [Fact]
    public void REG_AT_029_CloudTranscriptionPolicyEvaluatorDeniesAudioOnStrictLocal()
    {
        var coreOptions = new OpenConnectOptions();
        coreOptions.Privacy.Enabled = true;
        coreOptions.Privacy.Mode = FirewallMode.StrictLocal;

        var cloudOptions = new CloudTranscriptionOptions
        {
            Provider = "azure-speech",
            Mode = CloudTranscriptionMode.Endpointing,
            RequireApprovedAudioEgress = true,
            ApprovedAudioEgressPolicy = false
        };

        var evaluator = new CloudTranscriptionPolicyEvaluator(
            Microsoft.Extensions.Options.Options.Create(cloudOptions),
            Microsoft.Extensions.Options.Options.Create(coreOptions));

        var decision = evaluator.EvaluateAudioEgress();
        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.VoiceEgressDenied, decision.ErrorCode);
    }

    [Fact]
    public void REG_AT_029_CloudTranscriptionPolicyEvaluatorAllowsWhenPermitted()
    {
        var coreOptions = new OpenConnectOptions();
        coreOptions.Privacy.Enabled = true;
        coreOptions.Privacy.Mode = FirewallMode.PolicyControlled;

        var cloudOptions = new CloudTranscriptionOptions
        {
            Provider = "azure-speech",
            Mode = CloudTranscriptionMode.Endpointing,
            RequireApprovedAudioEgress = false,
            ProviderCapabilitiesVerified = true
        };

        var evaluator = new CloudTranscriptionPolicyEvaluator(
            Microsoft.Extensions.Options.Options.Create(cloudOptions),
            Microsoft.Extensions.Options.Options.Create(coreOptions));

        var decision = evaluator.EvaluateAudioEgress();
        Assert.True(decision.Allowed);
        Assert.Null(decision.ErrorCode);
    }

    [Fact]
    public void REG_AT_008_012_AgentExecutionProfilesConfigured()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectExecutionProfiles(o =>
        {
            o.EnableDirectAgent = true;
            o.EnableHarnessAgent = true;
            o.EnableWorkflow = true;
            o.EnableDurableWorkflow = true;
        });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<OpenConnectExecutionProfileOptions>>().Value;
        Assert.True(options.EnableDirectAgent);
        Assert.True(options.EnableHarnessAgent);
        Assert.True(options.EnableWorkflow);
        Assert.True(options.EnableDurableWorkflow);
    }

    [Fact]
    public void REG_AT_014_McpServiceCatalogAndAuthorization()
    {
        var registry = new McpServiceRegistry();
        var mcpOptions = new OpenConnectMcpServiceOptions
        {
            Name = "jira-mcp",
            Endpoint = new Uri("https://jira-mcp.internal")
        };
        mcpOptions.AllowedTools.Add("search_issues");
        mcpOptions.AllowedTools.Add("create_issue");
        registry.Register(mcpOptions);

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.True(registry.IsToolAllowed("jira-mcp", "search_issues", principal, tenant));
        Assert.False(registry.IsToolAllowed("jira-mcp", "delete_all", principal, tenant));
        Assert.False(registry.IsToolAllowed("jira-mcp", "search_issues", default, tenant));
    }

    [Fact]
    public void REG_AT_025_OptionalityMissingWorkersDoesNotBreakCore()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IWorkerCapabilityRegistry>();
        Assert.Empty(registry.GetAll());

        var ready = registry.GetReady();
        Assert.Empty(ready);
    }
}
