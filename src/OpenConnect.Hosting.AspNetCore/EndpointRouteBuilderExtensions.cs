using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Maps the versioned OpenConnect HTTP API.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Maps OpenConnect endpoints under <c>/openconnect/v1</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnect(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/openconnect/v1");

        group.MapPost("/tasks", CreateTaskAsync).RequireAuthorization();
        group.MapGet("/tasks/{id}", GetTaskAsync).RequireAuthorization();
        group.MapPost("/tasks/{id}/cancel", (string id) => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Task cancellation is not available."))
            .RequireAuthorization();
        group.MapPost("/sessions", () => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Session creation is not available.")).RequireAuthorization();
        group.MapGet("/sessions/{id}", (string id) => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Session lookup is not available.")).RequireAuthorization();
        group.MapGet("/sessions/{id}/events", (string id, long? after) => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Session events are not available.")).RequireAuthorization();
        group.MapGet("/capabilities", () => Results.Ok(Array.Empty<CapabilityDescriptor>()));
        group.MapGet("/components", () => Results.Ok(Array.Empty<AgentDefinition>()));
        group.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        group.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
        return endpoints;
    }

    private static async Task<IResult> CreateTaskAsync(HttpContext context, TaskRequest request, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientTaskId) || request.ClientTaskId.Length > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clientTaskId"] = ["A client task ID of at most 128 characters is required."] });

        var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A valid Idempotency-Key header is required."] });

        var tenantClaim = context.User.FindFirstValue("tenant_id");
        var principalClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!UlidTryParse(tenantClaim, out var tenant) || !UlidTryParse(principalClaim, out var principal))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The authenticated identity is missing required tenant or subject claims.");

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ClientTaskId)));
        var result = await store.AcceptAsync(request.ClientTaskId, key, fingerprint, new TenantId(tenant), new PrincipalId(principal), cancellationToken);
        return result.Conflict
            ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The idempotency key was already used with a different request.", type: "https://openconnect.dev/problems/idempotency-conflict")
            : Results.Accepted($"/openconnect/v1/tasks/{result.Task!.Id.Value}", result.Task);
    }

    private static async Task<IResult> GetTaskAsync(string id, HttpContext context, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The task ID is invalid.");
        if (!UlidTryParse(context.User.FindFirstValue("tenant_id"), out var tenant) ||
            !UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var principal))
            return Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");
        var task = await store.GetAsync(value, cancellationToken);
        return task is null || task.TenantId.Value != tenant || task.PrincipalId.Value != principal
            ? Results.Problem(statusCode: 404, title: "Task not found.")
            : Results.Ok(task);
    }

    private static bool UlidTryParse(string? text, out Ulid value)
    {
        try { value = Ulid.Parse(text ?? string.Empty); return true; }
        catch (FormatException) { value = default; return false; }
    }
}

/// <summary>Task submission body. Tenant and principal are always taken from authenticated claims.</summary>
public sealed record TaskRequest(string ClientTaskId);

/// <summary>Process-local task/idempotency store, replaceable by durable persistence.</summary>
public sealed class InMemoryTaskStore
{
    private readonly ConcurrentDictionary<string, StoredTask> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Ulid, TaskRecord> _byId = new();

    internal Task<(TaskRecord? Task, bool Conflict)> AcceptAsync(string clientId, string key, string fingerprint, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storeKey = $"{tenant.Value}:{principal.Value}:{key}";
        while (true)
        {
            if (_byKey.TryGetValue(storeKey, out var existing))
                return Task.FromResult((existing.Fingerprint == fingerprint ? existing.Record : null, existing.Fingerprint != fingerprint));

            var record = new TaskRecord(new TaskId(Ulid.NewUlid()), tenant, principal, RunState.Queued);
            var stored = new StoredTask(fingerprint, record);
            if (!_byKey.TryAdd(storeKey, stored)) continue;
            _byId[record.Id.Value] = record;
            return Task.FromResult<(TaskRecord?, bool)>((record, false));
        }
    }

    internal Task<TaskRecord?> GetAsync(Ulid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _byId.TryGetValue(id, out var task);
        return Task.FromResult(task);
    }

    private sealed record StoredTask(string Fingerprint, TaskRecord Record);
}
