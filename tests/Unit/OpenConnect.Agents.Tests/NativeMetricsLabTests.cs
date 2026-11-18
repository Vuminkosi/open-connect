// <copyright file="NativeMetricsLabTests.cs">
// MAF-native metrics/ActivitySources audit lab (AG-IMPL-018, OC2-D-MAF-020).
// Investigate-first result (read-only reflection + IL scan over local NuGet
// cache, MAF 1.27.0 / Microsoft.Extensions.AI 10.10.0, net8.0 assets):
//   REUSE  Experimental.Microsoft.Agents.AI      ActivitySource, created in
//            CompactionTelemetry..cctor (Microsoft.Agents.AI.dll); agent runs
//            start a "chat" activity carrying gen_ai.operation.name=invoke_agent
//            plus gen_ai.agent.* / gen_ai.provider.* tags. OBSERVED below.
//   REUSE-capable, ABSENT-by-default (exact missing-hook evidence):
//            Microsoft.Agents.AI.Workflows         ActivitySource, created in
//            WorkflowTelemetryContext..cctor, operations workflow.build /
//            workflow.session / workflow_invoke / executor.process /
//            edge_group.process / message.send with OTel propagation. BUT the
//            context type is internal, Workflow.TelemetryContext has an
//            internal getter, and the only public enablement path
//            (OpenTelemetryWorkflowBuilderExtensions.WithOpenTelemetry)
//            targets the non-generic WorkflowBuilder hierarchy, which no
//            shipped builder (Sequential/Concurrent/Handoff/GroupChat/Magentic
//            via AgentWorkflowBuilder) derives from: WorkflowTelemetryContext
//            instances are newed only by WithOpenTelemetry and the static
//            Disabled singleton. Hence no deterministic native operation via
//            existing runners can light up this source on 1.27.0. The workflow
//            test below encodes this as a fail-closed negative probe: the
//            native run must succeed AND emit zero activities on this source.
//            If a future MAF enables it, the probe fails and the verdict must
//            be re-proven by observing the new activities (resumption: switch
//            the probe to assert presence + tag names).
//   REUSE  Experimental.Microsoft.Extensions.AI  ActivitySource AND Meter
//            (same name for both), created per OpenTelemetry* wrapper ctor
//            (Chat/Image/Embedding/HostedFile/Realtime/STT/TTS) in
//            Microsoft.Extensions.AI 10.10.0; gen_ai.* tags plus the
//            gen_ai.client.operation.duration histogram (and siblings).
//            OBSERVED below.
//   REUSE-as-interface (ABSENT as instrumentation):
//            Microsoft.Extensions.AI.Evaluation (IEvaluator, EvaluationMetric,
//            EvaluationResult, EvaluationContext) and MAF IAgentEvaluator /
//            AgentEvaluationResults (Microsoft.Agents.AI.dll): no
//            ActivitySource/Meter/DiagnosticListener memberrefs in those
//            assemblies (only Stopwatch timing).
//   ABSENT Microsoft.Agents.AI.Harness + Microsoft.Agents.AI.Abstractions:
//            no ActivitySource/Meter/DiagnosticListener memberrefs.
//   ABSENT DiagnosticSource event names: no DiagnosticListener /
//            DiagnosticSource instrumentation memberrefs in any scanned
//            Microsoft.Agents.AI* / Microsoft.Extensions.AI(*) assembly.
// This lab subscribes an ActivityListener/MeterListener to each REUSE source,
// runs deterministic native operations (stub IChatClient, no network, bounded
// waits), asserts the native events/measurements are observed (or, for the
// workflows source, documents with a passing probe that the default path is
// dark), and asserts OpenConnect defines no same-named source (no duplicate
// instrumentation, no double-counted cost ledger per AG2-EVAL-002).
// </copyright>

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Executable proof that OpenConnect reuses (subscribes to) MAF-native
/// instrumentation instead of duplicating it. Deterministic stubs only.
/// </summary>
public sealed class NativeMetricsLabTests
{
    private const string AgentsSourceName = "Experimental.Microsoft.Agents.AI";
    private const string WorkflowsSourceName = "Microsoft.Agents.AI.Workflows";
    private const string MeaiSourceName = "Experimental.Microsoft.Extensions.AI";

