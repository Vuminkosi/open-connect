using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Hosting.AspNetCore.Execution;

[assembly: InternalsVisibleTo("OpenConnect.Hosting.Tests")]

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Maps the versioned OpenConnect HTTP API.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Maps OpenConnect endpoints under <c>/openconnect/v1</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnect(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/openconnect/v1");

        group.MapPost("/tasks", async (HttpContext context, TaskRequest request, CancellationToken cancellationToken) =>
        {
            var store = context.RequestServices.GetService<InMemoryTaskStore>();
            return store is null
                ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Task execution is not configured. Register bounded task execution to enable task submission.")
                : await HandleCreateTaskAsync(context, request, store, cancellationToken);
        }).RequireAuthorization();
        group.MapGet("/tasks/{id}", HandleGetTaskAsync).RequireAuthorization();
        group.MapPost("/tasks/{id}/cancel", HandleCancelTaskAsync).RequireAuthorization();
        group.MapPost("/sessions", HandleCreateSession).RequireAuthorization();
        group.MapGet("/sessions/{id}", HandleGetSession).RequireAuthorization();
        group.MapGet("/sessions/{id}/events", HandleGetSessionEvents).RequireAuthorization();
        group.MapGet("/capabilities", HandleCapabilities).RequireAuthorization();
        group.MapGet("/components", HandleComponents).RequireAuthorization();
        group.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        group.MapGet("/health/ready", HandleReady).RequireAuthorization();
        return endpoints;
    }

    internal static async Task<IResult> HandleCreateTaskAsync(HttpContext context, TaskRequest request, InMemoryTaskStore store, CancellationToken cancellationToken)
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

        if (request.Message?.Length > 32_000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["A message of at most 32000 characters is allowed."] });
        if (request.SessionId is not null && !UlidTryParse(request.SessionId, out _))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sessionId"] = ["A valid session ID is required."] });

        var services = context.RequestServices;
        if (services.GetService<IHostTaskExecutionDispatcher>() is not { } dispatcher ||
            services.GetService<IHostTaskExecutionAuthorizer>() is null || services.GetService<IHostTaskExecutor>() is null)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Task execution is not configured. Register an executor and authorizer to enable task submission.");
        if (request.SessionId is not null && (!UlidTryParse(request.SessionId, out var sessionId) ||
            services.GetService<InMemorySessionStore>()?.Get(sessionId) is not { } session || session.TenantId.Value != tenant || session.PrincipalId.Value != principal))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not found.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.ClientTaskId}\n{request.Message}\n{request.SessionId}")));
        var result = await dispatcher.AcceptAsync(request, key, fingerprint, new TenantId(tenant), new PrincipalId(principal), cancellationToken);
        return result.Status switch
        {
            ExecutionAdmissionStatus.Conflict => Results.Problem(statusCode: 409, title: "The idempotency key was already used with a different request.", type: "https://openconnect.dev/problems/idempotency-conflict"),
            ExecutionAdmissionStatus.Unavailable => Results.Problem(statusCode: 503, title: "Task execution is at capacity. Try again shortly."),
            _ => IdentifierJson(result.Task!, StatusCodes.Status202Accepted, $"/openconnect/v1/tasks/{result.Task!.Id.Value}")
        };
    }

    internal static async Task<IResult> HandleGetTaskAsync(string id, HttpContext context, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The task ID is invalid.");
        if (!UlidTryParse(context.User.FindFirstValue("tenant_id"), out var tenant) ||
            !UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var principal))
            return Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");
        var task = await store.GetAsync(value, cancellationToken);
        return task is null || task.TenantId.Value != tenant || task.PrincipalId.Value != principal
            ? Results.Problem(statusCode: 404, title: "Task not found.")
            : IdentifierJson(task, StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleCancelTaskAsync(string id, HttpContext context, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The task ID is invalid.");
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var existing = await store.GetAsync(value, cancellationToken);
        if (existing is null || existing.TenantId.Value != tenant || existing.PrincipalId.Value != principal)
            return Results.Problem(statusCode: 404, title: "Task not found.");
        var dispatcher = context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
        if (dispatcher is null) return Results.Problem(statusCode: 503, title: "Task execution is not configured.");
        var cancelled = await dispatcher.CancelAsync(value, cancellationToken);
        return cancelled is null ? Results.Json(null, options: OpenConnectHttpJson.CreateOptions()) : IdentifierJson(cancelled, StatusCodes.Status200OK);
    }

    internal static IResult HandleCreateSession(HttpContext context, SessionRequest? request, InMemorySessionStore store)
    {
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Create(new TenantId(tenant), new PrincipalId(principal));
        return IdentifierJson(session, StatusCodes.Status201Created, $"/openconnect/v1/sessions/{session.Id.Value}");
    }

    internal static IResult HandleGetSession(string id, HttpContext context, InMemorySessionStore store)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The session ID is invalid.");
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Get(value);
        return session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal
            ? Results.Problem(statusCode: 404, title: "Session not found.") : IdentifierJson(session, StatusCodes.Status200OK);
    }

    internal static IResult HandleGetSessionEvents(string id, long? after, HttpContext context, InMemorySessionStore store)
    {
        if (!UlidTryParse(id, out var value) || after < 0) return Results.Problem(statusCode: 400, title: "The session or event position is invalid.");
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Get(value);
        if (session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal) return Results.Problem(statusCode: 404, title: "Session not found.");
        return Results.Ok(store.GetEvents(value, after ?? 0));
    }

    private static bool TryIdentity(HttpContext context, out Ulid tenant, out Ulid principal)
    {
        var tenantValid = UlidTryParse(context.User.FindFirstValue("tenant_id"), out tenant);
        var principalValid = UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out principal);
        return tenantValid && principalValid;
    }

    private static IResult IdentityError() => Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");

    private static IResult IdentifierJson(object value, int statusCode, string? location = null) =>
        new IdentifierJsonResult(value, statusCode, location);

    internal static IResult HandleCapabilities(IServiceProvider services) => Results.Ok(services.GetService<ICapabilityRegistry>()?.GetReady(CapabilityKind.Model) ?? Array.Empty<CapabilityDescriptor>());

    internal static IResult HandleComponents(IServiceProvider services) => Results.Ok(services.GetService<IAgentDefinitionRegistry>()?.GetAll() ?? Array.Empty<AgentDefinition>());

    internal static IResult HandleReady(IServiceProvider services) => services.GetService<InMemoryTaskStore>() is not null && services.GetService<InMemorySessionStore>() is not null
        ? Results.Ok(new { status = "ready" }) : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service is not ready.");

    private static bool UlidTryParse(string? text, out Ulid value)
    {
        try
        {
            var parsed = Ulid.Parse(text ?? string.Empty);
            if (parsed == default || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            {
                value = default;
                return false;
            }

            value = parsed;
            return true;
        }
        catch (FormatException) { value = default; return false; }
    }

    private sealed class IdentifierJsonResult(object value, int statusCode, string? location) : IResult
    {
        private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            if (location is not null)
                httpContext.Response.Headers["Location"] = location;
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, value, value.GetType(), JsonOptions, httpContext.RequestAborted);
        }
    }
}

