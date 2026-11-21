using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudCapabilityAvailabilityEndpoints
{
    private sealed record AvailabilityWriteRequest(string? Availability);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static void MapCloudCapabilityAvailabilityEndpoints(this RouteGroupBuilder group)
    {
        const string path = "/operator/capabilities/{capabilityId}/availability";
        group.MapGet(path, async (string capabilityId, ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators, CloudCapabilityAvailabilityService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!operators.IsAllowed(principal)) return Results.Forbid();
            var result = await service.GetAsync(capabilityId, cancellationToken);
            if (result is null) return Results.NotFound();
            context.Response.Headers.ETag = $"\"{result.Revision}\"";
            return Results.Ok(result);
        });
        group.MapPut(path, async (string capabilityId, ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators,
            CloudCapabilityAvailabilityService service, IAntiforgery antiforgery, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
            if (actor is null) return Results.Unauthorized();
            try
            {
                if (!operators.IsAllowed(principal))
                {
                    await service.RecordAsync(actor, traceId, capabilityId, "forbidden", true, cancellationToken);
                    return Results.Forbid();
                }
                if (!await antiforgery.IsRequestValidAsync(context) || !TryRevision(context, out var revision))
                {
                    await service.RecordAsync(actor, traceId, capabilityId, "invalid_request", false, cancellationToken);
                    return Problem("invalid_request", 400, traceId);
                }
                var request = await ReadAsync(context.Request, cancellationToken);
                if (request is null)
                {
                    await service.RecordAsync(actor, traceId, capabilityId, "invalid_request", false, cancellationToken);
                    return Problem("invalid_request", 400, traceId);
                }
                var result = await service.PutAsync(actor, traceId, capabilityId, request.Availability, revision, cancellationToken);
                if (result.Availability is not null)
                {
                    context.Response.Headers.ETag = $"\"{result.Availability.Revision}\"";
                    return Results.Ok(result.Availability);
                }
                return Problem(result.Status, result.Status switch { "not_found" => 404, "conflict" => 409, "deployment_required" => 409, "unavailable" => 503, _ => 422 }, traceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Problem("capability_unavailable", 503, traceId); }
        });
    }

    private static IResult Problem(string code, int status, string traceId) => Results.Problem(statusCode: status,
        title: "Capability availability request could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = traceId });

    private static bool TryRevision(HttpContext context, out long revision)
    {
        revision = 0;
        var headers = context.Request.Headers.IfMatch;
        if (headers.Count != 1) return false;
        var value = headers.ToString();
        return value.Length > 2 && value[0] == '"' && value[^1] == '"' &&
            long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0 && revision < long.MaxValue;
    }

    private static async Task<AvailabilityWriteRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 4096) return null;
        var buffer = new byte[4097];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) return null;
            return document.RootElement.Deserialize<AvailabilityWriteRequest>(Options);
        }
        catch (JsonException) { return null; }
    }
}