    // Grep-derived OpenConnect-owned source inventory (2026-11-09, master):
    //   src/OpenConnect.Observability/Instrumentation.cs: "OpenConnect.Metering",
    //     "OpenConnect.Vectors" (ActivitySources) + Meter "OpenConnect",
    //     Meter "OpenConnect.Vectors".
    //   src/OpenConnect.Client/OpenConnectClient.cs: "OpenConnect.Client".
    //   src/OpenConnect.Navigator/Diagnostics/NavigatorActivitySource.cs:
    //     "OpenConnect.Navigator" (ActivitySource + Meter).
    // src/OpenConnect.Agents itself defines no ActivitySource/Meter.
    private static readonly string[] OpenConnectOwnedSourceNames =
    [
        "OpenConnect",
        "OpenConnect.Client",
        "OpenConnect.Metering",
        "OpenConnect.Vectors",
        "OpenConnect.Navigator",
    ];

    private readonly ITestOutputHelper _output;

    public NativeMetricsLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Native_agent_run_emits_Experimental_Microsoft_Agents_AI_activity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var observed = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentsSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        listener.ActivityStopped += activity => { lock (observed) { observed.Add(activity); } };
        ActivitySource.AddActivityListener(listener);

        var client = new LabStubChatClient();
        var options = new HarnessAgentOptions
        {
            Id = "lab-native-metrics",
            Name = "LabNativeMetrics",
            Description = "Deterministic native-metrics audit lab.",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = false,
        };
        HarnessAgent agent = client.AsHarnessAgent(options);
        AgentSession session = await agent.CreateSessionAsync(ct);
        AgentResponse response = await agent.RunAsync("lab-native-metrics-turn", session, cancellationToken: ct);

        Assert.StartsWith("ack:lab-native-metrics-turn", response.Text, StringComparison.Ordinal);
        List<Activity> stopped;
        lock (observed) { stopped = observed.ToList(); }
        _output.WriteLine($"Native agent run completed; activities on '{AgentsSourceName}': {stopped.Count}.");
        foreach (var activity in stopped)
        {
            _output.WriteLine($"  op={activity.OperationName} tags=[{string.Join(",", activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value))}]");
        }

        var invoke = Assert.Single(stopped, activity => activity.Source.Name == AgentsSourceName && string.Equals((string?)activity.GetTagItem("gen_ai.operation.name"), "invoke_agent", StringComparison.Ordinal));
        Assert.Equal("invoke_agent", invoke.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("lab-native-metrics", invoke.GetTagItem("gen_ai.agent.id"));
    }

    [Fact]
    public async Task Native_workflow_run_leaves_Workflows_source_dark_on_default_path()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var observed = new List<(string Source, string Operation)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkflowsSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        listener.ActivityStopped += activity =>
        {
            lock (observed) { observed.Add((activity.Source.Name, activity.OperationName)); }
        };
        ActivitySource.AddActivityListener(listener);

        var client = new LabStubChatClient();
        var agent = new MafRealAgentFactory(new LabRegistry(), client).CreateAgent(new AgentId("workflow-metrics"));
        // Shipped-builder path with no public telemetry opt-in (see file header):
        // the native run must succeed while emitting nothing on this source.
        // Assert.Empty is intentionally fail-closed: if a future MAF lights up
        // this source on the default path, this probe fails and the REUSE
        // verdict must be re-proven by observing the new activities.
        var workflow = AgentWorkflowBuilder.BuildSequential([agent]);
        var result = await new NativeWorkflowRunner().RunAgentTurnAsync(
            workflow,
            [new ChatMessage(ChatRole.User, "lab-workflow-metrics-turn")],
            ct);

