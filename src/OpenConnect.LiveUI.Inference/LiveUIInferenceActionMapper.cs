using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Maps validated inference proposals to the existing typed LiveUI execution contract.</summary>
public sealed class LiveUIInferenceActionMapper(LiveUIActionPlanValidator validator)
{
    public IReadOnlyList<LiveUIAction> Map(
        LiveUIInferenceRequest request,
        LiveUIActionPlan plan,
        LiveUIManifest manifest,
        TimeSpan actionTimeout,
        int maximumActions = 8)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!validator.Validate(request, plan, maximumActions, out var reason))
            throw new ArgumentException($"The inference plan is invalid: {reason}.", nameof(plan));
        if (!string.IsNullOrEmpty(request.ManifestHash) && !string.Equals(request.ManifestHash, manifest.ContentHash, StringComparison.Ordinal))
            throw new ArgumentException("The manifest changed after planning.", nameof(manifest));
        if (actionTimeout <= TimeSpan.Zero || actionTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(actionTimeout));
        var route = manifest.Routes.SingleOrDefault(candidate => candidate.Id == request.RouteId)
            ?? throw new ArgumentException("The request route is not present in the manifest.", nameof(request));

        return plan.Actions.Select(proposal =>
        {
            var navigate = proposal.Type == "navigate";
            var target = navigate ? null : route.Targets.SingleOrDefault(candidate => candidate.Id == proposal.TargetId);
            var destination = navigate ? manifest.Routes.SingleOrDefault(candidate => candidate.Id == proposal.TargetId) : null;
            if (navigate && destination is null || !navigate && target is null)
                throw new ArgumentException("The plan references an unknown route or target.", nameof(plan));
            var type = proposal.Type switch
            {
                "activate" when target!.Kind == LiveUITargetKind.Tab => LiveUIActionType.SelectTab,
                "activate" => LiveUIActionType.Click,
                "navigate" => LiveUIActionType.Navigate,
                "setValue" => LiveUIActionType.SetValue,
                "select" => LiveUIActionType.Select,
                "highlight" => LiveUIActionType.Highlight,
                "focus" => LiveUIActionType.Focus,
                "scroll" => LiveUIActionType.Scroll,
                _ => throw new ArgumentException("The plan contains an unsupported action.", nameof(plan))
            };
            return new LiveUIAction(Guid.NewGuid().ToString("N"), plan.SchemaVersion, request.SessionId,
                manifest.ContentHash, route.Id, target?.Id, type,
                new LiveUIActionParameters(Value: proposal.Value, DestinationRouteId: destination?.Id), actionTimeout);
        }).ToArray();
    }
}
