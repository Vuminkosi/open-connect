using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

public static class ServiceCollectionExtensions
{
    private const string DefaultContractVersion = "openconnect.worker.v1";

    public static IServiceCollection AddOpenConnect(this IServiceCollection services, Action<OpenConnectOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ThrowOnConflictingRepeatRegistration(services, configure);
        services.TryAddSingleton(OptionsConflictGuard.Capture(Materialize(configure)));
        services.AddSingleton<IValidateOptions<OpenConnectOptions>, OpenConnectOptionsValidator>();
        services.AddOptions<OpenConnectOptions>().Configure(options => configure?.Invoke(options)).ValidateOnStart();
        services.TryAddSingleton<ICapabilityRegistry>(_ => new CapabilityRegistry());
        services.TryAddSingleton<WorkerCapabilityRegistry>(sp => new WorkerCapabilityRegistry(TimeProvider.System, sp.GetService<ICapabilityRegistry>()));
        services.TryAddSingleton<IWorkerCapabilityRegistry>(sp => sp.GetRequiredService<WorkerCapabilityRegistry>());
        services.TryAddSingleton<IAgentDefinitionRegistry>(provider =>
        {
            var registry = new AgentDefinitionRegistry();
            if (provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value.Agents.RegisterLeadAgent)
                registry.Register(OpenConnectLeadAgent.Definition);
            return registry;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenConnectOptions>, PrivacyWarningConfiguration>());
        EnsureSingleHostFirewall(services, configure is null ? null : Materialize(configure).Privacy);
        return services;
    }

    /// <summary>Configures the single host privacy firewall without registering a duplicate firewall instance.</summary>
    public static IServiceCollection AddOpenConnectPrivacy(this IServiceCollection services, Action<PrivacyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PrivacyOptions>().Configure(options => configure?.Invoke(options));
        services.AddOptions<OpenConnectOptions>().Configure(options => configure?.Invoke(options.Privacy));
        var privacySnapshot = new PrivacyOptions();
        configure?.Invoke(privacySnapshot);
        EnsureSingleHostFirewall(services, privacySnapshot);
        return services;
    }

    public static IServiceCollection AddOpenConnectObservability(this IServiceCollection services, Action<OpenConnectObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectObservabilityOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options => !options.RecordSensitiveContent, "Observability must not record raw sensitive content by default.")
            .ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddOpenConnectModelProvider(this IServiceCollection services, Action<OpenConnectModelProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectModelProviderOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options => !options.AllowUnapprovedFallback, $"{ErrorCodes.ProviderUnavailable}: Unapproved model provider fallback is prohibited.")
            .ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddOpenConnectExecutionProfiles(this IServiceCollection services, Action<OpenConnectExecutionProfileOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectExecutionProfileOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddOpenConnectNats(this IServiceCollection services, Action<OpenConnectNatsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectNatsOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options => !options.EnableJetStream || !string.IsNullOrWhiteSpace(options.Url), "NATS URL is required when JetStream is enabled.")
            .ValidateOnStart();
        services.AddOptions<OpenConnectOptions>().Configure(options =>
        {
            var nats = new OpenConnectNatsOptions();
            configure?.Invoke(nats);
            if (nats.EnableJetStream)
                options.Transport.Mode = OpenConnectTransportMode.JetStream;
        });
        return services;
    }

    public static IServiceCollection AddOpenConnectTool<TTool>(this IServiceCollection services, string toolId, AgentId? agentId = null)
        where TTool : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        services.TryAddScoped<TTool>();
        var registry = GetOrAddCoreToolRegistry(services);
        var owner = agentId ?? OpenConnectLeadAgent.Definition.Id;
        registry.RegisterDescriptor(new ToolDescriptor(new ToolId(toolId), typeof(TTool).Name, owner));
        return services;
    }

    public static IServiceCollection AddOpenConnectMcp(this IServiceCollection services, Action<OpenConnectMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectMcpOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.TryAddSingleton<McpServiceRegistry>();
        return services;
    }

