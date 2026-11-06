using System.Reflection;

namespace OpenConnect.Agents.Execution;

/// <summary>Inspects the pinned Microsoft Agent Framework runtime surface without guessing APIs.</summary>
public static class MafRuntimeProbe
{
    /// <summary>Returns exported types from MAF assemblies, including declared public methods for agent types.</summary>
    public static IReadOnlyList<string> GetAgentApiInventory()
    {
        _ = Assembly.Load("Microsoft.Agents.AI");
        _ = Assembly.Load("Microsoft.Agents.AI.Abstractions");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "Microsoft.Agents.AI" or "Microsoft.Agents.AI.Abstractions")
            .ToArray();

        return assemblies
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
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); }
    }
}
