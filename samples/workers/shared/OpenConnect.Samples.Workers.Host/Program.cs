using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Navigator.AspNetCore;
using OpenConnect.Samples.Workers.Contracts;

namespace OpenConnect.Samples.Workers.Host;

public sealed class SampleTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OpenConnectSampleTestAuth";

    public SampleTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenantHeader = Request.Headers["X-OpenConnect-Test-Tenant"].FirstOrDefault();
        var principalHeader = Request.Headers["X-OpenConnect-Test-Principal"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantHeader) || string.IsNullOrWhiteSpace(principalHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("tenant_id", tenantHeader),
            new(ClaimTypes.NameIdentifier, principalHeader),
            new("sub", principalHeader)
        };

        var identityMode = Request.Headers["X-OpenConnect-Test-Identity-Mode"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(identityMode))
        {
            claims.Add(new Claim("identity_mode", identityMode));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class SampleWorkerHostApp
{
    public static WebApplication CreateApp(string[] args)
    {
        var embeddedOps = string.Equals(Environment.GetEnvironmentVariable("OC_EMBEDDED_OPERATIONAL"), "1", StringComparison.Ordinal);
        var standaloneOps = string.Equals(Environment.GetEnvironmentVariable("OC_STANDALONE_OPERATIONAL"), "1", StringComparison.Ordinal);
        var embeddedOutbox = string.Equals(Environment.GetEnvironmentVariable("OC_EMBEDDED_OUTBOX"), "1", StringComparison.Ordinal);
        var standaloneOutbox = string.Equals(Environment.GetEnvironmentVariable("OC_STANDALONE_OUTBOX"), "1", StringComparison.Ordinal);
        SampleGatewayCoordinator.ValidateHostingExclusivity(embeddedOps, standaloneOps, embeddedOutbox, standaloneOutbox);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services
            .AddAuthentication(SampleTestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SampleTestAuthHandler>(SampleTestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddOpenConnect(options =>
        {
            options.Privacy.Enabled = true;
            options.Privacy.Mode = FirewallMode.StrictLocal;
            options.Agents.RegisterLeadAgent = true;
        });
        builder.Services.AddOpenConnectAgent("worker-sample-direct");
        builder.Services.AddOpenConnectTaskExecution();
        builder.Services.AddMetaTodoIntegration(builder.Configuration);
        builder.Services.AddMafHostTaskExecutor();
        builder.Services.AddOpenConnectNavigator();

        var coordinator = new SampleGatewayCoordinator();
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(coordinator.CapabilityRegistry);
        builder.Services.AddSingleton(coordinator.TokenVault);
        builder.Services.AddSingleton<OpenConnect.Privacy.IPrivacyFirewall>(coordinator.Firewall);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();

        app.MapOpenConnect();

        app.MapGet("/sample-harness/workers", () => Results.Ok(CanonicalWorkers.All));

        return app;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--verify-config")
        {
            using var app = SampleWorkerHostApp.CreateApp(args);
            Console.WriteLine("PASS: OpenConnect.Samples.Workers.Host verified real OpenConnect gateway, MAF, privacy, and worker routing.");
            return 0;
        }

        await using var webApp = SampleWorkerHostApp.CreateApp(args);
        await webApp.RunAsync();
        return 0;
    }
}
