using System.Reflection;

namespace OpenConnect.Agents.Execution;

/// <summary>Inspects the loaded Microsoft Agent Framework assemblies without assuming a package API.</summary>
public static class MafRuntimeProbe
{
    private static readonly (string Capability, string TypeName)[] CapabilityTypes =
    [
        ("Workflow", "Microsoft.Agents.AI.Workflows.WorkflowBuilder"),
        ("Workflow execution", "Microsoft.Agents.AI.Workflows.Workflow"),
        ("Checkpoint", "Microsoft.Agents.AI.Workflows.Checkpoint"),
        ("Human-in-the-loop", "Microsoft.Agents.AI.Workflows.HumanInTheLoopRequest"),
        ("Conflict handling", "Microsoft.Agents.AI.Workflows.WorkflowConflictException"),
        // Harness execution is provided by the referenced Microsoft.Agents.AI.Harness
        // 1.27.0 package. Verified present on 2026-11-09 by reflection over the loaded
        // assembly (3 exported types): the canonical agent type below
        // (Microsoft.Agents.AI.HarnessAgent : DelegatingAIAgent), HarnessAgentOptions,
        // and the ChatClientHarnessExtensions.AsHarnessAgent factory.
        ("Harness", "Microsoft.Agents.AI.HarnessAgent"),
        // No "Durable"-named type or member exists in MAF 1.27.0; there is no durable backend package
        // referenced. The name below is the expected durable workflow host type; absent means the
        // DurableWorkflow profile must fail closed until a durable backend is selected.
        ("Durable execution", "Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor"),
        // MCP/A2A integration lives in external SDK packages (not referenced, never auto-loaded).
        // The names below are the documented client types of those SDKs; absent means those profiles
        // must fail closed.
        ("MCP integration", "ModelContextProtocol.Client.McpClient"),
        ("A2A integration", "A2A.A2AClient")
    ];

    /// <summary>Reports workflow-related types present in the actual loaded MAF assemblies.</summary>
    public static IReadOnlyList<MafCapabilityProbe> GetWorkflowCapabilityMatrix()
    {
        var assemblies = LoadMafAssemblies();
        var types = assemblies.SelectMany(GetLoadableTypes)
            .Where(type => type.FullName is not null)
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);

        return CapabilityTypes.Select(candidate =>
        {
            if (types.TryGetValue(candidate.TypeName, out var type))
            {
                return new MafCapabilityProbe(candidate.Capability, MafCapabilityStatus.Available,
                    candidate.TypeName, null, type.Assembly.GetName().Name);
            }

            return new MafCapabilityProbe(candidate.Capability, MafCapabilityStatus.Unavailable,
                candidate.TypeName, $"Missing type: {candidate.TypeName}", null);
        }).ToArray();
    }

    /// <summary>Returns exported types from MAF assemblies, including declared public methods.</summary>
    public static IReadOnlyList<string> GetAgentApiInventory()
        => LoadMafAssemblies()
            .SelectMany(GetLoadableTypes)
            .Where(type => type.FullName is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type =>
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(method => method.ToString())
                    .Order(StringComparer.Ordinal);
                return $"{type.Assembly.GetName().Name}: {type.FullName}{Environment.NewLine}" + string.Join(Environment.NewLine, methods);
            })
            .ToArray();

    private static Assembly[] LoadMafAssemblies()
    {
        // Best-effort load of the MAF assemblies so workflow-namespace
        // types (Microsoft.Agents.AI.Workflows.*) and harness-namespace
        // types resolve when the corresponding packages are referenced. A missing
        // assembly is ignored so the capability matrix reports Unavailable
        // (fail closed) instead of throwing here.
        foreach (var name in new[] { "Microsoft.Agents.AI", "Microsoft.Agents.AI.Abstractions", "Microsoft.Agents.AI.Workflows", "Microsoft.Agents.AI.Harness" })
        {
            try { _ = Assembly.Load(name); }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "Microsoft.Agents.AI" or "Microsoft.Agents.AI.Abstractions" or "Microsoft.Agents.AI.Workflows" or "Microsoft.Agents.AI.Harness")
            .ToArray();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); }
    }
}

/// <summary>Result of probing one runtime capability.</summary>
public sealed record MafCapabilityProbe(string Capability, MafCapabilityStatus Status, string RequiredTypeName,
    string? UnavailableReason, string? AssemblyName);

/// <summary>Availability of a capability in the loaded MAF runtime.</summary>
public enum MafCapabilityStatus
{
    /// <summary>The required runtime type was found in a loaded MAF assembly.</summary>
    Available,

    /// <summary>The required runtime type was not found in the loaded MAF assemblies.</summary>
    Unavailable
}