        Assert.NotEmpty(result.Events);
        List<(string Source, string Operation)> stopped;
        lock (observed) { stopped = observed.ToList(); }
        _output.WriteLine($"Native workflow run completed with {result.Events.Count} events; activities on '{WorkflowsSourceName}': {stopped.Count} (expected 0: telemetry is opt-in only on MAF 1.27.0).");
        Assert.Empty(stopped);
    }

    [Fact]
    public async Task Native_meai_wrapper_emits_activity_and_meter_measurement()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var activities = new List<(string Source, string Operation)>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MeaiSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        activityListener.ActivityStopped += activity =>
        {
            lock (activities) { activities.Add((activity.Source.Name, activity.OperationName)); }
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new List<(string Meter, string Instrument)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MeaiSourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (measurements) { measurements.Add((instrument.Meter.Name, instrument.Name)); }
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            lock (measurements) { measurements.Add((instrument.Meter.Name, instrument.Name)); }
        });
        meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            lock (measurements) { measurements.Add((instrument.Meter.Name, instrument.Name)); }
        });
        meterListener.Start();

        var inner = new LabStubChatClient();
        using var monitored = new OpenTelemetryChatClient(inner);
        var response = await monitored.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "lab-meai-metrics-turn")], cancellationToken: ct);

        Assert.StartsWith("ack:lab-meai-metrics-turn", response.Text, StringComparison.Ordinal);

        List<(string Source, string Operation)> stoppedActivities;
        lock (activities) { stoppedActivities = activities.ToList(); }
        List<(string Meter, string Instrument)> observedMeasurements;
        lock (measurements) { observedMeasurements = measurements.ToList(); }
        _output.WriteLine($"MEAI wrapper call completed; activities on '{MeaiSourceName}': {stoppedActivities.Count}; measurements: {observedMeasurements.Count}.");
        foreach (var (source, operation) in stoppedActivities)
        {
            _output.WriteLine($"  activity source={source} op={operation}");
        }

        foreach (var (meter, instrument) in observedMeasurements.Distinct())
        {
            _output.WriteLine($"  measurement meter={meter} instrument={instrument}");
        }

        Assert.NotEmpty(stoppedActivities);
        Assert.All(stoppedActivities, entry => Assert.Equal(MeaiSourceName, entry.Source));
        Assert.NotEmpty(observedMeasurements);
        Assert.All(observedMeasurements, entry => Assert.Equal(MeaiSourceName, entry.Meter));
        Assert.Contains(observedMeasurements, entry => entry.Instrument == "gen_ai.client.operation.duration");
    }

    [Fact]
    public async Task Native_runs_publish_no_agents_or_meai_DiagnosticListeners()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var listenerNames = new List<string>();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(new RecordingListenerObserver(listenerNames));

        var client = new LabStubChatClient();
        HarnessAgent agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            Id = "lab-native-diagnostics",
            Name = "LabNativeDiagnostics",
            Description = "DiagnosticListener absence probe.",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = false,
        });
        AgentSession session = await agent.CreateSessionAsync(ct);
        await agent.RunAsync("lab-native-diagnostics-turn", session, cancellationToken: ct);

        List<string> names;
        lock (listenerNames) { names = listenerNames.Distinct(StringComparer.Ordinal).ToList(); }
        _output.WriteLine($"DiagnosticListeners observed during native runs: [{string.Join(", ", names)}].");
        Assert.DoesNotContain(names, name =>
            name.Contains("Agents.AI", StringComparison.Ordinal) ||
            name.Contains("Extensions.AI", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenConnect_defines_no_duplicate_native_source_names()
    {
        string[] nativeNames = [AgentsSourceName, WorkflowsSourceName, MeaiSourceName];
        _output.WriteLine($"Native sources (REUSE): {string.Join(", ", nativeNames)}.");
        _output.WriteLine($"OpenConnect-owned sources: {string.Join(", ", OpenConnectOwnedSourceNames)}.");

        foreach (var owned in OpenConnectOwnedSourceNames)
        {
            Assert.DoesNotContain(nativeNames, native => string.Equals(native, owned, StringComparison.Ordinal));
        }

        var runtimeOwned = CollectRuntimeSourceNames();
        _output.WriteLine($"Runtime scan of loaded OpenConnect.* assemblies found: [{string.Join(", ", runtimeOwned.OrderBy(name => name, StringComparer.Ordinal))}].");
        foreach (var name in runtimeOwned)
        {
            Assert.DoesNotContain(nativeNames, native => string.Equals(native, name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Evaluation_hooks_are_reusable_interfaces_without_native_sources()
    {
        var evaluationAssembly = Assembly.Load("Microsoft.Extensions.AI.Evaluation");
        _output.WriteLine($"Evaluation assembly: {evaluationAssembly.FullName}.");

        string[] hookNames =
        [
            "Microsoft.Extensions.AI.Evaluation.IEvaluator",
            "Microsoft.Extensions.AI.Evaluation.EvaluationMetric",
            "Microsoft.Extensions.AI.Evaluation.EvaluationResult",
            "Microsoft.Extensions.AI.Evaluation.EvaluationContext",
        ];
        foreach (var hookName in hookNames)
        {
            var hook = evaluationAssembly.GetType(hookName);
            _output.WriteLine($"Hook {hookName}: {(hook is null ? "ABSENT" : "present")}.");
            Assert.NotNull(hook);
        }

        var mafAssembly = Assembly.Load("Microsoft.Agents.AI");
        var mafAgentEvaluator = mafAssembly.GetType("Microsoft.Agents.AI.IAgentEvaluator");
        var mafAgentResults = mafAssembly.GetType("Microsoft.Agents.AI.AgentEvaluationResults");
        _output.WriteLine($"MAF IAgentEvaluator: {(mafAgentEvaluator is null ? "ABSENT" : "present")}.");
        _output.WriteLine($"MAF AgentEvaluationResults: {(mafAgentResults is null ? "ABSENT" : "present")}.");
        Assert.NotNull(mafAgentEvaluator);
        Assert.NotNull(mafAgentResults);

        var nativeSources = CollectSourceNames(evaluationAssembly);
        _output.WriteLine($"ActivitySource/Meter instances in Evaluation assembly: [{string.Join(", ", nativeSources)}] (expected empty: ABSENT as instrumentation).");
        Assert.Empty(nativeSources);
    }

    private static HashSet<string> CollectRuntimeSourceNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName is null || !assemblyName.StartsWith("OpenConnect.", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in CollectSourceNames(assembly))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<string> CollectSourceNames(Assembly assembly)
    {
        var names = new List<string>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            FieldInfo[] fields;
            PropertyInfo[] properties;
            try
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (var field in fields)
            {
                TryRecordSourceMember(names, () => field.GetValue(null));
            }

            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                TryRecordSourceMember(names, () => property.GetValue(null));
            }
        }

        return names;
    }

    private static void TryRecordSourceMember(List<string> names, Func<object?> read)
    {
        object? value;
        try
        {
            value = read();
        }
        catch
        {
            return;
        }

        switch (value)
        {
            case ActivitySource source:
                names.Add(source.Name);
                break;
            case Meter meter:
                names.Add(meter.Name);
                break;
            case System.Collections.IDictionary dictionary:
                foreach (var entry in dictionary.Values)
                {
                    switch (entry)
                    {
                        case ActivitySource innerSource:
                            names.Add(innerSource.Name);
                            break;
                        case Meter innerMeter:
                            names.Add(innerMeter.Name);
                            break;
                    }
                }

                break;
        }
    }

    private sealed class RecordingListenerObserver(List<string> names) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            lock (names) { names.Add(value.Name); }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class LabRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("workflow-metrics"), "WorkflowMetrics");

        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class LabStubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("native-metrics-lab");

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            var prompt = snapshot.LastOrDefault(message =>
                message.Role == ChatRole.User &&
                (message.Text ?? string.Empty).StartsWith("lab-", StringComparison.Ordinal))?.Text ?? "lab-ack";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack:" + prompt)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
