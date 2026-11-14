using System.Reflection;
using System.Text.Json;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Loads the build-generated manifest embedded in the consuming application assembly.</summary>
public static class LiveUICompiledManifestLoader
{
    public static LiveUIManifest? LoadFromEntryAssembly(Assembly? applicationAssembly = null)
    {
        var assembly = applicationAssembly ?? Assembly.GetEntryAssembly();
        if (assembly is null) return null;

        var resourceName = $"{assembly.GetName().Name}.openconnect.liveui.manifest.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : JsonSerializer.Deserialize<LiveUIManifest>(stream, LiveUIManifestJson.CreateSerializerOptions());
    }
}