    public static IServiceCollection AddOpenConnectMcpService(this IServiceCollection services, string name, Action<OpenConnectMcpServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new OpenConnectMcpServiceOptions { Name = name };
        configure?.Invoke(options);
        if (options.Endpoint is null)
            throw new InvalidOperationException($"MCP service '{name}' requires a valid Endpoint.");
        var registry = GetOrAddMcpRegistry(services);
        registry.Register(options);
        return services;
    }

    public static IServiceCollection AddOpenConnectVoice(this IServiceCollection services, Action<OpenConnectVoiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<OpenConnectVoiceOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.TryAddSingleton<VoiceTurnCoordinator>();
        return services;
    }

    public static IServiceCollection AddOpenConnectServerVadSilero(this IServiceCollection services, Action<ServerVadSileroOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var materialized = new ServerVadSileroOptions();
        configure?.Invoke(materialized);
        if (materialized.SampleRate != 16_000 || materialized.FrameSamples != 512)
            throw new InvalidOperationException("Server Silero VAD requires 16000 Hz sample rate and 512-sample frames.");
        if (string.IsNullOrWhiteSpace(materialized.ModelPath))
            throw new InvalidOperationException("Server Silero VAD requires a non-empty ModelPath.");

        services.AddOptions<ServerVadSileroOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.TryAddSingleton<ServerSileroVadEngine>();
        return services;
    }

    public static IServiceCollection AddOpenConnectCloudTranscription(this IServiceCollection services, Action<CloudTranscriptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<CloudTranscriptionOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.TryAddSingleton<CloudTranscriptionPolicyEvaluator>();
        return services;
    }

