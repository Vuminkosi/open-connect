using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Client;

namespace OpenConnect.Samples.Blazor;

public enum TodoGatewayMode
{
    ExternalGateway,
    SameHostGateway
}

public sealed class TodoGatewayOptions
{
    public TodoGatewayMode Mode { get; init; } = TodoGatewayMode.ExternalGateway;
    public string TenantId { get; init; } = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    public string PrincipalId { get; init; } = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
    public string SameHostBaseUrl { get; init; } = "http://127.0.0.1:5000";

    public static TodoGatewayOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var modeText = configuration["OpenConnect:Gateway:Mode"] ?? "ExternalGateway";
        if (!Enum.TryParse<TodoGatewayMode>(modeText, true, out var mode))
            throw new InvalidOperationException($"OpenConnect:Gateway:Mode '{modeText}' is invalid.");
        if (mode == TodoGatewayMode.SameHostGateway && !environment.IsDevelopment())
            throw new InvalidOperationException("SameHostGateway is development-only until production authentication and credential propagation are configured.");
        var options = new TodoGatewayOptions
        {
            Mode = mode,
            TenantId = configuration["OpenConnect:Gateway:TenantId"] ?? "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            PrincipalId = configuration["OpenConnect:Gateway:PrincipalId"] ?? "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            SameHostBaseUrl = configuration["OpenConnect:Gateway:SameHostBaseUrl"] ?? "http://127.0.0.1:5000"
        };
        try
        {
            _ = Ulid.Parse(options.TenantId);
            _ = Ulid.Parse(options.PrincipalId);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("SameHostGateway tenant and principal IDs must be canonical ULIDs.");
        }
        return options;
    }
}

public sealed class SameHostDevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<TodoGatewayOptions> gatewayOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OpenConnectTodoDevelopment";
    private readonly TodoGatewayOptions _gatewayOptions = gatewayOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("tenant_id", _gatewayOptions.TenantId),
            new Claim(ClaimTypes.NameIdentifier, _gatewayOptions.PrincipalId),
            new Claim("sub", _gatewayOptions.PrincipalId),
            new Claim("identity_mode", "interactive")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class SameHostIdentityHandler(IOptions<TodoGatewayOptions> options) : DelegatingHandler
{
    private readonly TodoGatewayOptions _options = options.Value;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-OpenConnect-Test-Tenant", _options.TenantId);
        request.Headers.TryAddWithoutValidation("X-OpenConnect-Test-Principal", _options.PrincipalId);
        return base.SendAsync(request, cancellationToken);
    }
}
