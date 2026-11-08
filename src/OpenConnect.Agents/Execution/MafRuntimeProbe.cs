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
        ("Conflict handling", "Microsoft.Agents.AI.Workflows.WorkflowConflictException")
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
        _ = Assembly.Load("Microsoft.Agents.AI");
        _ = Assembly.Load("Microsoft.Agents.AI.Abstractions");
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "Microsoft.Agents.AI" or "Microsoft.Agents.AI.Abstractions")
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
