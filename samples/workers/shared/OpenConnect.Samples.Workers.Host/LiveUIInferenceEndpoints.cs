using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using OpenConnect.Agents.Execution;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Workers.Host;

public static class LiveUIInferenceEndpoints
{
    public static IEndpointRouteBuilder MapOpenConnectLiveUIInference(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/openconnect/v1/liveui/inference").RequireAuthorization();
        group.MapGet("worker/availability", async (IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var worker = services.GetService<LocalWorkerLiveUIInferenceProvider>();
            if (worker is null)
                return Results.Ok(new LiveUIInferenceAvailability(LiveUIInferenceProviderKind.LocalWorker,
                    LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "worker_not_configured"));
            return Results.Ok(await worker.GetAvailabilityAsync(cancellationToken));
        }).RequireRateLimiting("liveui-inference");

        group.MapGet("planner/availability", (IConfiguration configuration) =>
            Results.Ok(new LiveUIInferenceAvailability(LiveUIInferenceProviderKind.Gateway,
                configuration.GetValue<bool>("OpenConnect:LiveUI:ExternalPlanner:Enabled")
                    ? LiveUIInferenceAvailabilityStatus.Available : LiveUIInferenceAvailabilityStatus.Unavailable)))
            .RequireRateLimiting("liveui-inference");
        group.MapPost("plan", GenerateWorkerPlanAsync)
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024))
            .RequireRateLimiting("liveui-inference");
        group.MapPost("planner/plan", GenerateGatewayPlanAsync)
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024))
            .RequireRateLimiting("liveui-inference");
        return endpoints;
    }

    private static Task<IResult> GenerateWorkerPlanAsync(
        GatewayLiveUIInferenceRequest envelope, HttpContext context, InMemorySessionStore sessions,
        IPrivacyFirewall firewall, IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken) =>
        GeneratePlanAsync(envelope, context, sessions, firewall, services, configuration, false, cancellationToken);

    private static Task<IResult> GenerateGatewayPlanAsync(
        GatewayLiveUIInferenceRequest envelope, HttpContext context, InMemorySessionStore sessions,
        IPrivacyFirewall firewall, IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken) =>
        GeneratePlanAsync(envelope, context, sessions, firewall, services, configuration, true, cancellationToken);

    private static async Task<IResult> GeneratePlanAsync(
        GatewayLiveUIInferenceRequest envelope, HttpContext context, InMemorySessionStore sessions,
        IPrivacyFirewall firewall, IServiceProvider services, IConfiguration configuration, bool gatewayPlanner,
        CancellationToken cancellationToken)
    {
        if (gatewayPlanner && !configuration.GetValue<bool>("OpenConnect:LiveUI:ExternalPlanner:Enabled"))
            return Results.NotFound();
        if (envelope is null || envelope.Request is null ||
            (!(Guid.TryParseExact(envelope.RequestId, "D", out var requestId) ||
               Guid.TryParseExact(envelope.RequestId, "N", out requestId)) || requestId == Guid.Empty) ||
            !Guid.TryParseExact(envelope.ExecutionId, "N", out var executionId) || executionId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["The inference correlation envelope is invalid."] });

        var request = envelope.Request with { RequestId = envelope.RequestId, ExecutionId = envelope.ExecutionId };
        if (request.Intent is not { Length: > 0 and <= 8_000 } || request.SessionId is not { Length: > 0 and <= 26 } ||
            request.RouteId is not { Length: > 0 and <= 256 } || request.SchemaVersion != LiveUISchema.CurrentVersion ||
            request.ManifestHash is not { Length: > 0 and <= 128 } || request.Capabilities is null ||
            request.Capabilities.Count is < 1 or > 512 || request.Capabilities.Any(capability =>
                capability is null || capability.TargetId is not { Length: > 0 and <= 256 } ||
                capability.Label is { Length: > 256 } ||
                capability.Actions is null || capability.Actions.Count is < 1 or > 16 ||
                capability.Actions.Any(action => action is not { Length: > 0 and <= 32 })) ||
            request.Capabilities.Select(capability => capability.TargetId).Distinct(StringComparer.Ordinal).Count() != request.Capabilities.Count)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["The inference request is invalid."] });

        if (!TryParseIdentity(context.User, out var tenant, out var principal))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The authenticated identity is missing required tenant or subject claims.");

        Ulid sessionUlid;
        try { sessionUlid = Ulid.Parse(request.SessionId); }
        catch (FormatException)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not found.");
        }

        if (sessions.Get(sessionUlid) is not { } session || session.TenantId.Value != tenant || session.PrincipalId.Value != principal)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not found.");

        LiveUIInferenceResult result;
        LiveUIInferenceRequest validatedRequest;
        if (gatewayPlanner)
        {
            var contextForEgress = new EgressContext(session.TenantId, session.PrincipalId, session.Id, EgressDestination.ExternalModel);
            var guarded = new FirewallGuardedChatClient(services.GetRequiredService<IChatClient>(),
                services.GetRequiredService<IEgressGate>(), new FixedEgressContextProvider(contextForEgress));
            try
            {
                result = await new FirewallGuardedLiveUIGatewayInferenceProvider(guarded)
                    .GeneratePlanAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (PrivacyBlockedException)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Privacy policy blocked LiveUI gateway inference.",
                    type: "https://openconnect.dev/problems/privacy-blocked");
            }
            validatedRequest = request;
        }
        else
        {
            var egress = await firewall.EvaluateAsync(request.Intent,
                new EgressContext(new TenantId(tenant), new PrincipalId(principal), new SessionId(sessionUlid), EgressDestination.Worker),
                cancellationToken).ConfigureAwait(false);
            if (!egress.Allowed || egress.SanitizedContent is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Privacy policy blocked LiveUI worker inference.", type: "https://openconnect.dev/problems/privacy-blocked");
            var worker = services.GetService<LocalWorkerLiveUIInferenceProvider>();
            if (worker is null)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The LiveUI worker is not configured.");
            validatedRequest = request with { Intent = egress.SanitizedContent };
            result = await worker.GeneratePlanAsync(validatedRequest, cancellationToken).ConfigureAwait(false);
        }

        if (result.Plan is { } plan &&
            (plan.Actions is null || plan.Actions.Any(action => action is null || action.Type is null || action.TargetId is null) ||
             !new LiveUIActionPlanValidator().Validate(validatedRequest, plan, 8, out _)))
            result = new(gatewayPlanner ? LiveUIInferenceProviderKind.Gateway : LiveUIInferenceProviderKind.LocalWorker, null, "invalid_plan");

        return Results.Ok(new GatewayLiveUIInferenceResponse(envelope.RequestId, envelope.ExecutionId, result));
    }

    private static bool TryParseIdentity(ClaimsPrincipal principal, out Ulid tenant, out Ulid subject)
    {
        try
        {
            tenant = Ulid.Parse(principal.FindFirstValue("tenant_id") ?? string.Empty);
            subject = Ulid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? string.Empty);
            return tenant != default && subject != default;
        }
        catch (FormatException)
        {
            tenant = default;
            subject = default;
            return false;
        }
    }
}
