using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationDeploymentProfileEndpoints
{
    private sealed record WriteRequest(long DeploymentRevision, string TokenizerHash, IReadOnlyList<CloudTranslationLanguagePair> LanguagePairs);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 8 };

    public static void MapCloudTranslationDeploymentProfileEndpoints(this RouteGroupBuilder group)
    {
        const string path = "/operator/deployments/{deploymentId:guid}/translation-profile";
        group.MapGet(path, async (Guid deploymentId, ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators,
            CloudTranslationDeploymentProfiles profiles, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!operators.IsAllowed(principal))
                return Results.Forbid();
            try
            {
                var result = await profiles.GetAsync(deploymentId, cancellationToken);
                if (result is null)
                    return Results.NotFound();
                context.Response.Headers.ETag = $"\"{result.Revision}\"";
                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
        group.MapPut(path, async (Guid deploymentId, ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators,
            CloudTranslationDeploymentProfiles profiles, IAntiforgery antiforgery, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
            if (actor is null)
                return Results.Unauthorized();
            try
            {
                if (!operators.IsAllowed(principal))
                {
                    await profiles.RecordAsync(actor, traceId, deploymentId, "forbidden", true, cancellationToken);
                    return Results.Forbid();
                }
                if (!await antiforgery.IsRequestValidAsync(context) || !TryRevision(context, out var revision))
                {
                    await profiles.RecordAsync(actor, traceId, deploymentId, "invalid_request", false, cancellationToken);
                    return Results.BadRequest();
                }
                var request = await ReadAsync(context.Request, cancellationToken);
                if (request is null)
                {
                    await profiles.RecordAsync(actor, traceId, deploymentId, "invalid_request", false, cancellationToken);
                    return Results.BadRequest();
                }
                var status = await profiles.PutAsync(actor, traceId, new(deploymentId, request.DeploymentRevision, revision,
                    request.TokenizerHash, request.LanguagePairs), revision, cancellationToken);
                if (status == "succeeded")
                { context.Response.Headers.ETag = $"\"{revision + 1}\""; return Results.NoContent(); }
                await profiles.RecordAsync(actor, traceId, deploymentId, status, false, cancellationToken);
                return Results.Problem(statusCode: status == "conflict" ? 409 : 422, title: "Translation deployment profile could not be applied.",
                    extensions: new Dictionary<string, object?> { ["code"] = status, ["traceId"] = traceId });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.Problem(statusCode: 503, title: "Translation deployment profile is unavailable."); }
        });
    }

    private static bool TryRevision(HttpContext context, out long revision)
    {
        revision = 0;
        var values = context.Request.Headers.IfMatch;
        if (values.Count != 1)
            return false;
        var value = values.ToString();
        return value.Length > 2 && value[0] == '"' && value[^1] == '"' &&
            long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) &&
            revision >= 0 && revision < long.MaxValue;
    }

    private static async Task<WriteRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 32768)
            return null;
        var buffer = new byte[32769];
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
            if (length > 32768)
                return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object && Unique(document.RootElement)
                ? document.RootElement.Deserialize<WriteRequest>(Options) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!Unique(item))
                    return false;
        return true;
    }
}
