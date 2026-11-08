namespace OpenConnect.Navigator;

public sealed record NavigatorTourStep(NavigatorTourStepType Type, string? TargetId = null, string? Text = null, NavigatorActionType? Action = null, TimeSpan? Timeout = null);
public sealed record NavigatorTourPlan(string TourId, int Version, IReadOnlyList<NavigatorTourStep> Steps, string RouteId, bool IsDynamic = false);

public sealed class NavigatorTourValidator(int maxSteps = 25)
{
    public const int AbsoluteMaxSteps = 25;
    public void Validate(NavigatorTourPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (maxSteps is < 1 or > AbsoluteMaxSteps || plan.Steps.Count == 0 || plan.Steps.Count > maxSteps)
            throw new ArgumentException("Tour step count is outside the configured bound.", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.TourId) || plan.Version < 1 || string.IsNullOrWhiteSpace(plan.RouteId))
            throw new ArgumentException("Tour identity and route are required.", nameof(plan));
        foreach (var step in plan.Steps)
        {
            if (!Enum.IsDefined(step.Type) || step.Action is { } action && !Enum.IsDefined(action)) throw new ArgumentException("Tour contains an unsupported step or action.", nameof(plan));
            if (step.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))) throw new ArgumentException("Step timeout exceeds its bound.", nameof(plan));
            if (step.Type is NavigatorTourStepType.Highlight or NavigatorTourStepType.Focus or NavigatorTourStepType.Locate or NavigatorTourStepType.Execute && string.IsNullOrWhiteSpace(step.TargetId))
                throw new ArgumentException("Target-based steps require a registered target ID.", nameof(plan));
            if (step.Type == NavigatorTourStepType.Execute && step.Action is not (NavigatorActionType.Navigate or NavigatorActionType.Observe or NavigatorActionType.Highlight or NavigatorActionType.Focus))
                throw new ArgumentException("Mutating actions require a separately authorized command grant.", nameof(plan));
        }
    }
}

public static class NavigatorToolFacade
{
    private static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string>
    {
        ["observe"] = "navigator.observe", ["find-target"] = "navigator.find-target", ["highlight"] = "navigator.highlight",
        ["start-tour"] = "navigator.start-tour", ["request-action"] = "navigator.request-action", ["capture-request"] = "navigator.capture-request", ["speak"] = "navigator.speak"
    };
    public static IReadOnlyList<string> GetTools(IReadOnlySet<string> grants, bool captureEnabled, bool speechEnabled) =>
        Tools.Where(x => grants.Contains(x.Key) && (x.Key != "capture-request" || captureEnabled) && (x.Key != "speak" || speechEnabled)).Select(x => x.Value).ToArray();
}

public static class NavigatorRiskClassifier
{
    public static NavigatorActionRisk Classify(NavigatorActionType action, bool registered, bool freshAuthorization, bool sensitive = false)
    {
        if (!Enum.IsDefined(action) || action is NavigatorActionType.Click or NavigatorActionType.Fill or NavigatorActionType.Select or NavigatorActionType.Toggle or NavigatorActionType.InvokeRegisteredAction && (!registered || !freshAuthorization))
            return NavigatorActionRisk.Forbidden;
        if (sensitive || action == NavigatorActionType.Capture) return NavigatorActionRisk.Sensitive;
        // Mutations are permitted only after the target is registered and authorization is fresh.
        // Unverified guesses must remain forbidden (R-UI-005).
        if (action is NavigatorActionType.Click or NavigatorActionType.Fill or NavigatorActionType.Select or NavigatorActionType.Toggle or NavigatorActionType.InvokeRegisteredAction)
            return NavigatorActionRisk.Mutation;
        return action switch
        {
            NavigatorActionType.Navigate or NavigatorActionType.Scroll => NavigatorActionRisk.Navigation,
            NavigatorActionType.Focus => NavigatorActionRisk.Interaction,
            _ => NavigatorActionRisk.ReadOnly
        };
    }
}