/// <summary>Task submission body. Tenant and principal are always taken from authenticated claims.</summary>
public sealed record TaskRequest(string ClientTaskId, string? Message = null, string? SessionId = null);
public sealed record SessionRequest(string? Name = null);
public sealed record SessionRecord(SessionId Id, TenantId TenantId, PrincipalId PrincipalId, DateTimeOffset CreatedAt);

/// <summary>Process-local task/idempotency store, replaceable by durable persistence.</summary>
public sealed class InMemoryTaskStore : ITaskLifecycleStore
{
    private readonly ConcurrentDictionary<Ulid, TaskRecord> _byId = new();

    internal Task<TaskRecord?> GetAsync(Ulid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _byId.TryGetValue(id, out var task);
        return Task.FromResult(task);
    }

    internal TaskRecord? Find(Ulid id) => _byId.GetValueOrDefault(id);
    internal IReadOnlyCollection<TaskRecord> All() => _byId.Values.ToArray();
    internal void Put(TaskRecord record) => _byId[record.Id.Value] = record;
    internal bool TryRemove(Ulid id) => _byId.TryRemove(id, out _);

    /// <inheritdoc />
    public Task<TaskRecord?> TransitionAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, AgentRunState expectedState, AgentRunState newState, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_byId.TryGetValue(taskId.Value, out var current))
        {
            if (current.TenantId != tenantId || current.PrincipalId != principalId || current.State != expectedState)
                return Task.FromResult<TaskRecord?>(null);
            var updated = current with { State = newState };
            if (_byId.TryUpdate(taskId.Value, updated, current)) return Task.FromResult<TaskRecord?>(updated);
        }
        return Task.FromResult<TaskRecord?>(null);
    }

    internal Task<TaskRecord?> CancelAsync(Ulid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_byId.TryGetValue(id, out var current))
        {
            if (current.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled) return Task.FromResult<TaskRecord?>(current);
            var updated = current with { State = AgentRunState.Cancelled };
            if (_byId.TryUpdate(id, updated, current)) return Task.FromResult<TaskRecord?>(updated);
        }
        return Task.FromResult<TaskRecord?>(null);
    }

}

/// <summary>Process-local session and bounded event store.</summary>
public sealed class InMemorySessionStore
{
    private const int EventLimit = 1000;
    private readonly ConcurrentDictionary<Ulid, SessionRecord> _sessions = new();
    private readonly InMemoryEventJournal _journal;

    public InMemorySessionStore() : this(new InMemoryEventJournal()) { }

    public InMemorySessionStore(InMemoryEventJournal journal) => _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public SessionRecord Create(TenantId tenant, PrincipalId principal)
    {
        var session = new SessionRecord(new SessionId(Ulid.NewUlid()), tenant, principal, DateTimeOffset.UtcNow);
        _sessions[session.Id.Value] = session;
        return session;
    }
    public SessionRecord? Get(Ulid id) => _sessions.GetValueOrDefault(id);
    internal int Count => _sessions.Count;
    internal bool Remove(Ulid id) => _sessions.TryRemove(id, out _);
    public IReadOnlyList<EventRecord> GetEvents(Ulid id, long afterSequence) => _journal.GetEvents(new SessionId(id), afterSequence);

    public Task<long> AppendEventAsync(Ulid id, string type, string data, CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(id)) throw new KeyNotFoundException("Session not found.");
        return _journal.AppendAsync(new EventRecord(new SessionId(id), 0, type, data), cancellationToken);
    }
}