    public static IServiceCollection AddOpenConnectSecurityWorker(this IServiceCollection services, Action<SecurityWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SecurityWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Security, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<SecurityWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        services.AddOptions<OpenConnectOptions>().Configure(core =>
        {
            core.Privacy.Rules.Enabled = options.Rules.Enabled;
            core.Privacy.GlinerPii.Enabled = options.Gliner.Enabled;
            core.Privacy.DebertaInjection.Enabled = options.Deberta.Enabled;
            core.Privacy.Qwen3Guard.Enabled = options.Qwen3Guard.Enabled;
            core.Privacy.PaddleOcr.Enabled = options.PaddleOcr.Enabled;
            core.Privacy.YuNet.Enabled = options.YuNet.Enabled;
        });

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        var modalities = new HashSet<string>(StringComparer.Ordinal);
        if (options.Rules.Enabled) { capabilities.Add("security.rules"); modalities.Add("text"); }
        if (options.Gliner.Enabled) { capabilities.Add("security.gliner-pii"); modalities.Add("text"); }
        if (options.Deberta.Enabled) { capabilities.Add("security.deberta-injection"); modalities.Add("text"); }
        if (options.Qwen3Guard.Enabled) { capabilities.Add("security.qwen3-guard"); modalities.Add("text"); }
        if (options.PaddleOcr.Enabled) { capabilities.Add("security.paddle-ocr"); modalities.Add("image"); }
        if (options.YuNet.Enabled) { capabilities.Add("security.yunet"); modalities.Add("image"); }

        var readiness = capabilities.Count == 0
            ? WorkerReadiness.Disabled
            : options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable;

        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Security,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "security-unconfigured",
                readiness,
                capabilities,
                modalities,
                new HashSet<string>(["en"], StringComparer.OrdinalIgnoreCase),
                options.Rules.Enabled && capabilities.Count == 1 ? "rules-only" : "multi-detector",
                "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectClassificationWorker(this IServiceCollection services, Action<ClassificationWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ClassificationWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Classification, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<ClassificationWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Classification,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "classification-unconfigured",
                options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable,
                new HashSet<string>(["classification.advisory"], StringComparer.Ordinal),
                new HashSet<string>(["text"], StringComparer.Ordinal),
                new HashSet<string>(["en"], StringComparer.OrdinalIgnoreCase),
                options.ModelId ?? "classification-default",
                options.ModelRevision ?? "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectTranslationWorker(this IServiceCollection services, Action<TranslationWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new TranslationWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Nllb, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<TranslationWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        var readiness = !options.LicenseApproved || options.Endpoint is null
            ? WorkerReadiness.Unavailable
            : WorkerReadiness.Ready;
        var languages = options.AllowedLanguages.Count > 0
            ? new HashSet<string>(options.AllowedLanguages, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["eng_Latn", "fra_Latn"], StringComparer.OrdinalIgnoreCase);
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Nllb,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "nllb-unconfigured",
                readiness,
                new HashSet<string>(["translation.nllb"], StringComparer.Ordinal),
                new HashSet<string>(["text"], StringComparer.Ordinal),
                languages,
                options.ModelId ?? "facebook/nllb-200-distilled-600M",
                options.ModelRevision ?? "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectInferenceWorker(this IServiceCollection services, Action<InferenceWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new InferenceWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Inference, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<InferenceWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Inference,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "inference-unconfigured",
                options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable,
                new HashSet<string>(["inference.chat"], StringComparer.Ordinal),
                new HashSet<string>(["text"], StringComparer.Ordinal),
                new HashSet<string>(["en"], StringComparer.OrdinalIgnoreCase),
                options.ModelId ?? "local-inference",
                options.ModelRevision ?? "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectTranscriptionWorker(this IServiceCollection services, Action<TranscriptionWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new TranscriptionWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Transcription, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<TranscriptionWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Transcription,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "transcription-unconfigured",
                options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable,
                new HashSet<string>(["voice.stt"], StringComparer.Ordinal),
                new HashSet<string>(["audio"], StringComparer.Ordinal),
                new HashSet<string>(["en"], StringComparer.OrdinalIgnoreCase),
                options.ModelId ?? "whisper-local",
                options.ModelRevision ?? "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectRealtimeVoiceWorker(this IServiceCollection services, Action<RealtimeVoiceWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RealtimeVoiceWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.RealtimeVoice, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<RealtimeVoiceWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.RealtimeVoice,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "realtime-voice-unconfigured",
                options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable,
                new HashSet<string>(["voice.realtime-coordination"], StringComparer.Ordinal),
                new HashSet<string>(["audio"], StringComparer.Ordinal),
                new HashSet<string>(["en"], StringComparer.OrdinalIgnoreCase),
                options.ModelId,
                options.ModelRevision,
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectKokoroWorker(this IServiceCollection services, Action<KokoroWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new KokoroWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.Kokoro, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<KokoroWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Kokoro,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "kokoro-unconfigured",
                options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable,
                new HashSet<string>(["voice.tts.kokoro"], StringComparer.Ordinal),
                new HashSet<string>(["audio"], StringComparer.Ordinal),
                new HashSet<string>(["en-US"], StringComparer.OrdinalIgnoreCase),
                options.DefaultModel,
                options.ModelRevision ?? options.DefaultVoice,
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectMmsTtsWorker(this IServiceCollection services, Action<MmsTtsWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new MmsTtsWorkerOptions();
        configure?.Invoke(options);
        ValidateWorkerEngineAndTransport(OpenConnectWorkerKind.MmsTts, options.Engine, options.Environment, options.Endpoint, options.RequireAuthenticatedTransport);
        services.AddOptions<MmsTtsWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        var readiness = !options.LicenseApproved || options.Endpoint is null
            ? WorkerReadiness.Unavailable
            : WorkerReadiness.Ready;
        var languages = options.Languages.Count > 0
            ? new HashSet<string>(options.Languages, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["eng"], StringComparer.OrdinalIgnoreCase);
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.MmsTts,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "mms-tts-unconfigured",
                readiness,
                new HashSet<string>(["voice.tts.mms"], StringComparer.Ordinal),
                new HashSet<string>(["audio"], StringComparer.Ordinal),
                languages,
                options.ModelId ?? "facebook/mms-tts",
                options.ModelRevision ?? "v1",
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectPlaywrightWorker(this IServiceCollection services, Action<PlaywrightWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new PlaywrightWorkerOptions();
        configure?.Invoke(options);
        if (!options.RequireApprovedActions)
            throw new InvalidOperationException("Playwright worker requires RequireApprovedActions = true.");
        services.AddOptions<PlaywrightWorkerOptions>().Configure(o => configure?.Invoke(o)).ValidateOnStart();
        var readiness = !options.Enabled
            ? WorkerReadiness.Disabled
            : options.Endpoint is not null ? WorkerReadiness.Ready : WorkerReadiness.Unavailable;
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.NavigatorPlaywright,
                DefaultContractVersion,
                "local-worker",
                options.Endpoint?.ToString() ?? "navigator-playwright-unconfigured",
                readiness,
                new HashSet<string>(["navigator.playwright"], StringComparer.Ordinal),
                new HashSet<string>(["dom", "image"], StringComparer.Ordinal),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectOperationalAgents(this IServiceCollection services, Action<OperationalAgentsWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OperationalAgentsWorkerOptions();
        configure?.Invoke(options);
        EnforceHostingModeExclusivity(services, OpenConnectWorkerKind.OperationalAgents, options.HostingMode, options.StandaloneDeployed);
        services.AddOptions<OperationalAgentsWorkerOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => !(o.StandaloneDeployed && o.HostingMode == WorkerHostingMode.Embedded),
                $"{ErrorCodes.WorkerHostingConflict}: Embedded operational agents must be disabled when standalone operational worker is deployed.")
            .ValidateOnStart();
        var readiness = options.HostingMode == WorkerHostingMode.Disabled
            ? WorkerReadiness.Disabled
            : WorkerReadiness.Ready;
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.OperationalAgents,
                DefaultContractVersion,
                options.HostingMode == WorkerHostingMode.Embedded ? "embedded-host" : "standalone-worker",
                options.HostId,
                readiness,
                new HashSet<string>(["operational.supervisor"], StringComparer.Ordinal),
                new HashSet<string>(["control"], StringComparer.Ordinal),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    public static IServiceCollection AddOpenConnectOutbox(this IServiceCollection services, Action<OpenConnectOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OpenConnectOutboxOptions();
        configure?.Invoke(options);
        EnforceHostingModeExclusivity(services, OpenConnectWorkerKind.Outbox, options.HostingMode, options.StandaloneDeployed);
        services.AddOptions<OpenConnectOutboxOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => !(o.StandaloneDeployed && o.HostingMode == WorkerHostingMode.Embedded),
                $"{ErrorCodes.WorkerHostingConflict}: Embedded outbox dispatcher must be disabled when standalone outbox worker is deployed.")
            .ValidateOnStart();
        var readiness = options.HostingMode == WorkerHostingMode.Disabled
            ? WorkerReadiness.Disabled
            : WorkerReadiness.Ready;
        return RegisterWorkerCapability(
            services,
            new WorkerCapabilityDescriptor(
                OpenConnectWorkerKind.Outbox,
                DefaultContractVersion,
                options.HostingMode == WorkerHostingMode.Embedded ? "embedded-host" : "standalone-worker",
                options.HostId,
                readiness,
                new HashSet<string>(["transport.outbox"], StringComparer.Ordinal),
                new HashSet<string>(["events"], StringComparer.Ordinal),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static IServiceCollection RegisterWorkerCapability(IServiceCollection services, WorkerCapabilityDescriptor descriptor)
    {
        var registry = GetOrAddWorkerRegistry(services);
        registry.Register(descriptor);
        return services;
    }

    private static WorkerCapabilityRegistry GetOrAddWorkerRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(WorkerCapabilityRegistry))?.ImplementationInstance as WorkerCapabilityRegistry;
        if (existing is not null)
            return existing;

        var capDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICapabilityRegistry));
        CapabilityRegistry? capInstance = capDescriptor?.ImplementationInstance as CapabilityRegistry;
        if (capInstance is null)
        {
            capInstance = new CapabilityRegistry();
            services.Replace(ServiceDescriptor.Singleton<ICapabilityRegistry>(capInstance));
        }

        var instance = new WorkerCapabilityRegistry(TimeProvider.System, capInstance);
        services.Replace(ServiceDescriptor.Singleton(instance));
        services.Replace(ServiceDescriptor.Singleton<IWorkerCapabilityRegistry>(instance));
        return instance;
    }

    private static CoreToolRegistry GetOrAddCoreToolRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(CoreToolRegistry))?.ImplementationInstance as CoreToolRegistry;
        if (existing is not null)
            return existing;

        var instance = new CoreToolRegistry();
        services.AddSingleton(instance);
        services.TryAddSingleton<IToolCatalog>(instance);
        return instance;
    }

