using System.Net.Http.Headers;

namespace OpenConnect.Docs.Cloud;

internal static class CloudTranslationAuthorization
{
    internal static async Task<(CloudCredentialIdentity? Identity, IResult? Error)> AuthorizeAsync(HttpContext context,
        CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return (null, Results.Unauthorized());
        if (!databaseConfigured) return (null, Results.StatusCode(503));
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return (null, Results.Unauthorized());
        try
        {
            var result = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "translation.translate", cancellationToken);
            return result.Authorized && result.Identity is not null ? (result.Identity, null) :
                (null, Results.StatusCode(result.Code switch { "unauthorized" => 401, "capability_unavailable" => 503, _ => 403 }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return (null, Results.StatusCode(503)); }
    }
}
