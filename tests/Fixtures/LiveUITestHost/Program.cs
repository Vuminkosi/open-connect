using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.LiveUI;
using OpenConnect.Samples.Blazor;

// This executable is a disposable acceptance fixture. It must never be used as a deployment host.
if (Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_TEST_HOST") != "1")
    throw new InvalidOperationException("The LiveUI test host requires explicit test opt-in.");

if (!int.TryParse(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_TEST_PORT"), out var port) || port is < 1024 or > 65535)
    throw new InvalidOperationException("The LiveUI test host requires a valid test port.");

var databasePath = Environment.GetEnvironmentVariable("OPENCONNECT_BLAZOR_SQLITE_PATH");
var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
if (string.IsNullOrWhiteSpace(databasePath) ||
    !Path.GetFullPath(databasePath).StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new InvalidOperationException("The LiveUI test host requires a disposable database under the temporary directory.");

await using var factory = new AcceptanceFactory();
factory.UseKestrel(options => options.ListenLocalhost(port));
factory.StartServer();
using (var scope = factory.Services.CreateScope())
{
    if (scope.ServiceProvider.GetRequiredService<ILiveUIActionAuthorizer>() is not AcceptanceAuthorizer)
        throw new InvalidOperationException("The test-only authorization override was not installed.");
}
await Task.Delay(Timeout.InfiniteTimeSpan);

internal sealed class AcceptanceFactory : WebApplicationFactory<BlazorDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "samples", "Blazor")));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILiveUIActionAuthorizer>();
            services.AddScoped<ILiveUIActionAuthorizer, AcceptanceAuthorizer>();
        });
    }
}

internal sealed class AcceptanceAuthorizer : ILiveUIActionAuthorizer
{
    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "acceptance_fixture"));
    }
}
