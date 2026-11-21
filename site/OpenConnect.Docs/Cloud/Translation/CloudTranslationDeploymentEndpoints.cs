using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationDeploymentEndpoints
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 4 };

    public static void MapCloudTranslationDeployment(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/translation/deployment-match", (HttpContext context, CancellationToken cancellationToken) =>
            MatchAsync(context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> MatchAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudTranslationAuthorization.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null) return error;
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(415);
        if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
        var buffer = new byte[4097];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) return Results.StatusCode(413);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) return Results.BadRequest();
            var request = document.RootElement.Deserialize<CloudTranslationDeploymentMatchRequest>(Options);
            if (request is null) return Results.BadRequest();
            var matched = await context.RequestServices.GetRequiredService<CloudTranslationDeploymentMatch>()
                .MatchesAsync(identity!.TenantId, request, cancellationToken);
            return Results.Json(new { matched });
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
