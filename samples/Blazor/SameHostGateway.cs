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
    IOptions<TodoGatewayOptions> gatewayOptions,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OpenConnectTodoDevelopment";
    private readonly TodoGatewayOptions _gatewayOptions = gatewayOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. If a Bearer token is presented on messaging/devices/agents routes, validate it strictly
        //    as a device-bound mobile JWT (never silently fall back to desktop identity!).
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            var tokenIssuer = serviceProvider.GetService<Messaging.TodoMobileTokenIssuer>();
            if (tokenIssuer is not null)
            {
                var validation = await tokenIssuer.ValidateAccessTokenAsync(token, Context.RequestAborted).ConfigureAwait(false);
                if (!validation.Succeeded || validation.Principal is null)
                    return AuthenticateResult.Fail(validation.FailureReason ?? "Invalid mobile device token.");

                return AuthenticateResult.Success(new AuthenticationTicket(validation.Principal, SchemeName));
            }
        }

        // 2. Otherwise in Development, honor explicit test tenant/principal headers or dev persona cookie
        //    if they match a valid seeded principal; otherwise use configured SameHostGateway defaults.
        var tenantIdText = _gatewayOptions.TenantId;
        var principalIdText = _gatewayOptions.PrincipalId;
        string? role = null;
        string? displayName = null;

        var headerTenant = Request.Headers["X-OpenConnect-Test-Tenant"].FirstOrDefault();
        var headerPrincipal = Request.Headers["X-OpenConnect-Test-Principal"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerTenant) && !string.IsNullOrWhiteSpace(headerPrincipal))
        {
            if (TryParseUlid(headerTenant, out var tUlid) && TryParseUlid(headerPrincipal, out var pUlid))
            {
                tenantIdText = tUlid.ToString();
                principalIdText = pUlid.ToString();
                var seeded = Models.TodoDirectorySeed.FindPrincipal(new TenantId(tUlid), new PrincipalId(pUlid));
                role = seeded?.Role;
                displayName = seeded?.DisplayName;
            }
        }
        else if (Request.Cookies.TryGetValue("oc_dev_persona", out var personaKey) && !string.IsNullOrWhiteSpace(personaKey))
        {
            var seeded = Models.TodoDirectorySeed.FindByKey(personaKey);
            if (seeded is not null)
            {
                tenantIdText = seeded.TenantId.Value.ToString();
                principalIdText = seeded.PrincipalId.Value.ToString();
                role = seeded.Role;
                displayName = seeded.DisplayName;
            }
        }
        else if (TryParseUlid(tenantIdText, out var defT) && TryParseUlid(principalIdText, out var defP))
        {
            var seeded = Models.TodoDirectorySeed.FindPrincipal(new TenantId(defT), new PrincipalId(defP));
            role = seeded?.Role;
            displayName = seeded?.DisplayName;
        }

        var claims = new List<Claim>
        {
            new("tenant_id", tenantIdText),
            new(ClaimTypes.NameIdentifier, principalIdText),
            new("sub", principalIdText),
            new("identity_mode", "interactive")
        };
        if (!string.IsNullOrWhiteSpace(role))
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (!string.IsNullOrWhiteSpace(displayName))
            claims.Add(new Claim(ClaimTypes.Name, displayName));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static bool TryParseUlid(string value, out Ulid ulid)
    {
        try
        {
            ulid = Ulid.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            ulid = default;
            return false;
        }
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
