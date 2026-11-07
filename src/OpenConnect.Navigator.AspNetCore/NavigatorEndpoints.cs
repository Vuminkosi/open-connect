using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OpenConnect.Navigator;
using OpenConnect.Abstractions;

namespace OpenConnect.Navigator.AspNetCore;

public static class NavigatorEndpoints
{
    public static IEndpointRouteBuilder MapOpenConnectNavigator(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/openconnect/v1/navigator").RequireAuthorization();
        group.MapGet("/targets", Targets);
        group.MapPost("/sessions", Create);
        group.MapGet("/sessions/{id}", Get);
        group.MapPost("/sessions/{id}/tours", Tour);
        group.MapPost("/sessions/{id}/commands", Command);
        group.MapPost("/sessions/{id}/approvals", Approval);
        group.MapPost("/sessions/{id}/commands/{commandId}/execute", ExecuteApproved);
        group.MapGet("/sessions/{id}/events", Events);
        return endpoints;
    }

    private static IResult Targets(string? routeId, HttpContext context, NavigatorTargetRegistry registry)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (string.IsNullOrWhiteSpace(routeId) || routeId.Length > 256) return Results.BadRequest();
        var capabilities = context.User.FindAll("capability").Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        var scope = new NavigatorTargetScope(tenant, principal, routeId, capabilities);
        return Results.Ok(registry.ListByRoute(routeId, scope));
    }

    private static IResult Create(CreateSessionRequest? request, HttpContext context, INavigatorSessionStore store)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (request is null || string.IsNullOrWhiteSpace(request.ClientInstance) || request.ClientInstance.Length > 128 ||
            string.IsNullOrWhiteSpace(request.RouteId) || request.RouteId.Length > 256) return Results.BadRequest();
        var session = store.Create(tenant, principal, request.ClientInstance, request.RouteId);
        return Results.Created($"/openconnect/v1/navigator/sessions/{session.Id}", new
        {
            sessionId = session.Id, tenantId = session.Tenant, principalId = session.Principal,
            state = "created"
        });
    }

    private static IResult Get(string id, HttpContext context, INavigatorSessionStore store)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        var session = store.Get(id, tenant, principal);
        return session is null ? Results.NotFound() : Results.Ok(new
        {
            sessionId = session.Id, tenantId = session.Tenant, principalId = session.Principal,
            state = "created"
        });
    }

    private static IResult Tour(string id, NavigatorTourPlan? plan, HttpContext context, INavigatorSessionStore store, IOptions<NavigatorOptions> options)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (store.Get(id, tenant, principal) is null) return Results.NotFound();
        if (plan is null) return Results.BadRequest();
        try { new NavigatorTourValidator(options.Value.Onboarding.MaxSteps).Validate(plan); }
        catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["tour"] = ["The tour is invalid or exceeds the allowed limits."] }); }
        return Results.Accepted(value: plan);
    }

    private static IResult Command(string id, CommandRequest? request, HttpContext context, INavigatorSessionStore store, NavigatorTargetRegistry registry)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (store.Get(id, tenant, principal) is null) return Results.NotFound();
        if (request?.Command is null) return Results.BadRequest();
        var session = store.Get(id, tenant, principal)!;
        var scope = new NavigatorTargetScope(tenant, principal, session.RouteId, new HashSet<string>());
        var target = registry.Resolve(request.Command.TargetId, scope).SingleOrDefault();
        if (target is null) return Results.Problem(statusCode: 403, title: "Command target is not authorized.");
        var risk = NavigatorRiskClassifier.Classify(request.Command.Action, true, true,
            string.Equals(target.Descriptor.Sensitivity, "sensitive", StringComparison.OrdinalIgnoreCase));
        if (risk == NavigatorActionRisk.Forbidden) return Results.Problem(statusCode: 403, title: "Command is not authorized.");
        if (risk is NavigatorActionRisk.Mutation or NavigatorActionRisk.Sensitive)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            if (!store.AddPending(id, request.Command, tenant, principal, expiresAt)) return Results.Conflict();
            return Results.Json(new { pendingCommandId = request.Command.CommandId, approvalRequired = true }, statusCode: 428);
        }
        return ExecuteCommand(id, request.Command, tenant, principal, store, registry);
    }

    private static IResult ExecuteApproved(string id, string commandId, HttpContext context, INavigatorSessionStore store, NavigatorTargetRegistry registry)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (store.Get(id, tenant, principal) is null) return Results.NotFound();
        var command = store.ConsumeApproved(id, commandId, tenant, principal, DateTimeOffset.UtcNow);
        if (command is null) return Results.Problem(statusCode: 403, title: "Approval is invalid, expired, or already used.");
        return ExecuteCommand(id, command, tenant, principal, store, registry);
    }

    private static IResult ExecuteCommand(string id, NavigatorCommand command, string tenant, string principal, INavigatorSessionStore store, NavigatorTargetRegistry registry)
    {
        var session = store.Get(id, tenant, principal)!;
        var scope = new NavigatorTargetScope(tenant, principal, session.RouteId, new HashSet<string>());
        var target = registry.Resolve(command.TargetId, scope).SingleOrDefault();
        if (target is null) return Results.Problem(statusCode: 403, title: "Command target is not authorized.");
        var now = DateTimeOffset.UtcNow;
        var grant = store.IssueGrant(id, command, tenant, principal, target, now);
        if (grant is null) return Results.Conflict();
        var normalizedArgs = System.Text.Json.JsonSerializer.Serialize(command.Args);
        if (!registry.TryValidate(grant, normalizedArgs, tenant, principal, id, command.ExpectedVersion, now, out _) ||
            !store.Execute(id, command, grant, now)) return Results.Problem(statusCode: 403, title: "Command is not authorized.");
        var argsHash = CommandGrant.ComputeArgsHash(command.Args);
        return Results.Ok(new
        {
            receipt = new
            {
                receiptId = Ulid.NewUlid().ToString(), commandId = command.CommandId,
                sessionId = id, principalId = principal, tenantId = tenant, argumentsHash = argsHash,
                outcome = "succeeded", issuedAt = now
            },
            grant = new
            {
                commandId = grant.CommandId, sessionId = grant.NavigatorSessionId,
                principalId = grant.Principal, tenantId = grant.Tenant, targetId = grant.TargetId,
                action = ToKebabCase(grant.Action.ToString()), argumentsHash = grant.ArgsHash,
                expiresAt = grant.ExpiresAt
            }
        });
    }

    private static IResult Approval(string id, ApprovalRequest? request, HttpContext context, INavigatorSessionStore store)
    {
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        if (store.Get(id, tenant, principal) is null) return Results.NotFound();
        if (request is null || string.IsNullOrWhiteSpace(request.CommandId) || string.IsNullOrWhiteSpace(request.ArgsHash)) return Results.BadRequest();
        // R-ID-005: approval records authorization only; execution consumes it separately exactly once.
        return store.ApprovePending(id, request.CommandId, request.ArgsHash, tenant, principal, DateTimeOffset.UtcNow)
            ? Results.Ok(new { approved = true }) : Results.Problem(statusCode: 403, title: "Approval is invalid or expired.");
    }

    /// <summary>Reads the session event stream; this endpoint never appends or mutates events.</summary>
    private static IResult Events(string id, long? after, HttpContext context, INavigatorSessionStore store)
    {
        if (after < 0) return Results.BadRequest();
        if (!Identity(context, out var tenant, out var principal)) return Forbidden();
        return store.Get(id, tenant, principal) is null ? Results.NotFound() : Results.Ok(store.Events(id, after ?? 0));
    }

    private static bool Identity(HttpContext context, out string tenant, out string principal)
    {
        tenant = context.User.FindFirstValue("tenant_id") ?? "";
        principal = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "";
        return IsUlid(tenant) && IsUlid(principal);
    }
    private static bool IsUlid(string value)
    {
        try { _ = Ulid.Parse(value); return true; }
        catch (FormatException) { return false; }
    }
    private static IResult Forbidden() => Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");

    private static string ToKebabCase(string value) => System.Text.RegularExpressions.Regex.Replace(
        value, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
}

public sealed record CreateSessionRequest(string ClientInstance, string RouteId);
public sealed record CommandRequest(NavigatorCommand Command);
public sealed record ApprovalRequest(string CommandId, string ArgsHash);
