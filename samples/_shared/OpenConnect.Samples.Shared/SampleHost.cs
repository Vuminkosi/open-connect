using Microsoft.Extensions.Hosting;

namespace OpenConnect.Samples.Shared;

/// <summary>
/// Builds a generic host for samples: JSON settings (optional) plus environment variables.
/// No provider is registered here; each sample registers its own DbContext and exactly one provider.
/// </summary>
public static class SampleHost
{
    /// <summary>
    /// Creates an application builder wired to <c>appsettings.json</c> (optional) and environment variables.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A host application builder.</returns>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        return builder;
    }
}