    private static McpServiceRegistry GetOrAddMcpRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(McpServiceRegistry))?.ImplementationInstance as McpServiceRegistry;
        if (existing is not null)
            return existing;

        var instance = new McpServiceRegistry();
        services.Replace(ServiceDescriptor.Singleton(instance));
        return instance;
    }

    private static void ValidateWorkerEngineAndTransport(
        OpenConnectWorkerKind kind,
        WorkerEngine engine,
        string environment,
        Uri? endpoint,
        bool requireAuthenticatedTransport)
    {
        if (engine == WorkerEngine.Fake && string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.WorkerUnavailable}: Worker '{kind}' cannot use WorkerEngine.Fake in Production environment.");
        }

        if (endpoint is not null && requireAuthenticatedTransport &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.WorkerUnavailable}: Worker '{kind}' endpoint must use an approved HTTP(S) transport scheme.");
        }
    }

    private sealed record WorkerHostingMarker(OpenConnectWorkerKind Kind, WorkerHostingMode Mode);

    private static void EnforceHostingModeExclusivity(
        IServiceCollection services,
        OpenConnectWorkerKind kind,
        WorkerHostingMode mode,
        bool standaloneDeployed)
    {
        if (standaloneDeployed && mode == WorkerHostingMode.Embedded)
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.WorkerHostingConflict}: Worker '{kind}' cannot run in Embedded mode when a Standalone container is deployed.");
        }

        var existing = services
            .Where(d => d.ServiceType == typeof(WorkerHostingMarker))
            .Select(d => d.ImplementationInstance as WorkerHostingMarker)
            .FirstOrDefault(m => m is not null && m.Kind == kind);

        if (existing is not null && existing.Mode != mode &&
            existing.Mode != WorkerHostingMode.Disabled && mode != WorkerHostingMode.Disabled)
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.WorkerHostingConflict}: Worker '{kind}' cannot be registered as both '{existing.Mode}' and '{mode}'.");
        }

        if (existing is null)
            services.AddSingleton(new WorkerHostingMarker(kind, mode));
    }

    private static void EnsureSingleHostFirewall(IServiceCollection services, PrivacyOptions? corePrivacy)
    {
        var extType = Type.GetType("OpenConnect.Privacy.PrivacyServiceCollectionExtensions, OpenConnect.Privacy", throwOnError: false);
        var method = extType?.GetMethod("EnsureHostPrivacyServices", BindingFlags.Public | BindingFlags.Static);
        if (method is null)
            return;

        if (corePrivacy is null)
        {
            method.Invoke(null, [services, null]);
            return;
        }

        var privacyOptionsType = Type.GetType("OpenConnect.Privacy.PrivacyOptions, OpenConnect.Privacy", throwOnError: false);
        if (privacyOptionsType is null)
        {
            method.Invoke(null, [services, null]);
            return;
        }

        var enabledProp = privacyOptionsType.GetProperty("Enabled");
        var modeProp = privacyOptionsType.GetProperty("Mode");
        var egressProp = privacyOptionsType.GetProperty("RequireEgressValidation");
        var tokenProp = privacyOptionsType.GetProperty("EnableTokenization");
        var outputProp = privacyOptionsType.GetProperty("EnableOutputInspection");

        var param = System.Linq.Expressions.Expression.Parameter(privacyOptionsType, "o");
        var body = System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Assign(
                System.Linq.Expressions.Expression.Property(param, enabledProp!),
                System.Linq.Expressions.Expression.Constant(corePrivacy.Enabled)),
            System.Linq.Expressions.Expression.Assign(
                System.Linq.Expressions.Expression.Property(param, modeProp!),
                System.Linq.Expressions.Expression.Constant(corePrivacy.Mode)),
            System.Linq.Expressions.Expression.Assign(
                System.Linq.Expressions.Expression.Property(param, egressProp!),
                System.Linq.Expressions.Expression.Constant(corePrivacy.RequireEgressValidation)),
            System.Linq.Expressions.Expression.Assign(
                System.Linq.Expressions.Expression.Property(param, tokenProp!),
                System.Linq.Expressions.Expression.Constant(corePrivacy.EnableTokenization)),
            System.Linq.Expressions.Expression.Assign(
                System.Linq.Expressions.Expression.Property(param, outputProp!),
                System.Linq.Expressions.Expression.Constant(corePrivacy.EnableOutputInspection)));
        var actionType = typeof(Action<>).MakeGenericType(privacyOptionsType);
        var configureDelegate = System.Linq.Expressions.Expression.Lambda(actionType, body, param).Compile();
        method.Invoke(null, [services, configureDelegate]);
    }

    private static void ThrowOnConflictingRepeatRegistration(IServiceCollection services, Action<OpenConnectOptions>? configure)
    {
        // A null configure delegate cannot introduce a conflict; treat as idempotent.
        if (configure is null)
            return;
        var registered = services
            .FirstOrDefault(d => d.ServiceType == typeof(OpenConnectRegistrationSnapshot))?
            .ImplementationInstance as OpenConnectRegistrationSnapshot;
        if (registered is null)
            return;
        var conflict = OptionsConflictGuard.FindConflict(registered, OptionsConflictGuard.Capture(Materialize(configure)));
        if (conflict.HasValue)
            throw OptionsConflictGuard.ConflictException(conflict.Value.Field, conflict.Value.OldValue, conflict.Value.NewValue);
    }

    private static OpenConnectOptions Materialize(Action<OpenConnectOptions>? configure)
    {
        var options = new OpenConnectOptions();
        configure?.Invoke(options);
        return options;
    }

    private sealed class PrivacyWarningConfiguration : IPostConfigureOptions<OpenConnectOptions>
    {
        public void PostConfigure(string? name, OpenConnectOptions options)
        {
            if (!options.Privacy.Enabled)
                System.Diagnostics.Trace.TraceWarning("OpenConnect privacy enforcement is explicitly disabled. Authentication and authorization remain enabled.");
        }
    }
}

