using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudInferenceModelPolicyEndpoints
{
    private sealed record ModelWriteRequest(string[]? AllowedModels);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static void MapCloudInferenceModelPolicyEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        const string path = "/tenants/{tenantId:guid}/inference-model-policy";
        group.MapGet(path, async (Guid tenantId, ClaimsPrincipal principal, HttpContext context, CloudInferenceModelPolicyService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (actor is null)
                return Results.Unauthorized();
            var result = await service.GetAsync(actor, tenantId, cancellationToken);
            if (result.Policy is null)
                return Results.NotFound();
            context.Response.Headers.ETag = $"\"{result.Policy.Revision}\"";
            return Results.Ok(result.Policy);
        });
        group.MapPut(path, async (Guid tenantId, ClaimsPrincipal principal, HttpContext context, IAntiforgery antiforgery, CloudInferenceModelPolicyService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (actor is null)
                return Results.Unauthorized();
            if (!await antiforgery.IsRequestValidAsync(context) || !TryRevision(context, out var revision))
            {
                await service.RecordInvalidAsync(actor, tenantId, cancellationToken);
                return Results.BadRequest();
            }
            var request = await ReadAsync(context.Request, cancellationToken);
            if (request is null)
            { await service.RecordInvalidAsync(actor, tenantId, cancellationToken); return Results.BadRequest(); }
            var result = await service.PutAsync(actor, tenantId, request.AllowedModels, revision, cancellationToken);
            if (result.Policy is not null)
            {
                context.Response.Headers.ETag = $"\"{result.Policy.Revision}\"";
                return Results.Ok(result.Policy);
            }
            return result.Status switch
            {
                "not_found" => Results.NotFound(),
                "forbidden" => Results.StatusCode(403),
                "conflict" => Results.Conflict(),
                _ => Results.BadRequest(),
            };
        });
    }

    private static bool TryRevision(HttpContext context, out int revision)
    {
        revision = 0;
        var headers = context.Request.Headers.IfMatch;
        if (headers.Count != 1)
            return false;
        var value = headers.ToString();
        return value.Length > 2 && value[0] == '"' && value[^1] == '"' &&
            int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision >= 0 && revision < int.MaxValue;
    }

    private static async Task<ModelWriteRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 20000)
            return null;
        var buffer = new byte[20001];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0)
                    break;
                length += count;
            }
            if (length > 20000)
                return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    return null;
            return document.RootElement.Deserialize<ModelWriteRequest>(Options);
        }
        catch (JsonException) { return null; }
    }
}
