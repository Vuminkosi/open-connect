using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Loads the build-generated manifest embedded in the consuming application assembly.</summary>
public static class LiveUICompiledManifestLoader
{
    private readonly record struct ManifestCacheKey(string AssemblyName, Guid ModuleVersionId, string ResourceName);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ManifestCacheKey, Lazy<LiveUIManifest?>> Cache = new();

    public static LiveUIManifest? LoadFromEntryAssembly(Assembly? applicationAssembly = null)
    {
        var assembly = applicationAssembly ?? Assembly.GetEntryAssembly();
        if (assembly is null) return null;

        var resourceName = $"{assembly.GetName().Name}.openconnect.liveui.manifest.json";
        var cacheKey = new ManifestCacheKey(assembly.GetName().Name ?? string.Empty,
            assembly.ManifestModule.ModuleVersionId, resourceName);
        return Cache.GetOrAdd(cacheKey, _ => new Lazy<LiveUIManifest?>(
            () => LoadAndValidate(assembly, resourceName), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static LiveUIManifest? LoadAndValidate(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        var manifest = JsonSerializer.Deserialize<LiveUIManifest>(stream, LiveUIManifestJson.CreateSerializerOptions());
        if (manifest is null) return null;

        var errors = new LiveUIManifestValidator().Validate(manifest);
        if (errors.Count != 0)
            throw new InvalidOperationException($"The embedded LiveUI manifest is invalid ({errors[0]}).");

        var routes = manifest.Routes.Select(route => route with
        {
            Targets = Array.AsReadOnly(route.Targets.Select(target => target with
            {
                Capabilities = target.Capabilities.ToFrozenSet()
            }).ToArray())
        }).ToArray();
        return manifest with { Routes = Array.AsReadOnly(routes) };
    }
}