public sealed class CoreToolRegistry : IToolCatalog
{
    private readonly ConcurrentDictionary<ToolId, ToolDescriptor> _tools = new();

    public void RegisterDescriptor(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _tools[descriptor.Id] = descriptor;
    }

    public ToolDescriptor? Get(ToolId toolId) => _tools.GetValueOrDefault(toolId);

    public IReadOnlyList<ToolDescriptor> GetForAgent(AgentId agentId) =>
        _tools.Values.Where(t => t.AgentId == agentId).OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<ToolDescriptor> GetAuthorized(AgentId agentId, PrincipalId principal, TenantId tenant)
    {
        if (principal.Value == default || tenant.Value == default)
            return Array.Empty<ToolDescriptor>();
        return GetForAgent(agentId);
    }
}

public sealed class McpServiceRegistry
{
    private readonly ConcurrentDictionary<string, OpenConnectMcpServiceOptions> _services = new(StringComparer.Ordinal);

    public void Register(OpenConnectMcpServiceOptions service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(service.Name);
        _services[service.Name] = service;
    }

    public OpenConnectMcpServiceOptions? Get(string name) => _services.GetValueOrDefault(name);

    public IReadOnlyList<OpenConnectMcpServiceOptions> GetAll() => _services.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    public bool IsToolAllowed(string serviceName, string toolId, PrincipalId principal, TenantId tenant)
    {
        if (principal.Value == default || tenant.Value == default)
            return false;
        return _services.TryGetValue(serviceName, out var service) && service.AllowedTools.Contains(toolId);
    }
}

public sealed class AgentDefinitionRegistry : IAgentDefinitionRegistry
{
    private readonly Dictionary<AgentId, AgentDefinition> _agents = new();
    private readonly object _gate = new();

    public AgentDefinition? Get(AgentId agentId)
    {
        lock (_gate) return _agents.GetValueOrDefault(agentId);
    }

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        lock (_gate) return _agents.Values.ToArray();
    }

    public void Register(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_agents.TryAdd(definition.Id, definition))
                throw new InvalidOperationException($"Agent '{definition.Id.Value}' is already registered.");
        }
    }
}

public static class OpenConnectLeadAgent
{
    public static readonly AgentDefinition Definition = new(new AgentId("lead"), "Lead");
}
