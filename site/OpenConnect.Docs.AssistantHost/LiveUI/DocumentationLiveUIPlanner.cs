using System.Security.Claims;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.AssistantHost.LiveUI;

internal interface IDocumentationWorkerPlanner
{
    Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);
    Task<LiveUIInferenceResult> GenerateAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken);
}

internal sealed class DocumentationWorkerPlanner(IServiceProvider services) : IDocumentationWorkerPlanner
{
    public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        services.GetService<LocalWorkerLiveUIInferenceProvider>()?.GetAvailabilityAsync(cancellationToken) ??
        Task.FromResult(new LiveUIInferenceAvailability(LiveUIInferenceProviderKind.LocalWorker,
            LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "worker_not_configured"));

    public Task<LiveUIInferenceResult> GenerateAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken) =>
        services.GetService<LocalWorkerLiveUIInferenceProvider>()?.GeneratePlanAsync(request, cancellationToken) ??
        Task.FromResult(new LiveUIInferenceResult(LiveUIInferenceProviderKind.LocalWorker, null, "worker_unavailable"));
}

internal sealed record DocumentationPlanResult(int Status, GatewayLiveUIInferenceResponse? Response = null);

internal sealed class DocumentationLiveUIPlanner(
    InMemorySessionStore sessions, IPrivacyFirewall firewall, IDocumentationWorkerPlanner worker)
{
    public async Task<DocumentationPlanResult> GenerateAsync(GatewayLiveUIInferenceRequest? envelope,
        ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope?.Request is not { } input ||
            (!(Guid.TryParseExact(envelope.RequestId, "D", out var requestId) || Guid.TryParseExact(envelope.RequestId, "N", out requestId)) || requestId == Guid.Empty) ||
            !Guid.TryParseExact(envelope.ExecutionId, "N", out var executionId) || executionId == Guid.Empty ||
            input.Intent is not { Length: > 0 and <= 8000 } || input.SessionId is not { Length: 26 } ||
            input.RouteId is not { Length: > 0 and <= 256 } || input.SchemaVersion != LiveUISchema.CurrentVersion ||
            input.ManifestHash is not { Length: > 0 and <= 128 } || input.Capabilities is null ||
            input.Capabilities.Count is < 1 or > 512 || input.Capabilities.Any(capability =>
                capability is null || capability.TargetId is not { Length: > 0 and <= 256 } || capability.Label is { Length: > 256 } ||
                capability.Actions is null || capability.Actions.Count is < 1 or > 16 ||
                capability.Actions.Any(action => action is not { Length: > 0 and <= 32 })) ||
            input.Capabilities.Select(capability => capability.TargetId).Distinct(StringComparer.Ordinal).Count() != input.Capabilities.Count)
            return new(StatusCodes.Status400BadRequest);

        Ulid tenant, subject, sessionId;
        try
        {
            tenant = Ulid.Parse(principal.FindFirstValue("tenant_id") ?? "");
            subject = Ulid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
            sessionId = Ulid.Parse(input.SessionId);
        }
        catch (FormatException) { return new(StatusCodes.Status404NotFound); }
        var session = sessions.Get(sessionId);
        if (session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != subject)
            return new(StatusCodes.Status404NotFound);
        if (!firewall.Enabled) return new(StatusCodes.Status403Forbidden);

        try
        {
            var request = input with { RequestId = envelope.RequestId, ExecutionId = envelope.ExecutionId };
            var screened = await LiveUIWorkerPrivacyScreen.PrepareAsync(request, firewall,
                new EgressContext(session.TenantId, session.PrincipalId, session.Id, EgressDestination.Worker), cancellationToken);
            var result = await worker.GenerateAsync(screened, cancellationToken);
            if (result.Provider != LiveUIInferenceProviderKind.LocalWorker)
                return new(StatusCodes.Status503ServiceUnavailable);
            if (result.Plan is { } plan && (result.Outcome != "completed" || plan.Actions is null || plan.Actions.Any(action => action is null ||
                action.Type is null || action.TargetId is null) ||
                !new LiveUIActionPlanValidator().Validate(screened, plan, 8, out _)))
                result = new(LiveUIInferenceProviderKind.LocalWorker, null, "invalid_plan");
            if (result.Plan is { } validPlan)
                await PrivacyJsonInspection.DemandUnchangedAsync(firewall, JsonSerializer.SerializeToElement(validPlan),
                    new EgressContext(session.TenantId, session.PrincipalId, session.Id, EgressDestination.Output), cancellationToken);
            return new(StatusCodes.Status200OK, new GatewayLiveUIInferenceResponse(envelope.RequestId, envelope.ExecutionId, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PrivacyBlockedException) { return new(StatusCodes.Status403Forbidden); }
        catch (Exception) { return new(StatusCodes.Status503ServiceUnavailable); }
    }
}
