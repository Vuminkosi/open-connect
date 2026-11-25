using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class SecurityWorkerOptionsAndReadinessTests
{
    [Fact]
    public void Configured_endpoint_alone_is_never_treated_as_ready()
    {
        var services = new ServiceCollection();
        services.AddOpenConnect();
        services.AddOpenConnectSecurityWorker(o =>
        {
            o.Endpoint = new Uri("https://security.internal/");
            o.Rules.Enabled = true;
            o.Gliner.Enabled = true;
            o.Malware.Enabled = true;
        });

        using var sp = services.BuildServiceProvider();
        var workerRegistry = sp.GetRequiredService<WorkerCapabilityRegistry>();
        var capabilityRegistry = sp.GetRequiredService<ICapabilityRegistry>();

        var descriptor = workerRegistry.Get(OpenConnectWorkerKind.Security);
        Assert.NotNull(descriptor);
        Assert.Equal(WorkerReadiness.Configured, descriptor.Readiness);
        Assert.Contains("security.malware", descriptor.CapabilityIds);
        Assert.Contains("file", descriptor.Modalities);

        // Must NOT appear in Ready lists until runtime verification updates readiness.
        Assert.Empty(workerRegistry.GetReady());
        Assert.Empty(capabilityRegistry.GetReady(CapabilityKind.Security));

        // Once verified at runtime, readiness transitions to Ready.
        workerRegistry.UpdateReadiness(OpenConnectWorkerKind.Security, WorkerReadiness.Ready);
        Assert.Single(workerRegistry.GetReady());
        Assert.Single(capabilityRegistry.GetReady(CapabilityKind.Security));
    }

    [Fact]
    public void Defaults_preserve_privacy_on_and_detector_aliases()
    {
        var options = new OpenConnectOptions();
        Assert.True(options.Privacy.Enabled);
        Assert.True(options.Privacy.Rules.Enabled);
        Assert.False(options.Privacy.GlinerPii.Enabled);
        Assert.False(options.Privacy.DebertaInjection.Enabled);
        Assert.False(options.Privacy.Malware.Enabled);
        Assert.False(options.Privacy.AgentTools.Enabled);
        Assert.False(options.Privacy.PromptInjection.Enabled);
        Assert.False(options.Privacy.ImageRedaction.Enabled);
        Assert.Same(options.Privacy.GlinerPii, options.Privacy.Gliner);
        Assert.Same(options.Privacy.DebertaInjection, options.Privacy.Deberta);

        var workerOpts = new SecurityWorkerOptions();
        Assert.Same(workerOpts.Gliner, workerOpts.GlinerPii);
        Assert.Same(workerOpts.Deberta, workerOpts.DebertaInjection);
        Assert.Equal(64 * 1024, workerOpts.Limits.MaxTextBytes);
        Assert.Equal(10 * 1024 * 1024, workerOpts.Limits.MaxImageBytes);
        Assert.Equal(20 * 1024 * 1024, workerOpts.Limits.MaxFileBytes);
    }

    [Fact]
    public void Validator_rejects_invalid_security_limits_and_thresholds()
    {
        var options = new OpenConnectOptions();
        options.Privacy.Limits.MaxTextBytes = 0;
        options.Privacy.Limits.MaxConcurrentRequests = 0;
        options.Privacy.Limits.RequestTimeout = TimeSpan.Zero;
        options.Privacy.PromptInjection.Threshold = 1.5;
        options.Privacy.ImageRedaction.RegionPaddingPixels = -1;
        options.Privacy.Malware.MaxConcurrentRequests = 0;

        var result = new OpenConnectOptionsValidator().Validate(Options.DefaultName, options);
        Assert.False(result.Succeeded);
        var joined = string.Join(" | ", result.Failures ?? []);
        Assert.Contains("Privacy:Limits:MaxTextBytes", joined);
        Assert.Contains("Privacy:Limits:MaxConcurrentRequests", joined);
        Assert.Contains("Privacy:Limits:RequestTimeout", joined);
        Assert.Contains("Privacy:PromptInjection:Threshold", joined);
        Assert.Contains("Privacy:ImageRedaction:RegionPaddingPixels", joined);
        Assert.Contains("Privacy:Malware:MaxConcurrentRequests", joined);
    }
}
