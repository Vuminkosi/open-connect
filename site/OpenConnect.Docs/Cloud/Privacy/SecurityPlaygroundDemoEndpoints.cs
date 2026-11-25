using System.Diagnostics;

namespace OpenConnect.Docs.Cloud;

/// <summary>
/// Thread-safe counters for the protected WAF and Service Fencing demo endpoints.
/// Counters only increment when an actual HTTP request reaches the backend route.
/// </summary>
public sealed class SecurityPlaygroundDemoCounters
{
    private long _wafBackendHits;
    private long _approvedServiceHits;

    public long WafBackendHits => Interlocked.Read(ref _wafBackendHits);
    public long ApprovedServiceHits => Interlocked.Read(ref _approvedServiceHits);

    public long IncrementWafBackendHits() => Interlocked.Increment(ref _wafBackendHits);
    public long IncrementApprovedServiceHits() => Interlocked.Increment(ref _approvedServiceHits);
}

/// <summary>
/// Maps narrow, bounded demo routes protected by the OWASP CRS WAF and Service Fencing checks (FW-060 / FW-100).
/// Never echoes raw request payloads, headers, or cookies.
/// </summary>
public static class SecurityPlaygroundDemoEndpoints
{
    public static void MapSecurityPlaygroundDemoEndpoints(this WebApplication app)
    {
        var wafGroup = app.MapGroup("/api/waf-demo");

        wafGroup.MapGet("/echo", (HttpContext context, SecurityPlaygroundDemoCounters counters) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var hits = counters.IncrementWafBackendHits();
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            return Results.Ok(new
            {
                ok = true,
                route = "/api/waf-demo/echo",
                hitCount = hits,
                traceId,
            });
        });

        wafGroup.MapPost("/echo", (HttpContext context, SecurityPlaygroundDemoCounters counters) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength is > 4096)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var hits = counters.IncrementWafBackendHits();
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            return Results.Ok(new
            {
                ok = true,
                route = "/api/waf-demo/echo",
                hitCount = hits,
                traceId,
            });
        });

        wafGroup.MapGet("/stats", (HttpContext context, SecurityPlaygroundDemoCounters counters) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                route = "/api/waf-demo/echo",
                hitCount = counters.WafBackendHits,
            });
        });

        app.MapGet("/api/fencing-demo/approved", (HttpContext context, SecurityPlaygroundDemoCounters counters) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var hits = counters.IncrementApprovedServiceHits();
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            return Results.Ok(new
            {
                ok = true,
                serviceId = "approved-demo-service",
                hitCount = hits,
                traceId,
            });
        });
    }
}
