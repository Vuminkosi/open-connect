namespace OpenConnect.LiveUI;

public static class LiveUISchema
{
    public const string CurrentVersion = "openconnect.liveui/v1";
}

public enum LiveUITargetKind { Button, Link, Input, Select, Checkbox, Tab, Heading, Region, List, Table }
public enum LiveUICapability { Activate, Close, Focus, Highlight, Inspect, Scroll, SetValue, Select, WaitForState }
public enum LiveUIActionType { Navigate, Open, Close, Click, Focus, Scroll, Highlight, ClearHighlight, SetValue, Select, SelectTab, Inspect, WaitForState }
public enum LiveUIActionOutcome { Completed, Denied, Ambiguous, MissingTarget, Uncertain, Failed, Conflict }

public sealed record LiveUIManifest(string SchemaVersion, string BuildId, string ContentHash, IReadOnlyList<LiveUIRoute> Routes);
public sealed record LiveUIRoute(string Id, string Path, string? Title, IReadOnlyList<LiveUITarget> Targets);
public sealed record LiveUITarget(
    string Id,
    LiveUITargetKind Kind,
    string Label,
    IReadOnlySet<LiveUICapability> Capabilities,
    string? Role = null,
    string? Selector = null,
    string? ParentId = null,
    bool RequiresConfirmation = false);

/// <summary>Typed request. Authorization identity comes from the host policy, never from model output.</summary>
public sealed record LiveUIAction(
    string ActionId,
    string SchemaVersion,
    string SessionId,
    string ManifestHash,
    string RouteId,
    string? TargetId,
    LiveUIActionType Type,
    LiveUIActionParameters Parameters,
    TimeSpan Timeout);

public sealed record LiveUIActionParameters(
    string? Value = null,
    string? DestinationRouteId = null,
    string? StateName = null,
    string? StateValue = null,
    string? ScrollAlignment = null);

public sealed record LiveUIActionReceipt(
    string ActionId,
    string SessionId,
    LiveUIActionOutcome Outcome,
    string Code,
    bool PostconditionVerified,
    DateTimeOffset CompletedAt,
    string? VerifiedState = null);

public sealed record LiveUIExecutionContext(LiveUIManifest Manifest, LiveUIRoute Route, LiveUITarget? Target, LiveUIAction Action);
public sealed record LiveUIAuthorizationDecision(bool Allowed, string Code, bool UserConfirmed = false);
public sealed record LiveUIRuntimeResult(LiveUIActionOutcome Outcome, string Code, bool PostconditionVerified, string? VerifiedState = null);

public interface ILiveUIActionAuthorizer
{
    ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ILiveUIRuntime
{
    ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ILiveUIManifestProvider
{
    ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed class LiveUIManifestValidator
{
    public IReadOnlyList<string> Validate(LiveUIManifest? manifest)
    {
        if (manifest is null) return ["manifest_missing"];

        var errors = new List<string>();
        if (manifest.SchemaVersion != LiveUISchema.CurrentVersion) errors.Add("manifest_schema_unsupported");
        if (string.IsNullOrWhiteSpace(manifest.BuildId)) errors.Add("manifest_build_id_missing");
        if (string.IsNullOrWhiteSpace(manifest.ContentHash)) errors.Add("manifest_hash_missing");
        if (manifest.Routes is null || manifest.Routes.Count == 0)
        {
            errors.Add("manifest_routes_missing");
            return errors;
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var routePaths = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in manifest.Routes)
        {
            if (route is null || string.IsNullOrWhiteSpace(route.Id) || string.IsNullOrWhiteSpace(route.Path))
            {
                errors.Add("manifest_route_invalid");
                continue;
            }
            if (!routeIds.Add(route.Id)) errors.Add("manifest_route_id_duplicate");
            if (!routePaths.Add(route.Path)) errors.Add("manifest_route_path_duplicate");
            if (route.Targets is null)
            {
                errors.Add("manifest_targets_missing");
                continue;
            }

            foreach (var target in route.Targets)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.Label) ||
                    !Enum.IsDefined(target.Kind) || target.Capabilities is null || target.Capabilities.Any(capability => !Enum.IsDefined(capability)))
                {
                    errors.Add("manifest_target_invalid");
                    continue;
                }
                if (target.Selector is { Length: > 512 }) errors.Add("manifest_selector_invalid");
                if (!targetIds.Add(target.Id)) errors.Add("manifest_target_id_duplicate");
            }
        }
        return errors;
    }
}
